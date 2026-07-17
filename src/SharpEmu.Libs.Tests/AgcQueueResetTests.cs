// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using SharpEmu.HLE;
using SharpEmu.Libs.Agc;
using Xunit;

namespace SharpEmu.Libs.Tests;

public sealed class AgcQueueResetTests
{
    private const uint DcbResetRegister = 0x05;
    private const uint AcbResetRegister = 0x09;
    private const ulong FirstSubmitAddress = 0x1000;
    private const ulong SecondSubmitAddress = 0x1100;
    private const ulong ThirdSubmitAddress = 0x1200;
    private const ulong FourthSubmitAddress = 0x1300;
    private const ulong FirstDcbAddress = 0x2000;
    private const ulong SecondDcbAddress = 0x3000;
    private const ulong ThirdDcbAddress = 0x4000;
    private const ulong FourthDcbAddress = 0x4800;
    private const ulong IndirectArgumentsAddress = 0x5000;

    [Fact]
    public void QueueResetDiscardsIndirectArgumentBaseFromEarlierSubmission()
    {
        var memory = CreateMemory();
        var context = new CpuContext(memory, Generation.Gen5);
        context[CpuRegister.Rdi] = FirstSubmitAddress;
        Assert.Equal(0, AgcExports.DriverSubmitDcb(context));
        memory.ClearReadHistory();

        context[CpuRegister.Rdi] = SecondSubmitAddress;
        Assert.Equal(0, AgcExports.DriverSubmitDcb(context));

        Assert.False(WasRead(memory, IndirectArgumentsAddress));
    }

    [Fact]
    public void QueueResetAllowsNewIndirectArgumentBaseInSameSubmission()
    {
        var memory = CreateMemory();
        var context = new CpuContext(memory, Generation.Gen5);
        context[CpuRegister.Rdi] = FirstSubmitAddress;
        Assert.Equal(0, AgcExports.DriverSubmitDcb(context));
        memory.ClearReadHistory();

        context[CpuRegister.Rdi] = ThirdSubmitAddress;
        Assert.Equal(0, AgcExports.DriverSubmitDcb(context));

        Assert.True(WasRead(memory, IndirectArgumentsAddress));
    }

    [Fact]
    public void AcbQueueResetDiscardsStateFromEarlierSubmissionForSameOwner()
    {
        const uint owner = 7;
        var memory = CreateAcbMemory();
        var context = new CpuContext(memory, Generation.Gen5);
        SubmitAcb(context, owner, FirstSubmitAddress);
        memory.ClearReadHistory();

        SubmitAcb(context, owner, SecondSubmitAddress);

        Assert.False(WasRead(memory, IndirectArgumentsAddress));
    }

    [Fact]
    public void AcbQueueResetDoesNotClearAnotherOwnersState()
    {
        var memory = CreateAcbMemory();
        var context = new CpuContext(memory, Generation.Gen5);
        SubmitAcb(context, owner: 7, FirstSubmitAddress);
        SubmitAcb(context, owner: 8, ThirdSubmitAddress);
        memory.ClearReadHistory();

        SubmitAcb(context, owner: 7, FourthSubmitAddress);

        Assert.True(WasRead(memory, IndirectArgumentsAddress));
    }

    private static FakeGuestMemory CreateMemory()
    {
        var secondDcb = new byte[7 * sizeof(uint)];
        CreateResetPacket().CopyTo(secondDcb, 0);
        CreateDrawIndirectPacket().CopyTo(secondDcb, 2 * sizeof(uint));
        var thirdDcb = new byte[11 * sizeof(uint)];
        CreateResetPacket().CopyTo(thirdDcb, 0);
        CreateSetIndirectBasePacket().CopyTo(thirdDcb, 2 * sizeof(uint));
        CreateDrawIndirectPacket().CopyTo(thirdDcb, 6 * sizeof(uint));
        var arguments = new byte[4 * sizeof(uint)];
        BinaryPrimitives.WriteUInt32LittleEndian(arguments, 3);
        BinaryPrimitives.WriteUInt32LittleEndian(arguments.AsSpan(4), 1);

        var memory = new FakeGuestMemory();
        memory.AddRegion(
            FirstSubmitAddress,
            CreateSubmitPacket(FirstDcbAddress, 4));
        memory.AddRegion(
            SecondSubmitAddress,
            CreateSubmitPacket(SecondDcbAddress, 7));
        memory.AddRegion(
            ThirdSubmitAddress,
            CreateSubmitPacket(ThirdDcbAddress, 11));
        memory.AddRegion(FirstDcbAddress, CreateSetIndirectBasePacket());
        memory.AddRegion(SecondDcbAddress, secondDcb);
        memory.AddRegion(ThirdDcbAddress, thirdDcb);
        memory.AddRegion(IndirectArgumentsAddress, arguments);
        return memory;
    }

    private static FakeGuestMemory CreateAcbMemory()
    {
        var resetAndDraw = new byte[7 * sizeof(uint)];
        CreateResetPacket(AcbResetRegister).CopyTo(resetAndDraw, 0);
        CreateDrawIndirectPacket().CopyTo(
            resetAndDraw,
            2 * sizeof(uint));
        var arguments = new byte[4 * sizeof(uint)];
        BinaryPrimitives.WriteUInt32LittleEndian(arguments, 3);
        BinaryPrimitives.WriteUInt32LittleEndian(arguments.AsSpan(4), 1);

        var memory = new FakeGuestMemory();
        memory.AddRegion(
            FirstSubmitAddress,
            CreateSubmitPacket(FirstDcbAddress, 4));
        memory.AddRegion(
            SecondSubmitAddress,
            CreateSubmitPacket(SecondDcbAddress, 7));
        memory.AddRegion(
            ThirdSubmitAddress,
            CreateSubmitPacket(ThirdDcbAddress, 2));
        memory.AddRegion(
            FourthSubmitAddress,
            CreateSubmitPacket(FourthDcbAddress, 5));
        memory.AddRegion(FirstDcbAddress, CreateSetIndirectBasePacket());
        memory.AddRegion(SecondDcbAddress, resetAndDraw);
        memory.AddRegion(ThirdDcbAddress, CreateResetPacket(AcbResetRegister));
        memory.AddRegion(FourthDcbAddress, CreateDrawIndirectPacket());
        memory.AddRegion(IndirectArgumentsAddress, arguments);
        return memory;
    }

    private static void SubmitAcb(
        CpuContext context,
        uint owner,
        ulong submitAddress)
    {
        context[CpuRegister.Rdi] = owner;
        context[CpuRegister.Rsi] = submitAddress;
        Assert.Equal(0, AgcExports.DriverSubmitAcb(context));
    }

    private static bool WasRead(FakeGuestMemory memory, ulong address)
    {
        foreach (var readAddress in memory.ReadAddresses)
        {
            if (readAddress == address)
            {
                return true;
            }
        }

        return false;
    }

    private static byte[] CreateSubmitPacket(ulong commandAddress, uint dwordCount)
    {
        var packet = new byte[16];
        BinaryPrimitives.WriteUInt64LittleEndian(packet, commandAddress);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(8), dwordCount);
        return packet;
    }

    private static byte[] CreateResetPacket(uint register = DcbResetRegister)
    {
        var packet = new byte[2 * sizeof(uint)];
        BinaryPrimitives.WriteUInt32LittleEndian(
            packet,
            Pm4(2, 0x10, register));
        return packet;
    }

    private static byte[] CreateSetIndirectBasePacket()
    {
        var packet = new byte[4 * sizeof(uint)];
        BinaryPrimitives.WriteUInt32LittleEndian(packet, Pm4(4, 0x11));
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(4), 1);
        BinaryPrimitives.WriteUInt64LittleEndian(
            packet.AsSpan(8),
            IndirectArgumentsAddress);
        return packet;
    }

    private static byte[] CreateDrawIndirectPacket()
    {
        var packet = new byte[5 * sizeof(uint)];
        BinaryPrimitives.WriteUInt32LittleEndian(packet, Pm4(5, 0x24));
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(16), 1);
        return packet;
    }

    private static uint Pm4(uint dwordCount, uint opcode, uint register = 0) =>
        0xC000_0000u |
        ((dwordCount - 2) << 16) |
        (opcode << 8) |
        (register << 2);
}
