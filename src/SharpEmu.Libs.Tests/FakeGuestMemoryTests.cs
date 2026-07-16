// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
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

    [Fact]
    public void FixedAddressAllocationRejectsOverlapWithoutAlternative()
    {
        var memory = new FakeGuestMemory();
        memory.AddRegion(0x2000, new byte[0x1000]);

        Assert.Equal(0UL, memory.AllocateAt(0x2800, 0x1000, allowAlternative: false));
        Assert.Equal(0, memory.GuestAllocationCount);
    }

    [Fact]
    public void SearchAllocationSkipsOccupiedRangesAndHonorsAlignment()
    {
        var memory = new FakeGuestMemory();
        memory.AddRegion(0x2000, new byte[0x1800]);

        Assert.True(memory.TryAllocateAtOrAbove(
            0x2100,
            0x1000,
            executable: false,
            alignment: 0x1000,
            out var address));
        Assert.Equal(0x4000UL, address);
        Assert.Equal(1, memory.GuestAllocationCount);
    }

    [Fact]
    public void ProtectionRequiresFullyMappedRange()
    {
        var memory = new FakeGuestMemory();
        memory.AddRegion(0x5000, new byte[0x1000]);

        Assert.True(memory.TryProtect(
            0x5800,
            0x800,
            GuestPageProtection.Read | GuestPageProtection.Write));
        Assert.False(memory.TryProtect(
            0x5800,
            0x801,
            GuestPageProtection.Read | GuestPageProtection.Write));
    }
}
