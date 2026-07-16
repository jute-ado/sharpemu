// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Core.Loader;
using SharpEmu.Core.Memory;
using SharpEmu.HLE;
using Xunit;

namespace SharpEmu.Core.Tests;

public sealed class VirtualMemoryContractTests
{
    private const int RegionSize = 0x4000;

    [Fact]
    public void ManagedMemorySatisfiesGuestMemoryContract()
    {
        using var fixture = CreateManagedFixture();

        AssertGuestMemoryContract(fixture);
    }

    [Fact]
    public void PhysicalMemorySatisfiesGuestMemoryContract()
    {
        using var fixture = CreatePhysicalFixture();

        AssertGuestMemoryContract(fixture);
    }

    private static void AssertGuestMemoryContract(MemoryFixture fixture)
    {
        var memory = fixture.Memory;
        var address = fixture.Address;
        byte[] initialPayload = [0x10, 0x20, 0x30, 0x40, 0x50];
        memory.Map(
            address,
            RegionSize,
            fileOffset: 0x80,
            initialPayload,
            ProgramHeaderFlags.Read | ProgramHeaderFlags.Write);

        var oracle = new byte[RegionSize];
        initialPayload.CopyTo(oracle, 0);
        var initialContents = new byte[RegionSize];
        Assert.True(memory.TryRead(address, initialContents));
        Assert.Equal(oracle, initialContents);

        Assert.True(memory.TryRead(address + RegionSize, Span<byte>.Empty));
        Assert.True(memory.TryWrite(address + RegionSize, ReadOnlySpan<byte>.Empty));
        Assert.True(memory.TryCompare(address + RegionSize, ReadOnlySpan<byte>.Empty));
        Assert.False(memory.TryRead(address + RegionSize - 1, new byte[2]));
        Assert.False(memory.TryWrite(address + RegionSize - 1, new byte[2]));
        Assert.False(memory.TryCompare(address + RegionSize - 1, new byte[2]));
        Assert.False(memory.TryRead(address - 1, Span<byte>.Empty));

        var random = new Random(0x5A17);
        for (var iteration = 0; iteration < 512; iteration++)
        {
            var length = random.Next(0, 129);
            var offset = random.Next(0, RegionSize - length + 1);
            var payload = new byte[length];
            random.NextBytes(payload);

            Assert.True(memory.TryWrite(address + (ulong)offset, payload));
            payload.CopyTo(oracle, offset);

            var actual = new byte[length];
            Assert.True(memory.TryRead(address + (ulong)offset, actual));
            Assert.Equal(payload, actual);
            Assert.True(memory.TryCompare(address + (ulong)offset, payload));
            if (length != 0)
            {
                actual[^1] ^= 0xFF;
                Assert.False(memory.TryCompare(address + (ulong)offset, actual));
            }
        }

        var finalContents = new byte[RegionSize];
        Assert.True(memory.TryRead(address, finalContents));
        Assert.Equal(oracle, finalContents);

        var query = Assert.IsAssignableFrom<IGuestVirtualMemoryQuery>(memory);
        Assert.True(query.TryQueryMemoryRegion(address + 0x1234, findNext: false, out var region));
        Assert.Equal(address, region.Address);
        Assert.Equal((ulong)RegionSize, region.Length);
        Assert.Equal(0x03, region.Protection);
        Assert.True(query.TryQueryMemoryRegion(address - 1, findNext: true, out var nextRegion));
        Assert.Equal(region, nextRegion);
        Assert.False(query.TryQueryMemoryRegion(address + RegionSize, findNext: true, out _));

        var stacks = Assert.IsAssignableFrom<IGuestStackMemory>(memory);
        stacks.RegisterStackRange(address + 0x2000, 0x800);
        stacks.RegisterStackRange(address + 0x2000, 0x800);
        Assert.True(stacks.TryGetStackRange(address + 0x2400, out var stackStart, out var stackEnd));
        Assert.Equal(address + 0x2000, stackStart);
        Assert.Equal(address + 0x2800, stackEnd);
        Assert.False(stacks.TryGetStackRange(address + 0x2800, out _, out _));

        Parallel.For(0, 8, worker =>
        {
            var payload = Enumerable.Repeat(checked((byte)(worker + 1)), 0x100).ToArray();
            Assert.True(memory.TryWrite(address + 0x3000UL + ((ulong)worker * 0x100), payload));
        });
        for (var worker = 0; worker < 8; worker++)
        {
            var actual = new byte[0x100];
            Assert.True(memory.TryRead(address + 0x3000UL + ((ulong)worker * 0x100), actual));
            Assert.All(actual, value => Assert.Equal(checked((byte)(worker + 1)), value));
        }

        var resetVersion = memory.ResetVersion;
        Assert.NotEqual(0UL, resetVersion);
        memory.Clear();
        Assert.NotEqual(resetVersion, memory.ResetVersion);
        Assert.Empty(memory.SnapshotRegions());
        Assert.False(memory.TryRead(address, new byte[1]));
        Assert.False(stacks.TryGetStackRange(address + 0x2400, out _, out _));
    }

    private static MemoryFixture CreateManagedFixture()
    {
        return new MemoryFixture(
            new VirtualMemory(),
            0x0000_0008_2000_0000,
            owner: null);
    }

    private static MemoryFixture CreatePhysicalFixture()
    {
        var memory = TestHostMemory.CreatePhysicalMemory();
        var address = memory.AllocateAt(0, RegionSize, executable: false);
        return new MemoryFixture(memory, address, memory);
    }

    private sealed class MemoryFixture(IVirtualMemory memory, ulong address, IDisposable? owner) : IDisposable
    {
        public IVirtualMemory Memory { get; } = memory;

        public ulong Address { get; } = address;

        public void Dispose() => owner?.Dispose();
    }
}
