// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using SharpEmu.HLE;
using SharpEmu.Libs.Agc;
using Xunit;

namespace SharpEmu.Libs.Tests;

public sealed class AgcIndirectCommandBufferTests
{
    private const ulong SubmitPacketAddress = 0x1000;
    private const ulong ParentDcbAddress = 0x2000;
    private const ulong ChildDcbAddress = 0x3000;
    private const ulong DestinationAddress = 0x4000;

    [Fact]
    public void DriverSubmitDcb_ExecutesWriteDataFromIndirectBuffer()
    {
        var memory = new FakeGuestMemory();
        memory.AddRegion(SubmitPacketAddress, CreateSubmitPacket(ParentDcbAddress, 4));
        memory.AddRegion(ParentDcbAddress, CreateIndirectBufferPacket(ChildDcbAddress, 5));
        memory.AddRegion(
            ChildDcbAddress,
            CreateWriteDataPacket(DestinationAddress, 0x1122_3344));
        memory.AddRegion(DestinationAddress, new byte[sizeof(uint)]);
        var context = new CpuContext(memory, Generation.Gen5);
        context[CpuRegister.Rdi] = SubmitPacketAddress;

        Assert.Equal(0, AgcExports.DriverSubmitDcb(context));
        Assert.True(context.TryReadUInt32(DestinationAddress, out var value));
        Assert.Equal(0x1122_3344u, value);
    }

    [Fact]
    public void DriverSubmitDcb_StopsSelfReferentialIndirectBufferAtDepthLimit()
    {
        var memory = new FakeGuestMemory();
        memory.AddRegion(SubmitPacketAddress, CreateSubmitPacket(ParentDcbAddress, 4));
        memory.AddRegion(
            ParentDcbAddress,
            CreateIndirectBufferPacket(ParentDcbAddress, 4));
        var context = new CpuContext(memory, Generation.Gen5);
        context[CpuRegister.Rdi] = SubmitPacketAddress;

        Assert.Equal(0, AgcExports.DriverSubmitDcb(context));
        Assert.InRange(memory.ReadCount, 1, 128);
    }

    [Fact]
    public void DriverSubmitDcb_ContinuesParentAfterRejectedIndirectBuffer()
    {
        var parent = new byte[9 * sizeof(uint)];
        CreateIndirectBufferPacket(ChildDcbAddress, 0xF_FFFF).CopyTo(parent, 0);
        CreateWriteDataPacket(DestinationAddress, 0xAABB_CCDD).CopyTo(parent, 16);
        var memory = new FakeGuestMemory();
        memory.AddRegion(SubmitPacketAddress, CreateSubmitPacket(ParentDcbAddress, 9));
        memory.AddRegion(ParentDcbAddress, parent);
        memory.AddRegion(DestinationAddress, new byte[sizeof(uint)]);
        var context = new CpuContext(memory, Generation.Gen5);
        context[CpuRegister.Rdi] = SubmitPacketAddress;

        Assert.Equal(0, AgcExports.DriverSubmitDcb(context));
        Assert.True(context.TryReadUInt32(DestinationAddress, out var value));
        Assert.Equal(0xAABB_CCDDu, value);
    }

    [Fact]
    public void DcbSetIndexCount_EmitsIndexBufferSizePacket()
    {
        const ulong commandBufferAddress = 0x5000;
        const ulong commandStorageAddress = 0x6000;
        var memory = new FakeGuestMemory();
        var commandBuffer = new byte[0x38];
        BinaryPrimitives.WriteUInt64LittleEndian(
            commandBuffer.AsSpan(0x10),
            commandStorageAddress);
        BinaryPrimitives.WriteUInt64LittleEndian(
            commandBuffer.AsSpan(0x18),
            commandStorageAddress + 0x100);
        memory.AddRegion(commandBufferAddress, commandBuffer);
        memory.AddRegion(commandStorageAddress, new byte[0x100]);
        var context = new CpuContext(memory, Generation.Gen5);
        context[CpuRegister.Rdi] = commandBufferAddress;
        context[CpuRegister.Rsi] = 37;

        Assert.Equal(0, AgcExports.DcbSetIndexCount(context));
        Assert.Equal(commandStorageAddress, context[CpuRegister.Rax]);
        Assert.True(context.TryReadUInt32(commandStorageAddress, out var header));
        Assert.Equal(0x13u, (header >> 8) & 0xFFu);
        Assert.True(context.TryReadUInt32(commandStorageAddress + 4, out var count));
        Assert.Equal(37u, count);
    }

    private static byte[] CreateSubmitPacket(ulong commandAddress, uint dwordCount)
    {
        var packet = new byte[16];
        BinaryPrimitives.WriteUInt64LittleEndian(packet, commandAddress);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(8), dwordCount);
        return packet;
    }

    private static byte[] CreateIndirectBufferPacket(ulong address, uint dwordCount)
    {
        var packet = new byte[4 * sizeof(uint)];
        BinaryPrimitives.WriteUInt32LittleEndian(packet, Pm4(4, 0x3F));
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(4), (uint)address);
        BinaryPrimitives.WriteUInt32LittleEndian(
            packet.AsSpan(8),
            (uint)(address >> 32) & 0xFFFFu);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(12), dwordCount & 0xF_FFFFu);
        return packet;
    }

    private static byte[] CreateWriteDataPacket(ulong address, uint value)
    {
        var packet = new byte[5 * sizeof(uint)];
        BinaryPrimitives.WriteUInt32LittleEndian(packet, Pm4(5, 0x37));
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(4), 1);
        BinaryPrimitives.WriteUInt64LittleEndian(packet.AsSpan(8), address);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(16), value);
        return packet;
    }

    private static uint Pm4(uint dwordCount, uint opcode) =>
        0xC000_0000u | ((dwordCount - 2) << 16) | (opcode << 8);
}
