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
    public void QueryReturnsContiguousProtectionRuns()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        const ulong pageSize = 0x1000;
        var memory = new PosixHostMemory();
        var address = memory.Allocate(0, 3 * pageSize, HostPageProtection.ReadWrite);
        Assert.NotEqual(0UL, address);

        try
        {
            Assert.True(memory.Protect(
                address + pageSize,
                pageSize,
                HostPageProtection.ReadOnly,
                out var oldProtection));
            Assert.Equal(0x04U, oldProtection);

            AssertRegion(memory, address, address, pageSize, HostPageProtection.ReadWrite);
            AssertRegion(memory, address + pageSize, address + pageSize, pageSize, HostPageProtection.ReadOnly);
            AssertRegion(memory, address + 2 * pageSize, address + 2 * pageSize, pageSize, HostPageProtection.ReadWrite);

            Assert.True(memory.Protect(
                address,
                pageSize,
                HostPageProtection.ReadOnly,
                out oldProtection));
            Assert.Equal(0x04U, oldProtection);
            AssertRegion(memory, address, address, 2 * pageSize, HostPageProtection.ReadOnly);

            Assert.True(memory.Protect(
                address,
                3 * pageSize,
                HostPageProtection.ReadWrite,
                out oldProtection));
            Assert.Equal(0x02U, oldProtection);
            AssertRegion(memory, address, address, 3 * pageSize, HostPageProtection.ReadWrite);
        }
        finally
        {
            Assert.True(memory.Free(address));
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
            Assert.True(memory.Query(address, out var region));
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
            Assert.True(memory.Free(address));
        }
    }

    private static void AssertRegion(
        IHostMemory memory,
        ulong queryAddress,
        ulong expectedBase,
        ulong expectedSize,
        HostPageProtection expectedProtection)
    {
        Assert.True(memory.Query(queryAddress, out var region));
        Assert.Equal(expectedBase, region.BaseAddress);
        Assert.Equal(expectedSize, region.RegionSize);
        Assert.Equal(expectedProtection, region.Protection);
    }
}
