// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using SharpEmu.HLE;
using SharpEmu.Libs.Agc;
using Xunit;

namespace SharpEmu.Libs.Tests;

public sealed class AgcCopyDataTests
{
    private const ulong PacketAddress = 0x1000;
    private const ulong SourceAddress = 0x2000;
    private const ulong DestinationAddress = 0x3000;
    private const ulong SubmitPacketAddress = 0x4000;
    private const ulong CommandBufferAddress = 0x5000;
    private const ulong CommandStorageAddress = 0x6000;
    private const ulong StackAddress = 0x7000;

    [Fact]
    public void ReadsAndExecutesAlignedMemoryCopy()
    {
        var memory = new FakeGuestMemory();
        memory.AddRegion(
            PacketAddress,
            CreateCopyDataPacket(
                destinationSelector: 2,
                DestinationAddress,
                sourceSelector: 2,
                SourceAddress,
                is64Bit: false));
        memory.AddRegion(SourceAddress, BitConverter.GetBytes(0x1122_3344u));
        memory.AddRegion(DestinationAddress, new byte[sizeof(uint)]);
        var context = new CpuContext(memory, Generation.Gen5);

        Assert.True(AgcCopyDataPacket.TryRead(
            context,
            PacketAddress,
            packetLength: 6,
            out var packet));
        Assert.Equal(AgcCopyDataSourceKind.Memory, packet.SourceKind);
        Assert.False(packet.Is64Bit);
        Assert.True(packet.TryExecute(context));
        Assert.True(context.TryReadUInt32(DestinationAddress, out var copied));
        Assert.Equal(0x1122_3344u, copied);
    }

    [Fact]
    public void ReadsAndExecutes64BitImmediateCopy()
    {
        const ulong immediate = 0x1122_3344_5566_7788;
        var memory = new FakeGuestMemory();
        memory.AddRegion(
            PacketAddress,
            CreateCopyDataPacket(
                destinationSelector: 4,
                DestinationAddress,
                sourceSelector: 11,
                immediate,
                is64Bit: true));
        memory.AddRegion(DestinationAddress, new byte[sizeof(ulong)]);
        var context = new CpuContext(memory, Generation.Gen5);

        Assert.True(AgcCopyDataPacket.TryRead(
            context,
            PacketAddress,
            packetLength: 6,
            out var packet));
        Assert.Equal(AgcCopyDataSourceKind.Immediate, packet.SourceKind);
        Assert.True(packet.Is64Bit);
        Assert.True(packet.TryExecute(context));
        Assert.True(context.TryReadUInt64(DestinationAddress, out var copied));
        Assert.Equal(immediate, copied);
    }

    [Theory]
    [InlineData(5u, 2u, 0x2000UL, 0x3000UL)]
    [InlineData(6u, 0u, 0x2000UL, 0x3000UL)]
    [InlineData(6u, 2u, 0x2002UL, 0x3000UL)]
    [InlineData(6u, 2u, 0x2000UL, 0x3002UL)]
    public void RejectsInvalidPacketContract(
        uint packetLength,
        uint sourceSelector,
        ulong source,
        ulong destination)
    {
        var memory = new FakeGuestMemory();
        memory.AddRegion(
            PacketAddress,
            CreateCopyDataPacket(
                destinationSelector: 2,
                destination,
                sourceSelector,
                source,
                is64Bit: false));
        var context = new CpuContext(memory, Generation.Gen5);

        Assert.False(AgcCopyDataPacket.TryRead(
            context,
            PacketAddress,
            packetLength,
            out _));
    }

    [Fact]
    public void DriverSubmitDcbExecutesCopyDataPacket()
    {
        var packet = CreateCopyDataPacket(
            destinationSelector: 2,
            DestinationAddress,
            sourceSelector: 2,
            SourceAddress,
            is64Bit: true);
        var memory = new FakeGuestMemory();
        memory.AddRegion(
            SubmitPacketAddress,
            CreateSubmitPacket(PacketAddress, 6));
        memory.AddRegion(PacketAddress, packet);
        memory.AddRegion(
            SourceAddress,
            BitConverter.GetBytes(0xAABB_CCDD_EEFF_0011UL));
        memory.AddRegion(DestinationAddress, new byte[sizeof(ulong)]);
        var context = new CpuContext(memory, Generation.Gen5);
        context[CpuRegister.Rdi] = SubmitPacketAddress;

        Assert.Equal(0, AgcExports.DriverSubmitDcb(context));
        Assert.True(context.TryReadUInt64(DestinationAddress, out var copied));
        Assert.Equal(0xAABB_CCDD_EEFF_0011UL, copied);
    }

    [Fact]
    public void DcbCopyDataEmitsCanonicalPacket()
    {
        const ulong immediate = 0x0102_0304_0506_0708;
        var commandBuffer = new byte[0x38];
        BinaryPrimitives.WriteUInt64LittleEndian(
            commandBuffer.AsSpan(0x10),
            CommandStorageAddress);
        BinaryPrimitives.WriteUInt64LittleEndian(
            commandBuffer.AsSpan(0x18),
            CommandStorageAddress + 0x100);
        var stack = new byte[4 * sizeof(ulong)];
        BinaryPrimitives.WriteUInt64LittleEndian(stack.AsSpan(8), immediate);
        BinaryPrimitives.WriteUInt64LittleEndian(stack.AsSpan(16), 1);
        BinaryPrimitives.WriteUInt64LittleEndian(stack.AsSpan(24), 1);
        var memory = new FakeGuestMemory();
        memory.AddRegion(CommandBufferAddress, commandBuffer);
        memory.AddRegion(CommandStorageAddress, new byte[0x100]);
        memory.AddRegion(StackAddress, stack);
        var context = new CpuContext(memory, Generation.Gen5);
        context[CpuRegister.Rdi] = CommandBufferAddress;
        context[CpuRegister.Rsi] = 5;
        context[CpuRegister.Rdx] = 2;
        context[CpuRegister.Rcx] = DestinationAddress;
        context[CpuRegister.R8] = 11;
        context[CpuRegister.R9] = 3;
        context[CpuRegister.Rsp] = StackAddress;

        Assert.Equal(0, AgcExports.DcbCopyData(context));
        Assert.Equal(CommandStorageAddress, context[CpuRegister.Rax]);
        Assert.True(context.TryReadUInt64(
            CommandBufferAddress + 0x10,
            out var nextCursor));
        Assert.Equal(CommandStorageAddress + 24, nextCursor);
        Assert.True(context.TryReadUInt32(CommandStorageAddress, out var header));
        Assert.Equal(0x40u, (header >> 8) & 0xFFu);
        Assert.Equal(6u, ((header >> 16) & 0x3FFFu) + 2);
        Assert.True(context.TryReadUInt32(CommandStorageAddress + 4, out var control));
        Assert.Equal(11u, ((control & 0xFu) << 1) | ((control >> 30) & 1u));
        Assert.Equal(4u, ((control >> 8) & 0xFu) << 1);
        Assert.Equal(3u, (control >> 13) & 3u);
        Assert.Equal(1u, (control >> 16) & 1u);
        Assert.Equal(1u, (control >> 20) & 1u);
        Assert.Equal(2u, (control >> 25) & 3u);
        Assert.True(context.TryReadUInt64(CommandStorageAddress + 8, out var source));
        Assert.Equal(immediate, source);
        Assert.True(context.TryReadUInt64(CommandStorageAddress + 16, out var destination));
        Assert.Equal(DestinationAddress, destination);
    }

    private static byte[] CreateSubmitPacket(ulong commandAddress, uint dwordCount)
    {
        var packet = new byte[16];
        BinaryPrimitives.WriteUInt64LittleEndian(packet, commandAddress);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(8), dwordCount);
        return packet;
    }

    private static byte[] CreateCopyDataPacket(
        uint destinationSelector,
        ulong destination,
        uint sourceSelector,
        ulong source,
        bool is64Bit)
    {
        var packet = new byte[6 * sizeof(uint)];
        var control =
            ((sourceSelector >> 1) & 0xFu) |
            (((destinationSelector >> 1) & 0xFu) << 8) |
            ((is64Bit ? 1u : 0u) << 16) |
            ((sourceSelector & 1u) << 30);
        BinaryPrimitives.WriteUInt32LittleEndian(packet, Pm4(6, 0x40));
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(4), control);
        BinaryPrimitives.WriteUInt64LittleEndian(packet.AsSpan(8), source);
        BinaryPrimitives.WriteUInt64LittleEndian(packet.AsSpan(16), destination);
        return packet;
    }

    private static uint Pm4(uint dwordCount, uint opcode) =>
        0xC000_0000u | ((dwordCount - 2) << 16) | (opcode << 8);
}
