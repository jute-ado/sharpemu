// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using SharpEmu.HLE;
using SharpEmu.Libs.Agc;
using Xunit;

namespace SharpEmu.Libs.Tests;

public sealed class AgcSuspendPointLifecycleTests
{
    private const ulong FirstSubmitAddress = 0x1000;
    private const ulong SecondSubmitAddress = 0x1100;
    private const ulong FirstDcbAddress = 0x2000;
    private const ulong SecondDcbAddress = 0x3000;
    private const ulong IndirectArgumentsAddress = 0x4000;
    private const ulong RetrySubmitAddress = 0x1200;
    private const ulong RetryDcbAddress = 0x5000;

    [Fact]
    public void SuspendPointResetsGraphicsStateBeforeNextSubmission()
    {
        var memory = CreateMemory();
        var context = new CpuContext(memory, Generation.Gen5);

        context[CpuRegister.Rdi] = FirstSubmitAddress;
        Assert.Equal(0, AgcExports.DriverSubmitDcb(context));
        Assert.Equal(0, AgcExports.SuspendPoint(context));
        memory.ClearReadHistory();

        context[CpuRegister.Rdi] = SecondSubmitAddress;
        Assert.Equal(0, AgcExports.DriverSubmitDcb(context));

        Assert.DoesNotContain(IndirectArgumentsAddress, memory.ReadAddresses);
    }

    [Fact]
    public void ZeroDimensionIndirectDispatchWithoutQueuedProducerDoesNotStall()
    {
        var memory = new FakeGuestMemory();
        memory.AddRegion(
            RetrySubmitAddress,
            CreateSubmitPacket(RetryDcbAddress, 7));
        memory.AddRegion(RetryDcbAddress, CreateIndirectDispatchDcb());
        var arguments = new byte[3 * sizeof(uint)];
        BinaryPrimitives.WriteUInt32LittleEndian(arguments.AsSpan(4), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(arguments.AsSpan(8), 1);
        memory.AddRegion(IndirectArgumentsAddress, arguments);
        var context = new CpuContext(memory, Generation.Gen5);

        try
        {
            context[CpuRegister.Rdi] = RetrySubmitAddress;
            Assert.Equal(0, AgcExports.DriverSubmitDcb(context));
            Assert.Equal(0, GpuWaitRegistry.CountForMemory(memory));
        }
        finally
        {
            GpuWaitRegistry.CollectAllForMemory(memory);
        }
    }

    private static FakeGuestMemory CreateMemory()
    {
        var arguments = new byte[4 * sizeof(uint)];
        BinaryPrimitives.WriteUInt32LittleEndian(arguments, 3);
        BinaryPrimitives.WriteUInt32LittleEndian(arguments.AsSpan(4), 1);

        var memory = new FakeGuestMemory();
        memory.AddRegion(
            FirstSubmitAddress,
            CreateSubmitPacket(FirstDcbAddress, 4));
        memory.AddRegion(
            SecondSubmitAddress,
            CreateSubmitPacket(SecondDcbAddress, 5));
        memory.AddRegion(FirstDcbAddress, CreateSetIndirectBasePacket());
        memory.AddRegion(SecondDcbAddress, CreateDrawIndirectPacket());
        memory.AddRegion(IndirectArgumentsAddress, arguments);
        return memory;
    }

    private static byte[] CreateSubmitPacket(ulong commandAddress, uint dwordCount)
    {
        var packet = new byte[16];
        BinaryPrimitives.WriteUInt64LittleEndian(packet, commandAddress);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(8), dwordCount);
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

    private static byte[] CreateIndirectDispatchDcb()
    {
        var packet = new byte[7 * sizeof(uint)];
        BinaryPrimitives.WriteUInt32LittleEndian(packet, Pm4(4, 0x11));
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(4), 1);
        BinaryPrimitives.WriteUInt64LittleEndian(
            packet.AsSpan(8),
            IndirectArgumentsAddress);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(16), Pm4(3, 0x16));
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(20), 0);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(24), 0x41);
        return packet;
    }

    private static uint Pm4(uint dwordCount, uint opcode) =>
        0xC000_0000u |
        ((dwordCount - 2) << 16) |
        (opcode << 8);
}
