// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Runtime.InteropServices;
using SharpEmu.Core.Loader;
using SharpEmu.Core.Memory;
using Xunit;

namespace SharpEmu.Core.Tests;

public sealed class PhysicalVirtualMemoryTests
{
    private const ulong OneGibibyte = 0x4000_0000UL;
    private const ulong HostBlockerSize = 0x2000_0000UL;
    private const uint MemReserve = 0x2000;
    private const uint MemRelease = 0x8000;
    private const uint PageNoAccess = 0x01;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nint VirtualAlloc(nint address, nuint size, uint allocationType, uint protection);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool VirtualFree(nint address, nuint size, uint freeType);

    [Theory]
    [InlineData(OneGibibyte - 0x1000, false, false)]
    [InlineData(OneGibibyte, false, true)]
    [InlineData(2 * OneGibibyte, false, true)]
    [InlineData(8 * OneGibibyte, false, true)]
    [InlineData(OneGibibyte, true, false)]
    [InlineData(8 * OneGibibyte, true, false)]
    public void LargeNonExecutableMappingsUseLazyReservation(
        ulong alignedSize,
        bool executable,
        bool expected)
    {
        Assert.Equal(
            expected,
            PhysicalVirtualMemory.ShouldReserveWithoutCommit(alignedSize, executable));
    }

    [WindowsX64Fact]
    public void AllocationSearchSkipsLargeHostReservation()
    {
        var blocker = VirtualAlloc(
            0,
            (nuint)HostBlockerSize,
            MemReserve,
            PageNoAccess);
        Assert.NotEqual(0, blocker);

        try
        {
            using var memory = new PhysicalVirtualMemory();
            var desiredAddress = unchecked((ulong)blocker);

            Assert.True(memory.TryAllocateAtOrAbove(
                desiredAddress,
                size: 0x1_0000,
                executable: false,
                alignment: 0x1000,
                out var actualAddress));
            Assert.True(actualAddress >= desiredAddress + HostBlockerSize);
        }
        finally
        {
            Assert.True(VirtualFree(blocker, 0, MemRelease));
        }
    }

    [WindowsX64Fact]
    public void DisposedMemoryRejectsFurtherOperations()
    {
        var memory = new PhysicalVirtualMemory();
        memory.Dispose();

        Assert.Throws<ObjectDisposedException>(() =>
            memory.TryAllocateAtExact(0, 0x1000, executable: false, out _));
        Assert.Throws<ObjectDisposedException>(() =>
            memory.AllocateAt(0, 0x1000, executable: false));
        Assert.Throws<ObjectDisposedException>(() =>
            memory.TryAllocateAtOrAbove(
                0x10000,
                0x1000,
                executable: false,
                alignment: 0x1000,
                out _));
        Assert.Throws<ObjectDisposedException>(() =>
            memory.TryAllocateGuestMemory(0x1000, 0x1000, out _));
        Assert.Throws<ObjectDisposedException>(() =>
            memory.Map(
                0x10000,
                0x1000,
                fileOffset: 0,
                fileData: Array.Empty<byte>(),
                ProgramHeaderFlags.Read));
        Assert.Throws<ObjectDisposedException>(() =>
            memory.RegisterStackRange(0x10000, 0x1000));
        Assert.Throws<ObjectDisposedException>(() =>
            memory.TryGetStackRange(0x10000, out _, out _));
        Assert.Throws<ObjectDisposedException>(() => memory.SnapshotRegions());
        Assert.Throws<ObjectDisposedException>(() =>
            memory.TryQueryMemoryRegion(0x10000, findNext: false, out _));
        Assert.Throws<ObjectDisposedException>(() =>
            memory.TryRead(0x10000, new byte[1]));
        Assert.Throws<ObjectDisposedException>(() =>
            memory.TryWrite(0x10000, new byte[1]));
        Assert.Throws<ObjectDisposedException>(() =>
            memory.TryWriteUInt64(0x10000, 0));
        Assert.Throws<ObjectDisposedException>(() =>
            memory.IsAccessible(0x10000, 1));
        Assert.Throws<ObjectDisposedException>(memory.Clear);

        memory.Dispose();
    }
}
