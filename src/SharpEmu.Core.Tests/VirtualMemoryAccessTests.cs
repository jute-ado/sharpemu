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
    public void AccessCannotCrossAdjacentRegionBoundary()
    {
        var memory = new VirtualMemory();
        memory.Map(BaseAddress, 4, 0, [1, 2, 3, 4], ProgramHeaderFlags.Read | ProgramHeaderFlags.Write);
        memory.Map(BaseAddress + 4, 4, 4, [5, 6, 7, 8], ProgramHeaderFlags.Read | ProgramHeaderFlags.Write);
        Span<byte> crossingRead = stackalloc byte[2];

        Assert.False(memory.TryRead(BaseAddress + 3, crossingRead));
        Assert.False(memory.TryWrite(BaseAddress + 3, new byte[] { 0xAA, 0xBB }));

        Span<byte> contents = stackalloc byte[8];
        Assert.True(memory.TryRead(BaseAddress, contents[..4]));
        Assert.True(memory.TryRead(BaseAddress + 4, contents[4..]));
        Assert.Equal(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 }, contents.ToArray());
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

        memory.Clear();

        Assert.Empty(memory.SnapshotRegions());
        Span<byte> contents = stackalloc byte[1];
        Assert.False(memory.TryRead(BaseAddress, contents));
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
