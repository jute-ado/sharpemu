// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Collections.Concurrent;
using SharpEmu.Libs.VideoOut;
using Silk.NET.Vulkan;
using VkBuffer = Silk.NET.Vulkan.Buffer;
using Xunit;

namespace SharpEmu.Libs.Tests.VideoOut;

public sealed class VulkanHostBufferPoolTests
{
    [Fact]
    public void ReturnedAllocationCanBeRentedAgain()
    {
        var destroyed = new List<VulkanHostBufferAllocation>();
        using var pool = new VulkanHostBufferPool(1024, destroyed.Add);
        var key = new VulkanHostBufferPoolKey(BufferUsageFlags.StorageBufferBit, 256);
        var allocation = Allocation(1, 2, key);

        pool.Register(allocation);
        Assert.True(pool.Return(allocation.Buffer, allocation.Memory));
        Assert.Equal(256UL, pool.CachedBytes);

        Assert.True(pool.TryRent(key, out var rented));
        Assert.Equal(allocation, rented);
        Assert.Equal(0UL, pool.CachedBytes);
        Assert.Empty(destroyed);
    }

    [Fact]
    public void ReturnDestroysAllocationThatWouldExceedBudget()
    {
        var destroyed = new List<VulkanHostBufferAllocation>();
        using var pool = new VulkanHostBufferPool(256, destroyed.Add);
        var key = new VulkanHostBufferPoolKey(BufferUsageFlags.VertexBufferBit, 512);
        var allocation = Allocation(3, 4, key);

        pool.Register(allocation);

        Assert.True(pool.Return(allocation.Buffer, allocation.Memory));
        Assert.Equal(0UL, pool.CachedBytes);
        Assert.Equal([allocation], destroyed);
        Assert.False(pool.TryRent(key, out _));
    }

    [Fact]
    public void UnknownAllocationIsNotClaimedByPool()
    {
        using var pool = new VulkanHostBufferPool(1024, _ => { });

        Assert.False(pool.Return(new VkBuffer(9), new DeviceMemory(10)));
    }

    [Fact]
    public void ConcurrentReturnsAndRentsPreserveEveryAllocation()
    {
        const int allocationCount = 4096;
        var key = new VulkanHostBufferPoolKey(BufferUsageFlags.StorageBufferBit, 1);
        var allocations = Enumerable.Range(1, allocationCount)
            .Select(index => Allocation((ulong)index, (ulong)index, key))
            .ToArray();
        using var pool = new VulkanHostBufferPool(allocationCount, _ => { });

        foreach (var allocation in allocations)
        {
            pool.Register(allocation);
        }

        Parallel.ForEach(
            allocations,
            allocation => Assert.True(pool.Return(allocation.Buffer, allocation.Memory)));

        Assert.Equal((ulong)allocationCount, pool.CachedBytes);

        var rentedHandles = new ConcurrentBag<ulong>();
        Parallel.For(
            0,
            allocationCount,
            _ =>
            {
                Assert.True(pool.TryRent(key, out var allocation));
                rentedHandles.Add(allocation.Buffer.Handle);
            });

        Assert.Equal(0UL, pool.CachedBytes);
        Assert.Equal(
            allocations.Select(allocation => allocation.Buffer.Handle).Order(),
            rentedHandles.Order());
    }

    [Fact]
    public void DisposeAtomicallyRetiresPoolAndDestroysAllocationsOnce()
    {
        var destroyed = new List<VulkanHostBufferAllocation>();
        var key = new VulkanHostBufferPoolKey(BufferUsageFlags.StorageBufferBit, 16);
        var allocation = Allocation(1, 2, key);
        var pool = new VulkanHostBufferPool(16, destroyed.Add);
        pool.Register(allocation);
        Assert.True(pool.Return(allocation.Buffer, allocation.Memory));

        pool.Dispose();
        pool.Dispose();

        Assert.Equal([allocation], destroyed);
        Assert.Equal(0UL, pool.CachedBytes);
        Assert.False(pool.TryRent(key, out _));
        Assert.False(pool.Return(allocation.Buffer, allocation.Memory));
        Assert.Throws<ObjectDisposedException>(
            () => pool.Register(Allocation(3, 4, key)));
    }

    private static VulkanHostBufferAllocation Allocation(
        ulong buffer,
        ulong memory,
        VulkanHostBufferPoolKey key) =>
        new(new VkBuffer(buffer), new DeviceMemory(memory), key, 0);
}
