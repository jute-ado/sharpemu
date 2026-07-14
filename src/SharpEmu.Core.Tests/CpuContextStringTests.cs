// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using Xunit;

namespace SharpEmu.Core.Tests;

public sealed class CpuContextStringTests
{
    [Fact]
    public void RejectsCStringWhoseAddressWrapsAroundGuestAddressSpace()
    {
        var memory = new SparseMemory(
            (ulong.MaxValue, (byte)'A'),
            (0, (byte)0));
        var context = new CpuContext(memory, Generation.Gen5);

        Assert.False(context.TryReadNullTerminatedUtf8(ulong.MaxValue, 2, out var value));
        Assert.Equal(string.Empty, value);
    }

    [Fact]
    public void ReadsCStringWithoutTouchingBytesAfterTerminator()
    {
        var memory = new SparseMemory(
            (0x1000, (byte)'o'),
            (0x1001, (byte)'k'),
            (0x1002, (byte)0));
        var context = new CpuContext(memory, Generation.Gen5);

        Assert.True(context.TryReadNullTerminatedUtf8(0x1000, int.MaxValue, out var value));
        Assert.Equal("ok", value);
        Assert.Equal(new[] { 0x1000UL, 0x1001UL, 0x1002UL }, memory.ReadAddresses);
    }

    [Fact]
    public void ReturnsCapacityLimitedStringWithoutTerminator()
    {
        var memory = new SparseMemory(
            (0x2000, (byte)'a'),
            (0x2001, (byte)'b'),
            (0x2002, (byte)'c'));
        var context = new CpuContext(memory, Generation.Gen5);

        Assert.True(context.TryReadNullTerminatedUtf8(0x2000, 3, out var value));
        Assert.Equal("abc", value);
    }

    [Theory]
    [InlineData(0UL, 1)]
    [InlineData(1UL, 0)]
    [InlineData(1UL, -1)]
    public void RejectsInvalidArguments(ulong address, int capacity)
    {
        var context = new CpuContext(new SparseMemory(), Generation.Gen5);

        Assert.False(context.TryReadNullTerminatedUtf8(address, capacity, out var value));
        Assert.Equal(string.Empty, value);
    }

    private sealed class SparseMemory(params (ulong Address, byte Value)[] bytes) : ICpuMemory
    {
        private readonly Dictionary<ulong, byte> _bytes = bytes.ToDictionary(pair => pair.Address, pair => pair.Value);

        public List<ulong> ReadAddresses { get; } = new();

        public bool TryRead(ulong virtualAddress, Span<byte> destination)
        {
            if (destination.Length != 1 || !_bytes.TryGetValue(virtualAddress, out var value))
            {
                return false;
            }

            ReadAddresses.Add(virtualAddress);
            destination[0] = value;
            return true;
        }

        public bool TryWrite(ulong virtualAddress, ReadOnlySpan<byte> source) => false;
    }
}
