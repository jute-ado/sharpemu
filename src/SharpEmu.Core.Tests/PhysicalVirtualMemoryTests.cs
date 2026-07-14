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
    private const uint MemCommit = 0x1000;
    private const uint MemReserve = 0x2000;
    private const uint MemRelease = 0x8000;
    private const uint PageNoAccess = 0x01;

    [WindowsX64Fact]
    public void AllocatedRegionSupportsBoundedReadWriteAndEmptyEndAccess()
    {
        using var memory = new PhysicalVirtualMemory();
        var address = memory.AllocateAt(0, 0x2000, executable: false);
        var payload = new byte[] { 1, 2, 3, 4 };

        Assert.True(memory.TryWrite(address + 0x100, payload));
        Span<byte> read = stackalloc byte[payload.Length];
        Assert.True(memory.TryRead(address + 0x100, read));
        Assert.Equal(payload, read.ToArray());
        Assert.True(memory.IsAccessible(address, 0x2000));
        Assert.True(memory.IsAccessible(address + 0x2000, 0));
        Assert.True(memory.TryRead(address + 0x2000, Span<byte>.Empty));
        Assert.True(memory.TryWrite(address + 0x2000, ReadOnlySpan<byte>.Empty));

        Span<byte> crossing = stackalloc byte[2];
        Assert.False(memory.TryRead(address + 0x1FFF, crossing));
        Assert.False(memory.TryWrite(address + 0x1FFF, crossing));
        Assert.False(memory.IsAccessible(address + 0x2000, 1));
    }

    [WindowsX64Fact]
    public void CompareMatchesMappedBytesWithoutCrossingRegionBounds()
    {
        using var memory = new PhysicalVirtualMemory();
        var address = memory.AllocateAt(0, 0x2000, executable: false);
        byte[] payload = [1, 2, 3, 4];
        Assert.True(memory.TryWrite(address + 0x100, payload));

        Assert.True(memory.TryCompare(address + 0x100, payload));
        Assert.False(memory.TryCompare(address + 0x100, [1, 2, 3, 5]));
        Assert.False(memory.TryCompare(address + 0x1FFF, [0, 0]));
        Assert.True(memory.TryCompare(address + 0x2000, []));
        Assert.False(memory.TryCompare(address + 0x2000, [0]));
    }

    [WindowsX64Fact]
    public void MappingCopiesPayloadZeroFillsSegmentAndPreservesAdjacentBytes()
    {
        using var memory = new PhysicalVirtualMemory();
        var address = memory.AllocateAt(0, 0x2000, executable: false);
        var initial = Enumerable.Repeat((byte)0xCC, 0x2000).ToArray();
        Assert.True(memory.TryWrite(address, initial));

        memory.Map(
            address + 0x100,
            0x500,
            fileOffset: 0x80,
            fileData: new byte[] { 1, 2, 3, 4 },
            ProgramHeaderFlags.Read | ProgramHeaderFlags.Execute);

        Span<byte> prefix = stackalloc byte[1];
        Assert.True(memory.TryRead(address + 0xFF, prefix));
        Assert.Equal(0xCC, prefix[0]);
        Span<byte> segment = stackalloc byte[0x500];
        Assert.True(memory.TryRead(address + 0x100, segment));
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, segment[..4].ToArray());
        Assert.All(segment[4..].ToArray(), value => Assert.Equal(0, value));
        Span<byte> suffix = stackalloc byte[1];
        Assert.True(memory.TryRead(address + 0x600, suffix));
        Assert.Equal(0xCC, suffix[0]);

        Assert.True(memory.TryQueryMemoryRegion(address + 0x100, findNext: false, out var region));
        Assert.Equal(0x05, region.Protection);
    }

    [WindowsX64Fact]
    public void GuestAllocatorHonorsAlignmentAndReturnsDistinctRanges()
    {
        using var memory = new PhysicalVirtualMemory();

        Assert.True(memory.TryAllocateGuestMemory(3, 0x1000, out var first));
        Assert.True(memory.TryAllocateGuestMemory(5, 0x4000, out var second));
        Assert.Equal(0UL, first & 0xFFF);
        Assert.Equal(0UL, second & 0x3FFF);
        Assert.True(second >= first + 3);
        Assert.True(memory.TryWrite(first, new byte[] { 1, 2, 3 }));
        Assert.True(memory.TryWrite(second, new byte[] { 4, 5, 6, 7, 8 }));
    }

    [WindowsX64Fact]
    public void GuestAllocatorRejectsInvalidRequests()
    {
        using var memory = new PhysicalVirtualMemory();

        Assert.False(memory.TryAllocateGuestMemory(0, 0x1000, out _));
        Assert.False(memory.TryAllocateGuestMemory(1, 0, out _));
        Assert.False(memory.TryAllocateGuestMemory(1, 3, out _));
    }

    [WindowsX64Fact]
    public void ClearReleasesRegionsAndAllowsFreshAllocation()
    {
        using var memory = new PhysicalVirtualMemory();
        var address = memory.AllocateAt(0, 0x1000, executable: false);
        Assert.True(memory.TryWrite(address, new byte[] { 1 }));
        var resetVersion = memory.ResetVersion;

        memory.Clear();

        Assert.NotEqual(resetVersion, memory.ResetVersion);
        Assert.Empty(memory.SnapshotRegions());
        Span<byte> one = stackalloc byte[1];
        Assert.False(memory.TryRead(address, one));
        var nextAddress = memory.AllocateAt(0, 0x1000, executable: false);
        Assert.True(memory.TryRead(nextAddress, one));
        Assert.Equal(0, one[0]);
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nint VirtualAlloc(nint address, nuint size, uint allocationType, uint protection);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool VirtualFree(nint address, nuint size, uint freeType);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nuint VirtualQuery(
        nint address,
        out MemoryBasicInformation64 information,
        nuint length);

    private static bool TryFindReservedPage(
        ulong address,
        ulong size,
        out ulong reservedAddress)
    {
        var end = checked(address + size);
        var cursor = address;
        while (cursor < end)
        {
            var information = QueryPage(cursor);
            var regionEnd = information.RegionSize > ulong.MaxValue - information.BaseAddress
                ? ulong.MaxValue
                : information.BaseAddress + information.RegionSize;
            if (information.State == MemReserve)
            {
                reservedAddress = Math.Max(cursor, information.BaseAddress);
                return reservedAddress < end;
            }

            if (regionEnd <= cursor)
            {
                break;
            }

            cursor = regionEnd;
        }

        reservedAddress = 0;
        return false;
    }

    private static MemoryBasicInformation64 QueryPage(ulong address)
    {
        Assert.NotEqual(
            0u,
            VirtualQuery(
                unchecked((nint)address),
                out var information,
                (nuint)Marshal.SizeOf<MemoryBasicInformation64>()));
        return information;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryBasicInformation64
    {
        public ulong BaseAddress;
        public ulong AllocationBase;
        public uint AllocationProtect;
        public uint Alignment1;
        public ulong RegionSize;
        public uint State;
        public uint Protect;
        public uint Type;
        public uint Alignment2;
    }

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
    public unsafe void GetPointerCommitsLazyReservedPageBeforeReturning()
    {
        using var memory = new PhysicalVirtualMemory();
        var address = memory.AllocateAt(0, OneGibibyte, executable: false);
        Assert.True(TryFindReservedPage(address, OneGibibyte, out var reservedAddress));
        Assert.Equal(MemReserve, QueryPage(reservedAddress).State);

        var pointer = memory.GetPointer(reservedAddress);

        Assert.NotEqual(0, (nint)pointer);
        Assert.Equal(MemCommit, QueryPage(reservedAddress).State);
        *(byte*)pointer = 0xA5;
        Span<byte> actual = stackalloc byte[1];
        Assert.True(memory.TryRead(reservedAddress, actual));
        Assert.Equal(0xA5, actual[0]);
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
    public void UnalignedRequestedAllocationPreservesGuestAddressAndTail()
    {
        var probe = VirtualAlloc(
            0,
            0x1_0000,
            MemReserve,
            PageNoAccess);
        Assert.NotEqual(0, probe);
        Assert.True(VirtualFree(probe, 0, MemRelease));

        using var memory = new PhysicalVirtualMemory();
        var desiredAddress = unchecked((ulong)probe) + 0x1000;
        var actualAddress = memory.AllocateAt(
            desiredAddress,
            0x2000,
            executable: false,
            allowAlternative: false);

        Assert.Equal(desiredAddress, actualAddress);
        Assert.True(memory.IsAccessible(desiredAddress, 0x2000));
        Assert.True(memory.TryWrite(desiredAddress + 0x1FFF, [0xA5]));
        Span<byte> tail = stackalloc byte[1];
        Assert.True(memory.TryRead(desiredAddress + 0x1FFF, tail));
        Assert.Equal(0xA5, tail[0]);
    }

    [WindowsX64Fact]
    public void AllocationSearchReturnsUnalignedGuestCandidateInsteadOfRoundedHostBase()
    {
        var probe = VirtualAlloc(
            0,
            0x1_0000,
            MemReserve,
            PageNoAccess);
        Assert.NotEqual(0, probe);
        Assert.True(VirtualFree(probe, 0, MemRelease));

        using var memory = new PhysicalVirtualMemory();
        var desiredAddress = unchecked((ulong)probe) + 0x1000;

        Assert.True(memory.TryAllocateAtOrAbove(
            desiredAddress,
            size: 0x2000,
            executable: false,
            alignment: 0x1000,
            out var actualAddress));
        Assert.Equal(desiredAddress, actualAddress);
        Assert.Single(memory.SnapshotRegions());
        Assert.True(memory.IsAccessible(actualAddress, 0x2000));
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
            memory.TryCompare(0x10000, new byte[1]));
        Assert.Throws<ObjectDisposedException>(() =>
            memory.TryWriteUInt64(0x10000, 0));
        Assert.Throws<ObjectDisposedException>(() =>
            memory.IsAccessible(0x10000, 1));
        Assert.Throws<ObjectDisposedException>(memory.Clear);

        memory.Dispose();
    }
}
