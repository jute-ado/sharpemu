// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Core.Cpu;
using SharpEmu.Core.Loader;
using SharpEmu.Core.Memory;
using SharpEmu.HLE;
using Xunit;

namespace SharpEmu.Core.Tests;

public sealed class VirtualMemoryQueryTests
{
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
    public void TrackedMemoryForwardsVirtualRegionQueries()
    {
        var memory = new VirtualMemory();
        memory.Map(0x8000, 0x1000, 0, [], ProgramHeaderFlags.Read | ProgramHeaderFlags.Write);
        var tracked = new TrackedCpuMemory(memory);

        Assert.True(tracked.TryQueryMemoryRegion(0x8000, findNext: false, out var region));
        Assert.Equal(new GuestVirtualMemoryRegion(0x8000, 0x1000, 0x03), region);
    }

    [WindowsFact]
    public void PhysicalMemoryReportsAllocatedRegionProtection()
    {
        using var memory = new PhysicalVirtualMemory();
        var address = memory.AllocateAt(0, 0x2000, executable: false);

        Assert.True(memory.TryQueryMemoryRegion(address + 0x1000, findNext: false, out var region));
        Assert.Equal(address, region.Address);
        Assert.Equal(0x2000UL, region.Length);
        Assert.Equal(0x03, region.Protection);
    }

    [WindowsX64Fact]
    public void PhysicalMemorySnapshotsPageProtectionRuns()
    {
        using var memory = new PhysicalVirtualMemory();
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

    [WindowsX64Fact]
    public void PhysicalMemoryCoalescesUniformSegmentProtectionApplications()
    {
        using var memory = new PhysicalVirtualMemory();
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

    [WindowsX64Fact]
    public void PhysicalMemoryPreservesMergedProtectionRunBoundaries()
    {
        using var memory = new PhysicalVirtualMemory();
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
