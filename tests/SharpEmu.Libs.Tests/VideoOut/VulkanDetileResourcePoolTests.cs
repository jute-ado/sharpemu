// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.VideoOut;
using Xunit;

namespace SharpEmu.Libs.Tests.VideoOut;

public sealed class VulkanDetileResourcePoolTests
{
    [Theory]
    [InlineData(0, 4096)]
    [InlineData(1, 4096)]
    [InlineData(4096, 4096)]
    [InlineData(4097, 8192)]
    [InlineData(64 * 1024 * 1024, 64 * 1024 * 1024)]
    [InlineData(64 * 1024 * 1024 + 1, 64 * 1024 * 1024 + 1)]
    public void BucketSizeRoundsPoolableBuffersAndLeavesLargeBuffersExact(
        ulong required,
        ulong expected)
    {
        Assert.Equal(
            expected,
            VulkanDetileResourceCapacity.BucketSize(required));
    }

    [Fact]
    public void TryRentChoosesSmallestFittingBundle()
    {
        var destroyed = new List<string>();
        var pool = CreatePool(destroyed, maxEntries: 4, maxBytes: 1_000);
        pool.Return("small", Capacity(10));
        pool.Return("large", Capacity(40));
        pool.Return("medium", Capacity(20));

        Assert.True(
            pool.TryRent(
                Capacity(15),
                out var resource,
                out var capacity));

        Assert.Equal("medium", resource);
        Assert.Equal(Capacity(20), capacity);
        Assert.Equal(2, pool.Count);
        Assert.Equal(50ul, pool.RetainedBytes);
        Assert.Empty(destroyed);
    }

    [Fact]
    public void TryRentDeclinesBundlesThatDoNotFitEveryBuffer()
    {
        var destroyed = new List<string>();
        var pool = CreatePool(destroyed, maxEntries: 2, maxBytes: 1_000);
        pool.Return(
            "wrong-shape",
            new VulkanDetileResourceCapacity(40, 1, 1, 1));

        Assert.False(
            pool.TryRent(
                new VulkanDetileResourceCapacity(20, 2, 1, 1),
                out _,
                out _));
        Assert.Equal(1, pool.Count);
    }

    [Fact]
    public void ReturnEvictsOldestBundleAtEntryLimit()
    {
        var destroyed = new List<string>();
        var pool = CreatePool(destroyed, maxEntries: 2, maxBytes: 1_000);
        pool.Return("first", Capacity(10));
        pool.Return("second", Capacity(20));

        pool.Return("third", Capacity(30));

        Assert.Equal(["first"], destroyed);
        Assert.Equal(2, pool.Count);
        Assert.Equal(50ul, pool.RetainedBytes);
    }

    [Fact]
    public void ReturnEvictsOldestBundlesUntilByteLimitFits()
    {
        var destroyed = new List<string>();
        var pool = CreatePool(destroyed, maxEntries: 4, maxBytes: 55);
        pool.Return("first", Capacity(10));
        pool.Return("second", Capacity(20));

        pool.Return("third", Capacity(40));

        Assert.Equal(["first", "second"], destroyed);
        Assert.Equal(1, pool.Count);
        Assert.Equal(40ul, pool.RetainedBytes);
    }

    [Fact]
    public void ReturnDestroysUnretainableOrOverflowingBundle()
    {
        var destroyed = new List<string>();
        var pool = CreatePool(destroyed, maxEntries: 4, maxBytes: 100);

        pool.Return("oversized", Capacity(101));
        pool.Return(
            "overflow",
            new VulkanDetileResourceCapacity(
                ulong.MaxValue,
                ulong.MaxValue,
                1,
                1));

        Assert.Equal(["oversized", "overflow"], destroyed);
        Assert.Equal(0, pool.Count);
        Assert.Equal(0ul, pool.RetainedBytes);
    }

    [Fact]
    public void ReturnDestroysBundleAbovePerBufferLimit()
    {
        var destroyed = new List<string>();
        var pool = CreatePool(
            destroyed,
            maxEntries: 4,
            maxBytes: 128 * 1024 * 1024);

        pool.Return(
            "large",
            new VulkanDetileResourceCapacity(
                VulkanDetileResourceCapacity.MaximumBucketBytes + 1,
                1,
                1,
                1));

        Assert.Equal(["large"], destroyed);
        Assert.Equal(0, pool.Count);
    }

    [Fact]
    public void ClearDestroysEveryRetainedBundleExactlyOnce()
    {
        var destroyed = new List<string>();
        var pool = CreatePool(destroyed, maxEntries: 4, maxBytes: 1_000);
        pool.Return("first", Capacity(10));
        pool.Return("second", Capacity(20));

        pool.Clear();
        pool.Clear();

        Assert.Equal(["first", "second"], destroyed);
        Assert.Equal(0, pool.Count);
        Assert.Equal(0ul, pool.RetainedBytes);
    }

    private static BoundedVulkanDetileResourcePool<string> CreatePool(
        List<string> destroyed,
        int maxEntries,
        ulong maxBytes) =>
        new(maxEntries, maxBytes, destroyed.Add);

    private static VulkanDetileResourceCapacity Capacity(ulong total) =>
        new(total, 0, 0, 0);
}
