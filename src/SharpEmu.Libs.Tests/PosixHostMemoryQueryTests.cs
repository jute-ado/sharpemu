// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Diagnostics;
using SharpEmu.HLE;
using SharpEmu.HLE.Host;
using SharpEmu.HLE.Host.Posix;
using Xunit;

namespace SharpEmu.Libs.Tests;

public sealed unsafe class PosixHostMemoryQueryTests
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
        Assert.True(HostMemory.Alloc(
            null,
            nuint.MaxValue,
            HostMemory.MEM_RESERVE,
            HostMemory.PAGE_NOACCESS) is null);
        Assert.True(HostMemory.Alloc(
            (void*)1,
            nuint.MaxValue,
            HostMemory.MEM_COMMIT,
            HostMemory.PAGE_READWRITE) is null);
        Assert.False(HostMemory.Protect(
            null,
            nuint.MaxValue,
            HostMemory.PAGE_READWRITE,
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
                HostPageProtection.NoAccess);
        }
        finally
        {
            Assert.True(memory.Free(address));
        }

        var compatibilityAddress = HostMemory.Alloc(
            null,
            checked((nuint)(2 * pageSize)),
            HostMemory.MEM_RESERVE,
            HostMemory.PAGE_NOACCESS);
        Assert.True(compatibilityAddress is not null);
        try
        {
            var partialCommit = HostMemory.Alloc(
                (byte*)compatibilityAddress + pageSize,
                checked((nuint)(2 * pageSize)),
                HostMemory.MEM_COMMIT,
                HostMemory.PAGE_READWRITE);

            Assert.True(partialCommit is null);
            Assert.NotEqual(
                0U,
                HostMemory.Query(
                    (byte*)compatibilityAddress + pageSize,
                    out var compatibilityRegion));
            Assert.Equal(HostMemory.PAGE_NOACCESS, compatibilityRegion.Protect);
        }
        finally
        {
            Assert.True(HostMemory.Free(
                compatibilityAddress,
                0,
                HostMemory.MEM_RELEASE));
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

            AssertRegion(memory, address, address, pageSize, HostPageProtection.ReadWrite);
            AssertRegion(memory, address + pageSize, address + pageSize, pageSize, HostPageProtection.ReadOnly);
            AssertRegion(memory, address + 2 * pageSize, address + 2 * pageSize, pageSize, HostPageProtection.ReadWrite);

            Assert.True(memory.Protect(
                address,
                pageSize,
                HostPageProtection.ReadOnly,
                out oldProtection), "Failed to protect the first page read-only.");
            Assert.Equal(0x04U, oldProtection);
            AssertRegion(memory, address, address, 2 * pageSize, HostPageProtection.ReadOnly);

            Assert.True(memory.Protect(
                address,
                3 * pageSize,
                HostPageProtection.ReadWrite,
                out oldProtection), "Failed to restore the complete allocation to read-write.");
            Assert.Equal(0x02U, oldProtection);
            AssertRegion(memory, address, address, 3 * pageSize, HostPageProtection.ReadWrite);
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
        HostPageProtection expectedProtection)
    {
        Assert.True(memory.Query(queryAddress, out var region), $"Failed to query address 0x{queryAddress:X}.");
        Assert.Equal(expectedBase, region.BaseAddress);
        Assert.Equal(expectedSize, region.RegionSize);
        Assert.Equal(expectedProtection, region.Protection);
    }
}
