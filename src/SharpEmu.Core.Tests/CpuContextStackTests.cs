// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using SharpEmu.HLE;
using Xunit;

namespace SharpEmu.Core.Tests;

public sealed class CpuContextStackTests
{
    [Fact]
    public void PushCommitsStackPointerOnlyAfterSuccessfulWrite()
    {
        var context = new CpuContext(new RejectingMemory(), Generation.Gen5);
        context[CpuRegister.Rsp] = 0x1000;

        Assert.False(context.PushUInt64(0x1122_3344_5566_7788));
        Assert.Equal(0x1000UL, context[CpuRegister.Rsp]);
    }

    [Fact]
    public void PushRejectsStackPointerUnderflowWithoutWriting()
    {
        var memory = new RecordingMemory();
        var context = new CpuContext(memory, Generation.Gen5);
        context[CpuRegister.Rsp] = 4;

        Assert.False(context.PushUInt64(0x1122_3344_5566_7788));
        Assert.Equal(4UL, context[CpuRegister.Rsp]);
        Assert.Empty(memory.WriteAddresses);
    }

    [Fact]
    public void PushWritesLittleEndianValueAndUpdatesStackPointer()
    {
        var memory = new RecordingMemory(writableAddress: 0x0FF8);
        var context = new CpuContext(memory, Generation.Gen5);
        context[CpuRegister.Rsp] = 0x1000;

        Assert.True(context.PushUInt64(0x1122_3344_5566_7788));
        Assert.Equal(0x0FF8UL, context[CpuRegister.Rsp]);
        Assert.Equal(new[] { 0x0FF8UL }, memory.WriteAddresses);
        Assert.Equal(0x1122_3344_5566_7788UL, BinaryPrimitives.ReadUInt64LittleEndian(memory.WrittenBytes));
    }

    [Fact]
    public void PopRejectsStackPointerOverflowWithoutReading()
    {
        var stackPointer = ulong.MaxValue - 3;
        var memory = new RecordingMemory(readableAddress: stackPointer, readValue: 42);
        var context = new CpuContext(memory, Generation.Gen5);
        context[CpuRegister.Rsp] = stackPointer;

        Assert.False(context.PopUInt64(out var value));
        Assert.Equal(0UL, value);
        Assert.Equal(stackPointer, context[CpuRegister.Rsp]);
        Assert.Empty(memory.ReadAddresses);
    }

    [Fact]
    public void PopCommitsStackPointerOnlyAfterSuccessfulRead()
    {
        var context = new CpuContext(new RejectingMemory(), Generation.Gen5);
        context[CpuRegister.Rsp] = 0x1000;

        Assert.False(context.PopUInt64(out var value));
        Assert.Equal(0UL, value);
        Assert.Equal(0x1000UL, context[CpuRegister.Rsp]);
    }

    [Fact]
    public void PopReadsLittleEndianValueAndUpdatesStackPointer()
    {
        var memory = new RecordingMemory(readableAddress: 0x1000, readValue: 0x1122_3344_5566_7788);
        var context = new CpuContext(memory, Generation.Gen5);
        context[CpuRegister.Rsp] = 0x1000;

        Assert.True(context.PopUInt64(out var value));
        Assert.Equal(0x1122_3344_5566_7788UL, value);
        Assert.Equal(0x1008UL, context[CpuRegister.Rsp]);
        Assert.Equal(new[] { 0x1000UL }, memory.ReadAddresses);
    }

    private sealed class RejectingMemory : ICpuMemory
    {
        public bool TryRead(ulong virtualAddress, Span<byte> destination) => false;

        public bool TryWrite(ulong virtualAddress, ReadOnlySpan<byte> source) => false;
    }

    private sealed class RecordingMemory(
        ulong? readableAddress = null,
        ulong readValue = 0,
        ulong? writableAddress = null) : ICpuMemory
    {
        public List<ulong> ReadAddresses { get; } = new();

        public List<ulong> WriteAddresses { get; } = new();

        public byte[] WrittenBytes { get; private set; } = Array.Empty<byte>();

        public bool TryRead(ulong virtualAddress, Span<byte> destination)
        {
            if (virtualAddress != readableAddress || destination.Length != sizeof(ulong))
            {
                return false;
            }

            ReadAddresses.Add(virtualAddress);
            BinaryPrimitives.WriteUInt64LittleEndian(destination, readValue);
            return true;
        }

        public bool TryWrite(ulong virtualAddress, ReadOnlySpan<byte> source)
        {
            if (virtualAddress != writableAddress || source.Length != sizeof(ulong))
            {
                return false;
            }

            WriteAddresses.Add(virtualAddress);
            WrittenBytes = source.ToArray();
            return true;
        }
    }
}
