// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Core.Cpu;
using SharpEmu.Core.Loader;
using SharpEmu.Core.Memory;
using SharpEmu.HLE;
using SharpEmu.HLE.Host;
using Xunit;

namespace SharpEmu.Core.Tests;

public sealed class VirtualMemoryQueryTests
{
    [Fact]
    public void EmptyVirtualMemoryHasNoContainingOrNextRegion()
    {
        var memory = new VirtualMemory();

        Assert.False(memory.TryQueryMemoryRegion(0, findNext: false, out _));
        Assert.False(memory.TryQueryMemoryRegion(0, findNext: true, out _));
        Assert.Equal(0, memory.CountRegionQueryProbes(0, findNext: true));
    }

    [Fact]
    public void VirtualMemoryReportsContainingAndNextRegions()
    {
        var memory = new VirtualMemory();
        memory.Map(
            0x3000,
            0x2000,
            0,
            new byte[0x100],
            ProgramHeaderFlags.Read | ProgramHeaderFlags.Execute);

        Assert.True(memory.TryQueryMemoryRegion(0x3800, findNext: false, out var containing));
        Assert.Equal(new GuestVirtualMemoryRegion(0x3000, 0x2000, 0x05), containing);

        Assert.True(memory.TryQueryMemoryRegion(0x2000, findNext: true, out var next));
        Assert.Equal(containing, next);
        Assert.False(memory.TryQueryMemoryRegion(0x2000, findNext: false, out _));
    }

    [Fact]
    public void VirtualMemoryQueryHandlesOrderedBoundariesAfterOutOfOrderMappings()
    {
        var memory = new VirtualMemory();
        memory.Map(0x9000, 0x100, 0, [], ProgramHeaderFlags.Read);
        memory.Map(0x3000, 0x100, 0, [], ProgramHeaderFlags.Read);
        memory.Map(0x6000, 0x100, 0, [], ProgramHeaderFlags.Read);

        Assert.True(memory.TryQueryMemoryRegion(0x30FF, findNext: false, out var containing));
        Assert.Equal(0x3000UL, containing.Address);
        Assert.False(memory.TryQueryMemoryRegion(0x3100, findNext: false, out _));
        Assert.True(memory.TryQueryMemoryRegion(0x3100, findNext: true, out var next));
        Assert.Equal(0x6000UL, next.Address);
        Assert.True(memory.TryQueryMemoryRegion(0x9000, findNext: true, out var last));
        Assert.Equal(0x9000UL, last.Address);
        Assert.False(memory.TryQueryMemoryRegion(0x9100, findNext: true, out _));
    }

    [Fact]
    public void VirtualMemoryRegionQueryUsesLogarithmicProbeDepth()
    {
        var memory = new VirtualMemory();
        const int regionCount = 1024;
        const ulong baseAddress = 0x1_0000;
        const ulong stride = 0x10;
        for (var index = 0; index < regionCount; index++)
        {
            memory.Map(
                baseAddress + ((ulong)index * stride),
                1,
                (ulong)index,
                [],
                ProgramHeaderFlags.Read);
        }

        var lastAddress = baseAddress + ((regionCount - 1) * stride);
        Assert.True(memory.TryQueryMemoryRegion(lastAddress, findNext: false, out var containing));
        Assert.Equal(lastAddress, containing.Address);
        Assert.InRange(memory.CountRegionQueryProbes(lastAddress, findNext: false), 1, 11);

        Assert.True(memory.TryQueryMemoryRegion(lastAddress - 1, findNext: true, out var next));
        Assert.Equal(lastAddress, next.Address);
        Assert.InRange(memory.CountRegionQueryProbes(lastAddress - 1, findNext: true), 1, 11);
    }

    [Fact]
    public void TrackedMemoryForwardsVirtualRegionQueries()
    {
        var memory = new VirtualMemory();
        memory.Map(0x8000, 0x1000, 0, [], ProgramHeaderFlags.Read | ProgramHeaderFlags.Write);
        var tracked = new TrackedCpuMemory(memory);

        Assert.True(tracked.TryQueryMemoryRegion(0x8000, findNext: false, out var region));
        Assert.Equal(new GuestVirtualMemoryRegion(0x8000, 0x1000, 0x03), region);
    }

    [Fact]
    public void PhysicalMemoryReportsAllocatedRegionProtection()
    {
        using var memory = TestHostMemory.CreatePhysicalMemory();
        var address = memory.AllocateAt(0, 0x2000, executable: false);

        Assert.True(memory.TryQueryMemoryRegion(address + 0x1000, findNext: false, out var region));
        Assert.Equal(address, region.Address);
        Assert.Equal(0x2000UL, region.Length);
        Assert.Equal(0x03, region.Protection);
    }

    [Fact]
    public void PhysicalMemoryRegionQueryUsesLogarithmicProbeDepth()
    {
        using var memory = new PhysicalVirtualMemory(new SparseAddressHostMemory());
        const int regionCount = 256;
        for (var index = 0; index < regionCount; index++)
        {
            _ = memory.AllocateAt(0, 0x1000, executable: false);
        }

        var last = memory.SnapshotRegions()[^1];
        Assert.True(memory.TryQueryMemoryRegion(last.VirtualAddress, findNext: false, out var containing));
        Assert.Equal(last.VirtualAddress, containing.Address);
        Assert.InRange(memory.CountRegionQueryProbes(last.VirtualAddress, findNext: false), 1, 9);

        Assert.True(memory.TryQueryMemoryRegion(last.VirtualAddress - 1, findNext: true, out var next));
        Assert.Equal(last.VirtualAddress, next.Address);
        Assert.InRange(memory.CountRegionQueryProbes(last.VirtualAddress - 1, findNext: true), 1, 9);
    }

    private sealed class SparseAddressHostMemory : IHostMemory
    {
        private const ulong Gap = 0x1000;
        private ulong _nextAddress = 0x0000_1000_0000_0000;

        public ulong Allocate(
            ulong desiredAddress,
            ulong size,
            HostPageProtection protection)
        {
            _ = protection;
            if (desiredAddress != 0)
            {
                return desiredAddress;
            }

            var result = _nextAddress;
            _nextAddress = checked(result + size + Gap);
            return result;
        }

        public ulong Reserve(
            ulong desiredAddress,
            ulong size,
            HostPageProtection protection) =>
            Allocate(desiredAddress, size, protection);

        public bool Commit(
            ulong address,
            ulong size,
            HostPageProtection protection) =>
            true;

        public bool Free(ulong address) => true;

        public bool Protect(
            ulong address,
            ulong size,
            HostPageProtection protection,
            out uint rawOldProtection)
        {
            rawOldProtection = 0;
            return true;
        }

        public bool ProtectRaw(
            ulong address,
            ulong size,
            uint rawProtection,
            out uint rawOldProtection)
        {
            rawOldProtection = 0;
            return true;
        }

        public bool Query(ulong address, out HostRegionInfo info)
        {
            info = default;
            return false;
        }

        public void FlushInstructionCache(ulong address, ulong size)
        {
        }
    }

    [Fact]
    public void PhysicalMemorySnapshotsPageProtectionRuns()
    {
        using var memory = TestHostMemory.CreatePhysicalMemory();
        var address = memory.AllocateAt(0, 0x4000, executable: true);
        memory.Map(
            address,
            0x1000,
            fileOffset: 0,
            fileData: [],
            ProgramHeaderFlags.Read | ProgramHeaderFlags.Execute);
        memory.Map(
            address + 0x1000,
            0x2000,
            fileOffset: 0,
            fileData: [],
            ProgramHeaderFlags.Read | ProgramHeaderFlags.Write);

        var regions = memory.SnapshotRegions();

        Assert.Collection(
            regions,
            region => AssertRegion(
                region,
                address,
                0x1000,
                ProgramHeaderFlags.Read | ProgramHeaderFlags.Execute),
            region => AssertRegion(
                region,
                address + 0x1000,
                0x2000,
                ProgramHeaderFlags.Read | ProgramHeaderFlags.Write),
            region => AssertRegion(
                region,
                address + 0x3000,
                0x1000,
                ProgramHeaderFlags.Read | ProgramHeaderFlags.Write | ProgramHeaderFlags.Execute));
    }

    [Fact]
    public void PhysicalMemoryCoalescesUniformSegmentProtectionApplications()
    {
        using var memory = TestHostMemory.CreatePhysicalMemory();
        var address = memory.AllocateAt(0, 0x40_0000, executable: true);
        var applicationsBeforeMap = memory.ProtectionApplicationCount;

        memory.Map(
            address,
            0x40_0000,
            fileOffset: 0,
            fileData: [],
            ProgramHeaderFlags.Read | ProgramHeaderFlags.Execute);

        Assert.Equal(applicationsBeforeMap + 2, memory.ProtectionApplicationCount);
        var region = Assert.Single(memory.SnapshotRegions());
        AssertRegion(
            region,
            address,
            0x40_0000,
            ProgramHeaderFlags.Read | ProgramHeaderFlags.Execute);
    }

    [Fact]
    public void PhysicalMemoryPreservesMergedProtectionRunBoundaries()
    {
        using var memory = TestHostMemory.CreatePhysicalMemory();
        var address = memory.AllocateAt(0, 0x4000, executable: true);
        memory.Map(
            address + 0x1000,
            0x1000,
            fileOffset: 0,
            fileData: [],
            ProgramHeaderFlags.Read | ProgramHeaderFlags.Execute);
        var applicationsBeforeMap = memory.ProtectionApplicationCount;

        memory.Map(
            address,
            0x4000,
            fileOffset: 0,
            fileData: [],
            ProgramHeaderFlags.Read | ProgramHeaderFlags.Write);

        Assert.Equal(applicationsBeforeMap + 4, memory.ProtectionApplicationCount);
        Assert.Collection(
            memory.SnapshotRegions(),
            region => AssertRegion(
                region,
                address,
                0x1000,
                ProgramHeaderFlags.Read | ProgramHeaderFlags.Write),
            region => AssertRegion(
                region,
                address + 0x1000,
                0x1000,
                ProgramHeaderFlags.Read | ProgramHeaderFlags.Write | ProgramHeaderFlags.Execute),
            region => AssertRegion(
                region,
                address + 0x2000,
                0x2000,
                ProgramHeaderFlags.Read | ProgramHeaderFlags.Write));
    }

    private static void AssertRegion(
        VirtualMemoryRegion region,
        ulong expectedAddress,
        ulong expectedSize,
        ProgramHeaderFlags expectedProtection)
    {
        Assert.Equal(expectedAddress, region.VirtualAddress);
        Assert.Equal(expectedSize, region.MemorySize);
        Assert.Equal(expectedProtection, region.Protection);
    }
}
