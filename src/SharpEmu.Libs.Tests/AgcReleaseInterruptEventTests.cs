// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using SharpEmu.HLE;
using SharpEmu.Libs.Agc;
using SharpEmu.Libs.Kernel;
using Xunit;

namespace SharpEmu.Libs.Tests;

public sealed class AgcReleaseInterruptEventTests
{
    private const ulong QueueHandleAddress = 0x1000;
    private const ulong SubmitAddress = 0x2000;
    private const ulong CommandAddress = 0x3000;
    private const ulong LabelAddress = 0x4000;
    private const ulong EventAddress = 0x5000;
    private const ulong EventCountAddress = 0x5100;
    private const ulong TimeoutAddress = 0x5200;

    [Theory]
    [InlineData(0x0000_0000u, 0u, 0u)]
    [InlineData(0x0001_0000u, 1u, 0u)]
    [InlineData(0x0102_0000u, 2u, 1u)]
    [InlineData(0xA503_0000u, 3u, 0xA5u)]
    public void AgcReleaseMemControlDecodesDataSelectionAndInterrupt(
        uint control,
        uint expectedDataSelection,
        uint expectedInterrupt)
    {
        var decoded = AgcExports.DecodeAgcReleaseMemControl(control);

        Assert.Equal(expectedDataSelection, decoded.DataSelection);
        Assert.Equal(expectedInterrupt, decoded.Interrupt);
    }

    [Fact]
    public void ReleaseMemInterruptRaisesTimestampedGraphicsEvent()
    {
        var memory = new FakeGuestMemory();
        memory.AddRegion(QueueHandleAddress, new byte[sizeof(ulong)]);
        memory.AddRegion(SubmitAddress, CreateSubmitPacket(CommandAddress, 8));
        memory.AddRegion(CommandAddress, CreateReleaseMemPacket(interrupt: 1));
        memory.AddRegion(LabelAddress, new byte[sizeof(uint)]);
        memory.AddRegion(EventAddress, new byte[0x20]);
        memory.AddRegion(EventCountAddress, new byte[sizeof(uint)]);
        memory.AddRegion(TimeoutAddress, new byte[sizeof(uint)]);
        var context = new CpuContext(memory, Generation.Gen5);

        context[CpuRegister.Rdi] = QueueHandleAddress;
        Assert.Equal(0, KernelEventQueueCompatExports.KernelCreateEqueue(context));
        Assert.True(context.TryReadUInt64(QueueHandleAddress, out var queueHandle));

        try
        {
            Assert.True(KernelEventQueueCompatExports.RegisterEvent(
                queueHandle,
                ident: 0,
                KernelEventQueueCompatExports.KernelEventFilterGraphics,
                userData: 0xCAFE));

            context[CpuRegister.Rdi] = SubmitAddress;
            Assert.Equal(0, AgcExports.DriverSubmitDcb(context));
            Assert.True(context.TryReadUInt32(LabelAddress, out var label));
            Assert.Equal(0x1234_5678u, label);

            context[CpuRegister.Rdi] = queueHandle;
            context[CpuRegister.Rsi] = EventAddress;
            context[CpuRegister.Rdx] = 1;
            context[CpuRegister.Rcx] = EventCountAddress;
            context[CpuRegister.R8] = TimeoutAddress;
            Assert.Equal(0, KernelEventQueueCompatExports.KernelWaitEqueue(context));
            Assert.True(context.TryReadUInt32(EventCountAddress, out var eventCount));
            Assert.Equal(1u, eventCount);
            Assert.True(context.TryReadUInt64(EventAddress + 0x10, out var eventData));
            Assert.NotEqual(0UL, eventData);
        }
        finally
        {
            context[CpuRegister.Rdi] = queueHandle;
            _ = KernelEventQueueCompatExports.KernelDeleteEqueue(context);
        }
    }

    private static byte[] CreateSubmitPacket(ulong commandAddress, uint dwordCount)
    {
        var packet = new byte[16];
        BinaryPrimitives.WriteUInt64LittleEndian(packet, commandAddress);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(8), dwordCount);
        return packet;
    }

    private static byte[] CreateReleaseMemPacket(uint interrupt)
    {
        var packet = new byte[8 * sizeof(uint)];
        BinaryPrimitives.WriteUInt32LittleEndian(packet, Pm4(8, 0x10, 0x18));
        BinaryPrimitives.WriteUInt32LittleEndian(
            packet.AsSpan(8),
            (1u << 16) | (interrupt << 24));
        BinaryPrimitives.WriteUInt64LittleEndian(packet.AsSpan(12), LabelAddress);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(20), 0x1234_5678);
        return packet;
    }

    private static uint Pm4(uint dwordCount, uint opcode, uint register) =>
        0xC000_0000u |
        ((dwordCount - 2) << 16) |
        (opcode << 8) |
        (register << 2);
}
