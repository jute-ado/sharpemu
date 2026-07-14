// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Core.Cpu.Disasm;
using SharpEmu.Core.Loader;
using SharpEmu.Core.Memory;
using SharpEmu.HLE;
using Xunit;

namespace SharpEmu.Core.Tests;

public sealed class IcedDecoderGuestReadTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void NonpositiveMaximumLengthDoesNotReadGuestMemory(int maxLength)
    {
        var memory = new SparseVirtualMemory((0x1000, 0x90));

        Assert.False(IcedDecoder.TryReadGuestBytes((ICpuMemory)memory, 0x1000, maxLength, out var bytes));
        Assert.Empty(bytes);
        Assert.Empty(memory.ReadAddresses);
    }

    [Fact]
    public void CpuMemoryOverloadStopsAtGuestAddressSpaceCeiling()
    {
        var memory = new SparseVirtualMemory(
            (ulong.MaxValue, 0x90),
            (0, 0xCC));

        Assert.True(IcedDecoder.TryReadGuestBytes((ICpuMemory)memory, ulong.MaxValue, 2, out var bytes));
        Assert.Equal(new byte[] { 0x90 }, bytes);
        Assert.Equal(new[] { ulong.MaxValue }, memory.ReadAddresses);
    }

    [Fact]
    public void VirtualMemoryOverloadStopsAtGuestAddressSpaceCeiling()
    {
        var memory = new SparseVirtualMemory(
            (ulong.MaxValue, 0x90),
            (0, 0xCC));

        Assert.True(IcedDecoder.TryReadGuestBytes((IVirtualMemory)memory, ulong.MaxValue, 2, out var bytes));
        Assert.Equal(new byte[] { 0x90 }, bytes);
        Assert.Equal(new[] { ulong.MaxValue }, memory.ReadAddresses);
    }

    [Fact]
    public void ReturnsReadablePrefixBeforeFirstUnmappedByte()
    {
        var memory = new SparseVirtualMemory(
            (0x2000, 0x48),
            (0x2001, 0x89));

        Assert.True(IcedDecoder.TryReadGuestBytes((ICpuMemory)memory, 0x2000, 4, out var bytes));
        Assert.Equal(new byte[] { 0x48, 0x89 }, bytes);
    }

    [Fact]
    public void ClampsReadsToArchitecturalMaximumInstructionLength()
    {
        var mapped = Enumerable.Range(0, 20)
            .Select(index => (0x3000UL + (ulong)index, (byte)index))
            .ToArray();
        var memory = new SparseVirtualMemory(mapped);

        Assert.True(IcedDecoder.TryReadGuestBytes((ICpuMemory)memory, 0x3000, 20, out var bytes));
        Assert.Equal(15, bytes.Length);
        Assert.Equal(15, memory.ReadAddresses.Count);
        Assert.Equal(Enumerable.Range(0, 15).Select(index => (byte)index), bytes);
    }

    private sealed class SparseVirtualMemory(params (ulong Address, byte Value)[] bytes) : IVirtualMemory
    {
        private readonly Dictionary<ulong, byte> _bytes = bytes.ToDictionary(pair => pair.Address, pair => pair.Value);

        public List<ulong> ReadAddresses { get; } = new();

        public void Clear()
        {
            _bytes.Clear();
            ReadAddresses.Clear();
        }

        public void Map(
            ulong virtualAddress,
            ulong memorySize,
            ulong fileOffset,
            ReadOnlySpan<byte> fileData,
            ProgramHeaderFlags protection) => throw new NotSupportedException();

        public IReadOnlyList<VirtualMemoryRegion> SnapshotRegions() => Array.Empty<VirtualMemoryRegion>();

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
