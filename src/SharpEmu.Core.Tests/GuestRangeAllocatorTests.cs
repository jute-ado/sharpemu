// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Core.Memory;
using Xunit;

namespace SharpEmu.Core.Tests;

public sealed class GuestRangeAllocatorTests
{
    [Fact]
    public void RejectsInvalidAndOverlappingArenasWithoutMutation()
    {
        var allocator = new GuestRangeAllocator();

        Assert.False(allocator.TryAddArena(0x1000, 0, 0));
        Assert.False(allocator.TryAddArena(0x1000, 0x100, 0x101));
        Assert.False(allocator.TryAddArena(ulong.MaxValue - 0x7F, 0x100, 0));
        Assert.False(allocator.TryAllocate(1, 1, out _));
        Assert.True(allocator.TryAddArena(0x1000, 0x100, 0));
        Assert.False(allocator.TryAddArena(0x1080, 0x100, 0));
        Assert.True(allocator.TryAddArena(0x1100, 0x100, 0));
        Assert.False(allocator.TryAllocate(0, 1, out _));
        Assert.False(allocator.TryAllocate(1, 0, out _));
        Assert.False(allocator.TryAllocate(1, 3, out _));

        Assert.True(allocator.TryAllocate(0x20, 0x10, out var address));
        Assert.Equal(0x1000UL, address);
    }

    [Fact]
    public void ReusesAlignedSplitsAndCoalescesAdjacentFrees()
    {
        var allocator = new GuestRangeAllocator();
        Assert.True(allocator.TryAddArena(0x1000, 0x1000, 0));
        Assert.True(allocator.TryAllocate(0x80, 0x10, out var padding));
        Assert.True(allocator.TryAllocate(0x180, 0x10, out var freed));
        Assert.True(allocator.TryAllocate(0x100, 0x10, out var guard));
        Assert.True(allocator.TryFree(freed));

        Assert.True(allocator.TryAllocate(0x80, 0x100, out var aligned));
        Assert.True(allocator.TryAllocate(0x40, 0x10, out var prefix));
        Assert.Equal(freed + 0x80, aligned);
        Assert.Equal(freed, prefix);

        Assert.True(allocator.TryFree(aligned));
        Assert.True(allocator.TryFree(prefix));
        Assert.True(allocator.TryFree(padding));
        Assert.True(allocator.TryAllocate(0x200, 0x10, out var coalesced));
        Assert.Equal(padding, coalesced);
        Assert.True(guard >= coalesced + 0x200);
    }

    [Fact]
    public void FreeingTopAllocationsReclaimsTheUnusedArenaTail()
    {
        var allocator = new GuestRangeAllocator();
        Assert.True(allocator.TryAddArena(0x1000, 0x1000, 0x100));
        Assert.True(allocator.TryAllocate(0x100, 0x10, out var first));
        Assert.True(allocator.TryAllocate(0x100, 0x10, out var second));
        Assert.True(allocator.TryAllocate(0x100, 0x10, out var third));

        Assert.True(allocator.TryFree(first));
        Assert.True(allocator.TryFree(third));
        Assert.True(allocator.TryFree(second));

        Assert.True(allocator.TryAllocate(0xF00, 0x100, out var wholeUsableArena));
        Assert.Equal(first, wholeUsableArena);
    }

    [Fact]
    public void RewindingCurrentArenaDoesNotAbsorbAnAdjacentRetiredArena()
    {
        var allocator = new GuestRangeAllocator();
        Assert.True(allocator.TryAddArena(0x1000, 0x100, 0));
        Assert.True(allocator.TryAllocate(0x100, 1, out var retired));
        Assert.True(allocator.TryAddArena(0x1100, 0x100, 0));
        Assert.True(allocator.TryAllocate(0x20, 1, out var current));

        Assert.True(allocator.TryFree(retired));
        Assert.True(allocator.TryFree(current));

        Assert.True(allocator.TryAllocate(0x100, 1, out var reusedRetired));
        Assert.Equal(retired, reusedRetired);
        Assert.True(allocator.TryAllocate(0x100, 1, out var reusedCurrent));
        Assert.Equal(current, reusedCurrent);
    }

    [Fact]
    public void ReclaimsAlignmentPaddingAndRetiredArenaTail()
    {
        var allocator = new GuestRangeAllocator();
        Assert.True(allocator.TryAddArena(0x1000, 0x100, 0));
        Assert.True(allocator.TryAllocate(1, 1, out var first));
        Assert.True(allocator.TryAllocate(1, 0x10, out var aligned));

        Assert.True(allocator.TryAllocate(8, 1, out var padding));
        Assert.Equal(first + 1, padding);
        Assert.Equal(0x1010UL, aligned);

        Assert.True(allocator.TryAddArena(0x2000, 0x100, 0));
        Assert.True(allocator.TryAllocate(0x20, 1, out var retiredTail));
        Assert.Equal(0x1011UL, retiredTail);
    }

    [Fact]
    public void RejectsUnknownInteriorAndDoubleFrees()
    {
        var allocator = new GuestRangeAllocator();
        Assert.True(allocator.TryAddArena(0x1000, 0x100, 0));
        Assert.True(allocator.TryAllocate(0x20, 0x10, out var address));

        Assert.False(allocator.TryFree(0));
        Assert.False(allocator.TryFree(address + 1));
        Assert.False(allocator.TryFree(address + 0x20));
        Assert.True(allocator.TryFree(address));
        Assert.False(allocator.TryFree(address));
    }

    [Fact]
    public void ClearDropsAllAllocatorState()
    {
        var allocator = new GuestRangeAllocator();
        Assert.True(allocator.TryAddArena(0x1000, 0x100, 0));
        Assert.True(allocator.TryAllocate(0x20, 0x10, out var address));

        allocator.Clear();

        Assert.False(allocator.TryFree(address));
        Assert.False(allocator.TryAllocate(1, 1, out _));
        Assert.True(allocator.TryAddArena(0x1000, 0x100, 0));
        Assert.True(allocator.TryAllocate(0x20, 0x10, out var replacement));
        Assert.Equal(address, replacement);
    }

    [Fact]
    public void DeterministicAllocationSequenceNeverOverlapsActiveRanges()
    {
        const ulong arenaSize = 0x1000;
        var allocator = new GuestRangeAllocator();
        var arenas = new List<(ulong Start, ulong End)>();
        var active = new List<(ulong Address, ulong Size)>();
        var random = new Random(unchecked((int)0xA110CA7E));

        AddArena();
        for (var iteration = 0; iteration < 4000; iteration++)
        {
            if (active.Count != 0 && random.Next(100) < 43)
            {
                var freeIndex = random.Next(active.Count);
                Assert.True(allocator.TryFree(active[freeIndex].Address));
                active.RemoveAt(freeIndex);
                continue;
            }

            var size = (ulong)random.Next(1, 257);
            var alignment = 1UL << random.Next(0, 9);
            if (!allocator.TryAllocate(size, alignment, out var address))
            {
                AddArena();
                Assert.True(allocator.TryAllocate(size, alignment, out address));
            }

            Assert.Equal(0UL, address & (alignment - 1));
            Assert.Contains(
                arenas,
                arena => address >= arena.Start &&
                         address <= arena.End &&
                         size <= arena.End - address);
            Assert.DoesNotContain(
                active,
                allocation =>
                    address < allocation.Address + allocation.Size &&
                    allocation.Address < address + size);
            active.Add((address, size));
        }

        foreach (var allocation in active)
        {
            Assert.True(allocator.TryFree(allocation.Address));
        }

        void AddArena()
        {
            var start = 0x1_0000UL + ((ulong)arenas.Count * 0x2000UL);
            Assert.True(allocator.TryAddArena(start, arenaSize, 0));
            arenas.Add((start, start + arenaSize));
        }
    }
}
