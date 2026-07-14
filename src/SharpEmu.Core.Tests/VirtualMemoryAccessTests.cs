// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Core.Loader;
using SharpEmu.Core.Memory;
using Xunit;

namespace SharpEmu.Core.Tests;

public sealed class VirtualMemoryAccessTests
{
    private const ulong BaseAddress = 0x4000;
    private const ulong RegionSize = 0x1000;

    [Fact]
    public void EmptyAccessAtMappedEndSucceedsWithoutChangingMemory()
    {
        var memory = CreateMappedMemory([1, 2, 3, 4]);

        Assert.True(memory.TryRead(BaseAddress + RegionSize, Span<byte>.Empty));
        Assert.True(memory.TryWrite(BaseAddress + RegionSize, ReadOnlySpan<byte>.Empty));

        Span<byte> contents = stackalloc byte[4];
        Assert.True(memory.TryRead(BaseAddress, contents));
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, contents.ToArray());
    }

    [Fact]
    public void NonemptyAccessAtMappedEndFails()
    {
        var memory = CreateMappedMemory([]);
        Span<byte> one = stackalloc byte[1];

        Assert.False(memory.TryRead(BaseAddress + RegionSize, one));
        Assert.False(memory.TryWrite(BaseAddress + RegionSize, one));
    }

    [Fact]
    public void CompareMatchesMappedBytesWithoutCrossingRegionBounds()
    {
        var memory = CreateMappedMemory([1, 2, 3, 4]);

        Assert.True(memory.TryCompare(BaseAddress, [1, 2, 3, 4]));
        Assert.False(memory.TryCompare(BaseAddress, [1, 2, 3, 5]));
        Assert.False(memory.TryCompare(BaseAddress + RegionSize - 1, [0, 0]));
        Assert.True(memory.TryCompare(BaseAddress + RegionSize, []));
        Assert.False(memory.TryCompare(BaseAddress + RegionSize, [0]));
    }

    [Fact]
    public void AccessRequiresMatchingRegionProtection()
    {
        var readOnly = new VirtualMemory();
        readOnly.Map(BaseAddress, 4, 0, [1, 2, 3, 4], ProgramHeaderFlags.Read);

        Assert.False(readOnly.TryWrite(BaseAddress, [0xAA]));
        Span<byte> readOnlyContents = stackalloc byte[4];
        Assert.True(readOnly.TryRead(BaseAddress, readOnlyContents));
        Assert.True(readOnly.TryCompare(BaseAddress, [1, 2, 3, 4]));
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, readOnlyContents.ToArray());

        var inaccessible = new VirtualMemory();
        inaccessible.Map(BaseAddress, 4, 0, [5, 6, 7, 8], ProgramHeaderFlags.None);
        byte[] destination = [0xCC, 0xCC, 0xCC, 0xCC];
        Assert.False(inaccessible.TryRead(BaseAddress, destination));
        Assert.False(inaccessible.TryCompare(BaseAddress, [5, 6, 7, 8]));
        Assert.False(inaccessible.TryWrite(BaseAddress, [0xAA]));
        Assert.Equal(new byte[] { 0xCC, 0xCC, 0xCC, 0xCC }, destination);
    }

    [Fact]
    public void CrossRegionAccessValidatesEveryProtectionBeforeMutation()
    {
        var memory = new VirtualMemory();
        memory.Map(BaseAddress, 4, 0, [1, 2, 3, 4], ProgramHeaderFlags.Read | ProgramHeaderFlags.Write);
        memory.Map(BaseAddress + 4, 4, 4, [5, 6, 7, 8], ProgramHeaderFlags.Read);
        byte[] destination = [0xCC, 0xCC];

        Assert.True(memory.TryRead(BaseAddress + 3, destination));
        Assert.Equal(new byte[] { 4, 5 }, destination);
        Assert.False(memory.TryWrite(BaseAddress + 3, [0xAA, 0xBB]));

        Span<byte> contents = stackalloc byte[8];
        Assert.True(memory.TryRead(BaseAddress, contents[..4]));
        Assert.True(memory.TryRead(BaseAddress + 4, contents[4..]));
        Assert.Equal(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 }, contents.ToArray());
    }

    [Fact]
    public void AccessSpansAdjacentRegionBoundary()
    {
        var memory = new VirtualMemory();
        memory.Map(BaseAddress, 4, 0, [1, 2, 3, 4], ProgramHeaderFlags.Read | ProgramHeaderFlags.Write);
        memory.Map(BaseAddress + 4, 4, 4, [5, 6, 7, 8], ProgramHeaderFlags.Read | ProgramHeaderFlags.Write);
        Span<byte> crossingRead = stackalloc byte[2];

        Assert.True(memory.TryRead(BaseAddress + 3, crossingRead));
        Assert.Equal(new byte[] { 4, 5 }, crossingRead.ToArray());
        Assert.True(memory.TryCompare(BaseAddress + 3, [4, 5]));
        Assert.False(memory.TryCompare(BaseAddress + 3, [4, 6]));
        Assert.True(memory.TryWrite(BaseAddress + 3, new byte[] { 0xAA, 0xBB }));

        Span<byte> contents = stackalloc byte[8];
        Assert.True(memory.TryRead(BaseAddress, contents[..4]));
        Assert.True(memory.TryRead(BaseAddress + 4, contents[4..]));
        Assert.Equal(new byte[] { 1, 2, 3, 0xAA, 0xBB, 6, 7, 8 }, contents.ToArray());
    }

    [Fact]
    public void AccessAcrossMappingGapFailsWithoutPartialMutation()
    {
        var memory = new VirtualMemory();
        memory.Map(BaseAddress, 4, 0, [1, 2, 3, 4], ProgramHeaderFlags.Read | ProgramHeaderFlags.Write);
        memory.Map(BaseAddress + 5, 4, 4, [5, 6, 7, 8], ProgramHeaderFlags.Read | ProgramHeaderFlags.Write);
        byte[] crossingRead = [0xCC, 0xCC, 0xCC];

        Assert.False(memory.TryRead(BaseAddress + 3, crossingRead));
        Assert.Equal(new byte[] { 0xCC, 0xCC, 0xCC }, crossingRead);
        Assert.False(memory.TryCompare(BaseAddress + 3, [4, 0, 5]));
        Assert.False(memory.TryWrite(BaseAddress + 3, [0xAA, 0xBB, 0xCC]));

        Span<byte> first = stackalloc byte[4];
        Span<byte> second = stackalloc byte[4];
        Assert.True(memory.TryRead(BaseAddress, first));
        Assert.True(memory.TryRead(BaseAddress + 5, second));
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, first.ToArray());
        Assert.Equal(new byte[] { 5, 6, 7, 8 }, second.ToArray());
    }

    [Fact]
    public void AccessSpansMultipleRegionsMappedOutOfOrder()
    {
        var memory = new VirtualMemory();
        memory.Map(BaseAddress + 8, 4, 8, [9, 10, 11, 12], ProgramHeaderFlags.Read | ProgramHeaderFlags.Write);
        memory.Map(BaseAddress, 4, 0, [1, 2, 3, 4], ProgramHeaderFlags.Read | ProgramHeaderFlags.Write);
        memory.Map(BaseAddress + 4, 4, 4, [5, 6, 7, 8], ProgramHeaderFlags.Read | ProgramHeaderFlags.Write);
        Span<byte> crossingRead = stackalloc byte[6];

        Assert.True(memory.TryRead(BaseAddress + 3, crossingRead));
        Assert.Equal(new byte[] { 4, 5, 6, 7, 8, 9 }, crossingRead.ToArray());
        Assert.True(memory.TryWrite(BaseAddress + 3, [0xA3, 0xA4, 0xA5, 0xA6, 0xA7, 0xA8]));

        Span<byte> contents = stackalloc byte[12];
        Assert.True(memory.TryRead(BaseAddress, contents));
        Assert.Equal(
            new byte[] { 1, 2, 3, 0xA3, 0xA4, 0xA5, 0xA6, 0xA7, 0xA8, 10, 11, 12 },
            contents.ToArray());
    }

    [Fact]
    public void RejectedOverlapLeavesExistingMappingIntact()
    {
        var memory = CreateMappedMemory([1, 2, 3, 4]);

        Assert.Throws<InvalidOperationException>(() =>
            memory.Map(
                BaseAddress + 0x800,
                RegionSize,
                0,
                [],
                ProgramHeaderFlags.Read | ProgramHeaderFlags.Write));

        var region = Assert.Single(memory.SnapshotRegions());
        Assert.Equal(BaseAddress, region.VirtualAddress);
        Span<byte> contents = stackalloc byte[4];
        Assert.True(memory.TryRead(BaseAddress, contents));
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, contents.ToArray());
    }

    [Fact]
    public void OverlapIsRejectedBeforeAllocatingMaximumSizedBackingStore()
    {
        var memory = CreateMappedMemory([1, 2, 3, 4]);

        Assert.Throws<InvalidOperationException>(() =>
            memory.Map(
                BaseAddress,
                int.MaxValue,
                0,
                [],
                ProgramHeaderFlags.Read | ProgramHeaderFlags.Write));

        var region = Assert.Single(memory.SnapshotRegions());
        Assert.Equal(RegionSize, region.MemorySize);
    }

    [Fact]
    public void WrappedMappingIsRejectedWithoutMutation()
    {
        var memory = new VirtualMemory();

        Assert.Throws<OverflowException>(() =>
            memory.Map(
                ulong.MaxValue,
                2,
                0,
                [],
                ProgramHeaderFlags.Read | ProgramHeaderFlags.Write));
        Assert.Empty(memory.SnapshotRegions());
    }

    [Fact]
    public void ClearRemovesMappingsAndTheirContents()
    {
        var memory = CreateMappedMemory([1, 2, 3, 4]);
        var resetVersion = memory.ResetVersion;

        memory.Clear();

        Assert.NotEqual(resetVersion, memory.ResetVersion);
        Assert.Empty(memory.SnapshotRegions());
        Span<byte> contents = stackalloc byte[1];
        Assert.False(memory.TryRead(BaseAddress, contents));
    }

    [Fact]
    public void SnapshotRegionsAreAddressOrderedRegardlessOfMapOrder()
    {
        var memory = new VirtualMemory();
        memory.Map(0x9000, 0x1000, 0, [], ProgramHeaderFlags.Read);
        memory.Map(0x3000, 0x1000, 0, [], ProgramHeaderFlags.Read);
        memory.Map(0x6000, 0x1000, 0, [], ProgramHeaderFlags.Read);

        Assert.Equal(
            new[] { 0x3000UL, 0x6000UL, 0x9000UL },
            memory.SnapshotRegions().Select(region => region.VirtualAddress));
        Assert.True(memory.TryQueryMemoryRegion(0x4000, findNext: true, out var next));
        Assert.Equal(0x6000UL, next.Address);
    }

    [Fact]
    public void RegionLookupUsesLogarithmicProbeDepth()
    {
        var memory = new VirtualMemory();
        const int regionCount = 1024;
        const ulong stride = 0x10;
        for (var index = 0; index < regionCount; index++)
        {
            memory.Map(
                BaseAddress + ((ulong)index * stride),
                1,
                (ulong)index,
                [unchecked((byte)index)],
                ProgramHeaderFlags.Read | ProgramHeaderFlags.Write);
        }

        var lastAddress = BaseAddress + ((regionCount - 1) * stride);
        Span<byte> value = stackalloc byte[1];
        Assert.True(memory.TryRead(lastAddress, value));
        Assert.Equal(0xFF, value[0]);
        Assert.InRange(memory.CountRegionLookupProbes(lastAddress), 1, 11);
    }

    private static VirtualMemory CreateMappedMemory(byte[] fileData)
    {
        var memory = new VirtualMemory();
        memory.Map(
            BaseAddress,
            RegionSize,
            0,
            fileData,
            ProgramHeaderFlags.Read | ProgramHeaderFlags.Write);
        return memory;
    }
}
