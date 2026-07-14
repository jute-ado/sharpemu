// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using Xunit;

namespace SharpEmu.Libs.Tests;

public sealed class FakeGuestMemoryTests
{
    [Fact]
    public void ReadRejectsRangeCrossingAddressSpaceCeiling()
    {
        var memory = new FakeGuestMemory();
        memory.AddRegion(ulong.MaxValue, [0x11, 0x22]);
        Span<byte> destination = stackalloc byte[2];
        destination.Fill(0xA5);

        Assert.False(memory.TryRead(ulong.MaxValue, destination));
        Assert.Equal([0xA5, 0xA5], destination.ToArray());
    }

    [Fact]
    public void WriteRejectsRangeCrossingAddressSpaceCeiling()
    {
        var memory = new FakeGuestMemory();
        var backing = new byte[] { 0x11, 0x22 };
        memory.AddRegion(ulong.MaxValue, backing);

        Assert.False(memory.TryWrite(ulong.MaxValue, [0x33, 0x44]));
        Assert.Equal([0x11, 0x22], backing);
    }

    [Fact]
    public void CompareRejectsRangeCrossingAddressSpaceCeiling()
    {
        var memory = new FakeGuestMemory();
        memory.AddRegion(ulong.MaxValue, [0x11, 0x22]);

        Assert.False(memory.TryCompare(ulong.MaxValue, [0x11, 0x22]));
        Assert.Equal(1, memory.CompareCount);
    }

    [Fact]
    public void SingleByteAtAddressSpaceCeilingRemainsAccessible()
    {
        var memory = new FakeGuestMemory();
        var backing = new byte[] { 0x11 };
        memory.AddRegion(ulong.MaxValue, backing);
        Span<byte> destination = stackalloc byte[1];

        Assert.True(memory.TryRead(ulong.MaxValue, destination));
        Assert.Equal(0x11, destination[0]);
        Assert.True(memory.TryCompare(ulong.MaxValue, [0x11]));
        Assert.False(memory.TryCompare(ulong.MaxValue, [0x22]));
        Assert.True(memory.TryWrite(ulong.MaxValue, [0x22]));
        Assert.Equal(0x22, backing[0]);
    }
}
