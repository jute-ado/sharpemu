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
    private const ulong WaitAddress = 0x7000;
    private const ulong ResumeSubmitPacketAddress = 0x8000;
    private const ulong ResumeDcbAddress = 0x9000;

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
    public void DriverSubmitDcb_ResumesParentOnlyAfterIndirectWaitCompletes()
    {
        var parent = new byte[9 * sizeof(uint)];
        CreateIndirectBufferPacket(ChildDcbAddress, 12).CopyTo(parent, 0);
        CreateWriteDataPacket(DestinationAddress, 0x2222_2222).CopyTo(parent, 16);
        var child = new byte[12 * sizeof(uint)];
        CreateWaitRegMemPacket(WaitAddress, 1).CopyTo(child, 0);
        CreateWriteDataPacket(DestinationAddress, 0x1111_1111).CopyTo(child, 28);
        var memory = new FakeGuestMemory();
        memory.AddRegion(SubmitPacketAddress, CreateSubmitPacket(ParentDcbAddress, 9));
        memory.AddRegion(ParentDcbAddress, parent);
        memory.AddRegion(ChildDcbAddress, child);
        memory.AddRegion(DestinationAddress, new byte[sizeof(uint)]);
        memory.AddRegion(WaitAddress, new byte[sizeof(uint)]);
        memory.AddRegion(
            ResumeSubmitPacketAddress,
            CreateSubmitPacket(ResumeDcbAddress, 1));
        memory.AddRegion(ResumeDcbAddress, BitConverter.GetBytes(0x8000_0000u));
        var context = new CpuContext(memory, Generation.Gen5);
        context[CpuRegister.Rdi] = SubmitPacketAddress;

        Assert.Equal(0, AgcExports.DriverSubmitDcb(context));
        Assert.True(context.TryReadUInt32(DestinationAddress, out var valueBeforeResume));
        Assert.Equal(0u, valueBeforeResume);

        Assert.True(context.TryWriteUInt32(WaitAddress, 1));
        context[CpuRegister.Rdi] = ResumeSubmitPacketAddress;
        Assert.Equal(0, AgcExports.DriverSubmitDcb(context));
        Assert.True(context.TryReadUInt32(DestinationAddress, out var valueAfterResume));
        Assert.Equal(0x2222_2222u, valueAfterResume);
    }

    [Fact]
    public void DriverSubmitDcb_ResumesParentWhenWaitEndsIndirectBuffer()
    {
        var parent = new byte[9 * sizeof(uint)];
        CreateIndirectBufferPacket(ChildDcbAddress, 7).CopyTo(parent, 0);
        CreateWriteDataPacket(DestinationAddress, 0x3333_3333).CopyTo(parent, 16);
        var memory = new FakeGuestMemory();
        memory.AddRegion(SubmitPacketAddress, CreateSubmitPacket(ParentDcbAddress, 9));
        memory.AddRegion(ParentDcbAddress, parent);
        memory.AddRegion(ChildDcbAddress, CreateWaitRegMemPacket(WaitAddress, 1));
        memory.AddRegion(DestinationAddress, new byte[sizeof(uint)]);
        memory.AddRegion(WaitAddress, new byte[sizeof(uint)]);
        memory.AddRegion(
            ResumeSubmitPacketAddress,
            CreateSubmitPacket(ResumeDcbAddress, 1));
        memory.AddRegion(ResumeDcbAddress, BitConverter.GetBytes(0x8000_0000u));
        var context = new CpuContext(memory, Generation.Gen5);
        context[CpuRegister.Rdi] = SubmitPacketAddress;

        Assert.Equal(0, AgcExports.DriverSubmitDcb(context));
        Assert.True(context.TryReadUInt32(DestinationAddress, out var valueBeforeResume));
        Assert.Equal(0u, valueBeforeResume);

        Assert.True(context.TryWriteUInt32(WaitAddress, 1));
        context[CpuRegister.Rdi] = ResumeSubmitPacketAddress;
        Assert.Equal(0, AgcExports.DriverSubmitDcb(context));
        Assert.True(context.TryReadUInt32(DestinationAddress, out var valueAfterResume));
        Assert.Equal(0x3333_3333u, valueAfterResume);
    }

    [Fact]
    public void DriverSubmitDcb_DoesNotResumeWaitersFromAnotherGuestMemory()
    {
        var firstMemory = CreateWaitingGuestMemory(0x4444_4444);
        var firstContext = new CpuContext(firstMemory, Generation.Gen5);
        firstContext[CpuRegister.Rdi] = SubmitPacketAddress;

        Assert.Equal(0, AgcExports.DriverSubmitDcb(firstContext));
        Assert.True(firstContext.TryReadUInt32(DestinationAddress, out var firstValueBeforeResume));
        Assert.Equal(0u, firstValueBeforeResume);

        var secondMemory = CreateWaitingGuestMemory(0x5555_5555);
        var secondContext = new CpuContext(secondMemory, Generation.Gen5);
        Assert.True(secondContext.TryWriteUInt32(WaitAddress, 1));
        secondContext[CpuRegister.Rdi] = ResumeSubmitPacketAddress;

        Assert.Equal(0, AgcExports.DriverSubmitDcb(secondContext));
        Assert.True(secondContext.TryReadUInt32(DestinationAddress, out var secondValue));
        Assert.Equal(0u, secondValue);
        Assert.True(firstContext.TryReadUInt32(DestinationAddress, out var firstValueAfterOtherSubmit));
        Assert.Equal(0u, firstValueAfterOtherSubmit);

        Assert.True(firstContext.TryWriteUInt32(WaitAddress, 1));
        firstContext[CpuRegister.Rdi] = ResumeSubmitPacketAddress;
        Assert.Equal(0, AgcExports.DriverSubmitDcb(firstContext));
        Assert.True(firstContext.TryReadUInt32(DestinationAddress, out var firstValueAfterResume));
        Assert.Equal(0x4444_4444u, firstValueAfterResume);
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

    private static byte[] CreateWaitRegMemPacket(ulong address, uint reference)
    {
        var packet = new byte[7 * sizeof(uint)];
        BinaryPrimitives.WriteUInt32LittleEndian(packet, Pm4(7, 0x3C));
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(4), 3);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(8), (uint)address);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(12), (uint)(address >> 32));
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(16), reference);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(20), uint.MaxValue);
        return packet;
    }

    private static FakeGuestMemory CreateWaitingGuestMemory(uint resumedValue)
    {
        var parent = new byte[9 * sizeof(uint)];
        CreateIndirectBufferPacket(ChildDcbAddress, 7).CopyTo(parent, 0);
        CreateWriteDataPacket(DestinationAddress, resumedValue).CopyTo(parent, 16);
        var memory = new FakeGuestMemory();
        memory.AddRegion(SubmitPacketAddress, CreateSubmitPacket(ParentDcbAddress, 9));
        memory.AddRegion(ParentDcbAddress, parent);
        memory.AddRegion(ChildDcbAddress, CreateWaitRegMemPacket(WaitAddress, 1));
        memory.AddRegion(DestinationAddress, new byte[sizeof(uint)]);
        memory.AddRegion(WaitAddress, new byte[sizeof(uint)]);
        memory.AddRegion(
            ResumeSubmitPacketAddress,
            CreateSubmitPacket(ResumeDcbAddress, 1));
        memory.AddRegion(ResumeDcbAddress, BitConverter.GetBytes(0x8000_0000u));
        return memory;
    }

    private static uint Pm4(uint dwordCount, uint opcode) =>
        0xC000_0000u | ((dwordCount - 2) << 16) | (opcode << 8);
}
