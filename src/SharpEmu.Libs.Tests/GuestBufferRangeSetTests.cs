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
    public void MergeCoalescesOverlappingAndAdjacentAliases()
    {
        var merged = GuestBufferRangeSet.Merge(
        [
            new GuestBufferRange(0x300, 0x20),
            new GuestBufferRange(0x100, 0x80),
            new GuestBufferRange(0x180, 0x20),
            new GuestBufferRange(0x170, 0x20),
        ]);

        Assert.Equal(2, merged.Count);
        Assert.Equal(
            new GuestBufferRange(0x100, 0xA0),
            merged[0]);
        Assert.Equal(
            new GuestBufferRange(0x300, 0x20),
            merged[1]);
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

    [Theory]
    [InlineData(true, 3UL, 2UL, false, true)]
    [InlineData(true, 2UL, 2UL, false, false)]
    [InlineData(true, 0UL, 0UL, true, true)]
    [InlineData(false, 3UL, 2UL, true, false)]
    public void ReadOnlyMutationVersionsOnlyWhileReferenced(
        bool changed,
        ulong lastUse,
        ulong completed,
        bool open,
        bool expected)
    {
        Assert.Equal(
            expected,
            GuestBufferRangeSet.MustVersionReadOnlyMutation(
                changed,
                lastUse,
                completed,
                open));
    }

    [Theory]
    [InlineData(3UL, 0UL, 2UL, false, false)]
    [InlineData(3UL, 0UL, 2UL, true, true)]
    [InlineData(3UL, 3UL, 2UL, false, true)]
    [InlineData(3UL, 3UL, 3UL, true, false)]
    public void PersistentAccessWaitsForWriteHazards(
        ulong lastUse,
        ulong lastWrite,
        ulong completed,
        bool writable,
        bool expected)
    {
        Assert.Equal(
            expected,
            GuestBufferRangeSet.MustWaitForPersistentAccess(
                lastUse,
                lastWrite,
                completed,
                writable));
    }

}
