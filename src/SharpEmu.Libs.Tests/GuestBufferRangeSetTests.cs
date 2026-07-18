// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.VideoOut;
using Xunit;

namespace SharpEmu.Libs.Tests;

public sealed class GuestBufferRangeSetTests
{
    [Fact]
    public void CreationAlignsBaseAndPadsDescriptorEnd()
    {
        Assert.True(
            GuestBufferRangeSet.TryCreate(
                address: 0x12F,
                length: 5,
                out var range));

        Assert.Equal(0x100UL, range.Start);
        Assert.Equal(0x134UL, range.End);
    }

    [Theory]
    [InlineData(0UL, 4)]
    [InlineData(1UL, -1)]
    [InlineData(ulong.MaxValue - 1, 8)]
    public void CreationRejectsInvalidOrOverflowingRanges(
        ulong address,
        int length)
    {
        Assert.False(
            GuestBufferRangeSet.TryCreate(
                address,
                length,
                out _));
    }

    [Fact]
    public void ContainmentAndOverlapUseHalfOpenRanges()
    {
        var allocation = new GuestBufferRange(0x100, 0x100);

        Assert.True(
            GuestBufferRangeSet.Contains(
                allocation,
                new GuestBufferRange(0x180, 0x80)));
        Assert.False(
            GuestBufferRangeSet.Overlaps(
                allocation,
                new GuestBufferRange(0x200, 0x20)));
    }

    [Fact]
    public void OverlapClosureIncludesRangesReachedAfterExpansion()
    {
        var expanded = GuestBufferRangeSet.ExpandToOverlapClosure(
            new GuestBufferRange(0x1F0, 0x20),
            [
                new GuestBufferRange(0x300, 0x80),
                new GuestBufferRange(0x200, 0x110),
                new GuestBufferRange(0x180, 0x70),
                new GuestBufferRange(0x500, 0x40),
            ]);

        Assert.Equal(
            new GuestBufferRange(0x1F0, 0x190),
            expanded);
    }

    [Fact]
    public void EvictionUsesCompletedLeastRecentlyUsedAllocations()
    {
        var evictions = GuestBufferRangeSet.SelectEvictions(
        [
            CacheEntry(0x100, 0x40, lastUse: 3),
            CacheEntry(0x200, 0x40, lastUse: 1),
            CacheEntry(0x300, 0x40, lastUse: 2),
        ],
        [],
        completedSequence: 3,
        maximumBytes: 0x40);

        Assert.Equal([1, 2], evictions);
    }

    [Fact]
    public void EvictionProtectsCurrentInFlightAndOpenAllocations()
    {
        var evictions = GuestBufferRangeSet.SelectEvictions(
        [
            CacheEntry(0x100, 0x40, lastUse: 1),
            CacheEntry(0x200, 0x40, lastUse: 4),
            CacheEntry(0x300, 0x40, lastUse: 2, open: true),
        ],
        [new GuestBufferRange(0x110, 0x10)],
        completedSequence: 3,
        maximumBytes: 0);

        Assert.Empty(evictions);
    }

    [Fact]
    public void EvictionAllowsProtectedWorkingSetToExceedSoftBudget()
    {
        var evictions = GuestBufferRangeSet.SelectEvictions(
        [
            CacheEntry(0x100, 0x80, lastUse: 1),
            CacheEntry(0x300, 0x20, lastUse: 2),
        ],
        [new GuestBufferRange(0x100, 0x80)],
        completedSequence: 2,
        maximumBytes: 0x40);

        Assert.Equal([1], evictions);
    }

    [Fact]
    public void EvictionDoesNothingWhileCacheIsWithinBudget()
    {
        var evictions = GuestBufferRangeSet.SelectEvictions(
        [
            CacheEntry(0x100, 0x20, lastUse: 1),
            CacheEntry(0x200, 0x20, lastUse: 2),
        ],
        [],
        completedSequence: 2,
        maximumBytes: 0x40);

        Assert.Empty(evictions);
    }

    private static GuestBufferCacheEntry CacheEntry(
        ulong start,
        ulong length,
        ulong lastUse,
        bool open = false) =>
        new(
            new GuestBufferRange(start, length),
            lastUse,
            open);

}
