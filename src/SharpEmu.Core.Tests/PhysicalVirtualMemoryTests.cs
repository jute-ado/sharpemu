// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Runtime.InteropServices;
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
}
