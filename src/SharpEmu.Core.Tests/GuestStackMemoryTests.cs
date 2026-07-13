// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Core.Loader;
using SharpEmu.Core.Memory;
using SharpEmu.Core.Cpu;
using Xunit;

namespace SharpEmu.Core.Tests;

public sealed class GuestStackMemoryTests
{
    private const ulong StackStart = 0x7000;
    private const ulong StackSize = 0x2000;

    [Fact]
    public void VirtualMemoryTracksRegisteredStackRangeBoundaries()
    {
        var memory = new VirtualMemory();
        memory.Map(
            StackStart,
            StackSize,
            fileOffset: 0,
            fileData: ReadOnlySpan<byte>.Empty,
            ProgramHeaderFlags.Read | ProgramHeaderFlags.Write);
        memory.RegisterStackRange(StackStart, StackSize);

        Assert.False(memory.TryGetStackRange(StackStart - 1, out _, out _));
        Assert.True(memory.TryGetStackRange(StackStart, out var start, out var end));
        Assert.Equal(StackStart, start);
        Assert.Equal(StackStart + StackSize, end);
        Assert.True(memory.TryGetStackRange(StackStart + StackSize - 1, out _, out _));
        Assert.False(memory.TryGetStackRange(StackStart + StackSize, out _, out _));
    }

    [Fact]
    public void ClearingVirtualMemoryClearsRegisteredStackRanges()
    {
        var memory = new VirtualMemory();
        memory.RegisterStackRange(StackStart, StackSize);

        memory.Clear();

        Assert.False(memory.TryGetStackRange(StackStart, out _, out _));
    }

    [Fact]
    public void TrackedCpuMemoryForwardsStackRangeQueries()
    {
        var memory = new VirtualMemory();
        memory.RegisterStackRange(StackStart, StackSize);
        var trackedMemory = new TrackedCpuMemory(memory);

        Assert.True(trackedMemory.TryGetStackRange(StackStart + 1, out var start, out var end));
        Assert.Equal(StackStart, start);
        Assert.Equal(StackStart + StackSize, end);
    }
}
