// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using SharpEmu.HLE;
using SharpEmu.Libs.Agc;
using Xunit;

namespace SharpEmu.Libs.Tests;

public sealed class AgcIndirectDrawArgumentsTests
{
    private const ulong BufferAddress = 0x2000;

    [Fact]
    public void ReadsNonIndexedDrawArgumentsAtByteOffset()
    {
        var memory = new FakeGuestMemory();
        var bytes = new byte[32];
        WriteUInt32(bytes, 8, 17);
        WriteUInt32(bytes, 12, 3);
        WriteUInt32(bytes, 16, 5);
        WriteUInt32(bytes, 20, 7);
        memory.AddRegion(BufferAddress, bytes);
        var context = new CpuContext(memory, Generation.Gen5);

        Assert.True(AgcIndirectDrawArguments.TryRead(
            context,
            BufferAddress,
            byteOffset: 8,
            indexed: false,
            out var arguments));
        Assert.Equal(
            new AgcIndirectDrawArguments(
                VertexCount: 17,
                InstanceCount: 3,
                FirstVertex: 5,
                FirstIndex: 0,
                VertexOffset: 0,
                FirstInstance: 7),
            arguments);
    }

    [Fact]
    public void ReadsIndexedDrawArgumentsIncludingSignedVertexOffset()
    {
        var memory = new FakeGuestMemory();
        var bytes = new byte[20];
        WriteUInt32(bytes, 0, 23);
        WriteUInt32(bytes, 4, 4);
        WriteUInt32(bytes, 8, 11);
        WriteUInt32(bytes, 12, unchecked((uint)-9));
        WriteUInt32(bytes, 16, 13);
        memory.AddRegion(BufferAddress, bytes);
        var context = new CpuContext(memory, Generation.Gen5);

        Assert.True(AgcIndirectDrawArguments.TryRead(
            context,
            BufferAddress,
            byteOffset: 0,
            indexed: true,
            out var arguments));
        Assert.Equal(
            new AgcIndirectDrawArguments(
                VertexCount: 23,
                InstanceCount: 4,
                FirstVertex: 0,
                FirstIndex: 11,
                VertexOffset: -9,
                FirstInstance: 13),
            arguments);
    }

    [Fact]
    public void RejectsTruncatedOrOverflowingArgumentRanges()
    {
        var memory = new FakeGuestMemory();
        memory.AddRegion(BufferAddress, new byte[12]);
        var context = new CpuContext(memory, Generation.Gen5);

        Assert.False(AgcIndirectDrawArguments.TryRead(
            context,
            BufferAddress,
            byteOffset: 0,
            indexed: false,
            out _));
        Assert.False(AgcIndirectDrawArguments.TryRead(
            context,
            ulong.MaxValue - 3,
            byteOffset: 8,
            indexed: false,
            out _));
        Assert.False(AgcIndirectDrawArguments.TryRead(
            context,
            BufferAddress,
            byteOffset: 2,
            indexed: false,
            out _));
        Assert.False(AgcIndirectDrawArguments.TryRead(
            context,
            ulong.MaxValue - 8,
            byteOffset: 0,
            indexed: true,
            out _));
    }

    private static void WriteUInt32(byte[] bytes, int offset, uint value) =>
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(offset), value);
}
