// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using SharpEmu.HLE;
using SharpEmu.Libs.Agc;
using Xunit;

namespace SharpEmu.Libs.Tests;

public sealed class AgcIndirectMultiDrawPacketTests
{
    private const ulong PacketAddress = 0x1000;
    private const ulong ArgumentBaseAddress = 0x4000;
    private const ulong CountAddress = 0x8000;

    [Fact]
    public void ReadsDirectCountAndStridedIndexedArguments()
    {
        var memory = new FakeGuestMemory();
        memory.AddRegion(
            PacketAddress,
            CreatePacket(
                dataOffset: 8,
                maximumDrawCount: 2,
                stride: 24,
                indirectCount: false));
        var arguments = new byte[56];
        WriteIndexedArguments(
            arguments,
            offset: 8,
            vertexCount: 10,
            instanceCount: 2,
            firstIndex: 3,
            vertexOffset: -4,
            firstInstance: 5);
        WriteIndexedArguments(
            arguments,
            offset: 32,
            vertexCount: 20,
            instanceCount: 6,
            firstIndex: 7,
            vertexOffset: 8,
            firstInstance: 9);
        memory.AddRegion(ArgumentBaseAddress, arguments);
        var context = new CpuContext(memory, Generation.Gen5);

        Assert.True(AgcIndirectMultiDrawPacket.TryRead(
            context,
            PacketAddress,
            packetLength: 10,
            ArgumentBaseAddress,
            indexed: true,
            out var packet));
        Assert.Equal(2u, packet.DrawCount);
        Assert.Equal(24u, packet.Stride);
        Assert.True(packet.TryReadArguments(context, 1, out var second));
        Assert.Equal(
            new AgcIndirectDrawArguments(20, 6, 0, 7, 8, 9),
            second);
        Assert.False(packet.TryReadArguments(context, 2, out _));
    }

    [Fact]
    public void IndirectCountIsCappedByPacketMaximum()
    {
        var memory = new FakeGuestMemory();
        memory.AddRegion(
            PacketAddress,
            CreatePacket(
                dataOffset: 0,
                maximumDrawCount: 3,
                stride: 16,
                indirectCount: true));
        memory.AddRegion(CountAddress, BitConverter.GetBytes(9u));
        var context = new CpuContext(memory, Generation.Gen5);

        Assert.True(AgcIndirectMultiDrawPacket.TryRead(
            context,
            PacketAddress,
            packetLength: 10,
            ArgumentBaseAddress,
            indexed: false,
            out var packet));
        Assert.Equal(3u, packet.DrawCount);
    }

    [Theory]
    [InlineData(9u, 16u, 1u)]
    [InlineData(10u, 12u, 1u)]
    [InlineData(10u, 18u, 1u)]
    [InlineData(10u, 16u, 65537u)]
    public void RejectsMalformedOrUnboundedPackets(
        uint packetLength,
        uint stride,
        uint maximumDrawCount)
    {
        var memory = new FakeGuestMemory();
        memory.AddRegion(
            PacketAddress,
            CreatePacket(
                dataOffset: 0,
                maximumDrawCount,
                stride,
                indirectCount: false));
        var context = new CpuContext(memory, Generation.Gen5);

        Assert.False(AgcIndirectMultiDrawPacket.TryRead(
            context,
            PacketAddress,
            packetLength,
            ArgumentBaseAddress,
            indexed: false,
            out _));
    }

    private static byte[] CreatePacket(
        uint dataOffset,
        uint maximumDrawCount,
        uint stride,
        bool indirectCount)
    {
        var bytes = new byte[40];
        WriteUInt32(bytes, 4, dataOffset);
        WriteUInt32(bytes, 16, indirectCount ? 1u << 30 : 0);
        WriteUInt32(bytes, 20, maximumDrawCount);
        WriteUInt32(bytes, 24, (uint)CountAddress);
        WriteUInt32(bytes, 28, (uint)(CountAddress >> 32));
        WriteUInt32(bytes, 32, stride);
        WriteUInt32(bytes, 36, 2);
        return bytes;
    }

    private static void WriteIndexedArguments(
        byte[] bytes,
        int offset,
        uint vertexCount,
        uint instanceCount,
        uint firstIndex,
        int vertexOffset,
        uint firstInstance)
    {
        WriteUInt32(bytes, offset, vertexCount);
        WriteUInt32(bytes, offset + 4, instanceCount);
        WriteUInt32(bytes, offset + 8, firstIndex);
        WriteUInt32(bytes, offset + 12, unchecked((uint)vertexOffset));
        WriteUInt32(bytes, offset + 16, firstInstance);
    }

    private static void WriteUInt32(byte[] bytes, int offset, uint value) =>
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(offset), value);
}
