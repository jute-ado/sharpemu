// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Diagnostics;
using SharpEmu.HLE.Host;
using SharpEmu.HLE.Host.Posix;
using Xunit;

namespace SharpEmu.Libs.Tests;

public sealed class PosixHostMemoryQueryTests
{
    [Fact]
    public void OversizedAllocationReturnsFailureWithoutThrowing()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var memory = new PosixHostMemory();

        Assert.Equal(0UL, memory.Allocate(0, ulong.MaxValue, HostPageProtection.NoAccess));
        Assert.Equal(0UL, memory.Reserve(0, ulong.MaxValue, HostPageProtection.NoAccess));
        Assert.False(memory.Commit(1, ulong.MaxValue, HostPageProtection.ReadWrite));
        Assert.False(memory.Protect(
            0,
            ulong.MaxValue,
            HostPageProtection.ReadWrite,
            out _));
    }

    [Fact]
    public void CommitPastReservationFailsWithoutChangingPrefixProtection()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var pageSize = checked((ulong)Environment.SystemPageSize);
        var memory = new PosixHostMemory();
        var address = memory.Reserve(0, 2 * pageSize, HostPageProtection.NoAccess);
        Assert.NotEqual(0UL, address);

        try
        {
            Assert.False(memory.Commit(
                address + pageSize,
                2 * pageSize,
                HostPageProtection.ReadWrite));
            AssertRegion(
                memory,
                address + pageSize,
                address + pageSize,
                pageSize,
                HostRegionState.Reserved,
                HostPageProtection.NoAccess);
        }
        finally
        {
            Assert.True(memory.Free(address));
        }
    }

    [Fact]
    public void CommitSplitsReservationIntoCommitmentRuns()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var pageSize = checked((ulong)Environment.SystemPageSize);
        var memory = new PosixHostMemory();
        var address = memory.Reserve(
            0,
            3 * pageSize,
            HostPageProtection.ReadWrite);
        Assert.NotEqual(0UL, address);

        try
        {
            AssertRegion(
                memory,
                address,
                address,
                3 * pageSize,
                HostRegionState.Reserved,
                HostPageProtection.NoAccess);

            Assert.True(memory.Commit(
                address + pageSize,
                pageSize,
                HostPageProtection.ReadWrite));

            AssertRegion(
                memory,
                address,
                address,
                pageSize,
                HostRegionState.Reserved,
                HostPageProtection.NoAccess);
            AssertRegion(
                memory,
                address + pageSize,
                address + pageSize,
                pageSize,
                HostRegionState.Committed,
                HostPageProtection.ReadWrite);
            AssertRegion(
                memory,
                address + 2 * pageSize,
                address + 2 * pageSize,
                pageSize,
                HostRegionState.Reserved,
                HostPageProtection.NoAccess);
        }
        finally
        {
            Assert.True(memory.Free(address));
        }
    }

    [Fact]
    public void QueryReturnsContiguousProtectionRuns()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var pageSize = checked((ulong)Environment.SystemPageSize);
        var memory = new PosixHostMemory();
        var address = memory.Allocate(0, 3 * pageSize, HostPageProtection.ReadWrite);
        Assert.NotEqual(0UL, address);

        try
        {
            Assert.True(memory.Protect(
                address + pageSize,
                pageSize,
                HostPageProtection.ReadOnly,
                out var oldProtection), "Failed to protect the middle page read-only.");
            Assert.Equal(0x04U, oldProtection);

            AssertRegion(memory, address, address, pageSize, HostRegionState.Committed, HostPageProtection.ReadWrite);
            AssertRegion(memory, address + pageSize, address + pageSize, pageSize, HostRegionState.Committed, HostPageProtection.ReadOnly);
            AssertRegion(memory, address + 2 * pageSize, address + 2 * pageSize, pageSize, HostRegionState.Committed, HostPageProtection.ReadWrite);

            Assert.True(memory.Protect(
                address,
                pageSize,
                HostPageProtection.ReadOnly,
                out oldProtection), "Failed to protect the first page read-only.");
            Assert.Equal(0x04U, oldProtection);
            AssertRegion(memory, address, address, 2 * pageSize, HostRegionState.Committed, HostPageProtection.ReadOnly);

            Assert.True(memory.Protect(
                address,
                3 * pageSize,
                HostPageProtection.ReadWrite,
                out oldProtection), "Failed to restore the complete allocation to read-write.");
            Assert.Equal(0x02U, oldProtection);
            AssertRegion(memory, address, address, 3 * pageSize, HostRegionState.Committed, HostPageProtection.ReadWrite);
        }
        finally
        {
            Assert.True(memory.Free(address), "Failed to release the three-page allocation.");
        }
    }

    [Fact]
    public void QueryLargeUniformReservationDoesNotWalkEveryGuestPage()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        const ulong reservationSize = 0x80_0000_0000; // 512 GiB.
        var memory = new PosixHostMemory();
        var address = memory.Reserve(0, reservationSize, HostPageProtection.NoAccess);
        Assert.NotEqual(0UL, address);

        try
        {
            var stopwatch = Stopwatch.StartNew();
            Assert.True(memory.Query(address, out var region), "Failed to query the large reservation.");
            stopwatch.Stop();

            Assert.Equal(address, region.BaseAddress);
            Assert.Equal(reservationSize, region.RegionSize);
            Assert.Equal(HostRegionState.Reserved, region.State);
            Assert.Equal(HostPageProtection.NoAccess, region.Protection);
            Assert.True(
                stopwatch.Elapsed < TimeSpan.FromMilliseconds(100),
                $"A uniform 512 GiB region query took {stopwatch.Elapsed}; it should depend on sparse protection boundaries, not page count.");
        }
        finally
        {
            Assert.True(memory.Free(address), "Failed to release the large reservation.");
        }
    }

    private static void AssertRegion(
        IHostMemory memory,
        ulong queryAddress,
        ulong expectedBase,
        ulong expectedSize,
        HostRegionState expectedState,
        HostPageProtection expectedProtection)
    {
        Assert.True(memory.Query(queryAddress, out var region), $"Failed to query address 0x{queryAddress:X}.");
        Assert.Equal(expectedBase, region.BaseAddress);
        Assert.Equal(expectedSize, region.RegionSize);
        Assert.Equal(expectedState, region.State);
        Assert.Equal(expectedProtection, region.Protection);
    }
}
