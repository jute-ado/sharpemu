// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using SharpEmu.HLE;
using SharpEmu.Libs.Kernel;
using Xunit;

namespace SharpEmu.Libs.Tests;

public sealed class KernelEventQueueCompatibilityTests
{
    private const ulong HandleAddress = 0x1000;
    private const ulong EventAddress = 0x2000;
    private const ulong CountAddress = 0x3000;
    private const ulong TimeoutAddress = 0x4000;
    private const ulong MissingEventAddress = 0x5000;
    private const ulong MissingCountAddress = 0x6000;

    [Fact]
    public void ZeroTimeoutAcceptsFourByteGuestValue()
    {
        var fixture = CreateQueue(mapEventBuffer: true);
        try
        {
            Assert.Equal(
                (int)OrbisGen2Result.ORBIS_GEN2_ERROR_TIMED_OUT,
                Wait(fixture, EventAddress));
            Assert.Equal(0u, ReadUInt32(fixture.Count));
        }
        finally
        {
            DeleteQueue(fixture);
        }
    }

    [Fact]
    public void UserEventEncodesAndCoalescesLatestNotification()
    {
        var fixture = CreateQueue(mapEventBuffer: true);
        const ulong ident = 0x1122_3344_5566_7788;
        try
        {
            Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_OK, AddUserEvent(fixture, ident));
            Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_OK, TriggerUserEvent(fixture, ident, 0xAABB));
            Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_OK, TriggerUserEvent(fixture, ident, 0xCCDD));

            Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_OK, Wait(fixture, EventAddress));
            Assert.Equal(1u, ReadUInt32(fixture.Count));
            Assert.Equal(ident, BinaryPrimitives.ReadUInt64LittleEndian(fixture.Events));
            Assert.Equal(
                KernelEventQueueCompatExports.KernelEventFilterUser,
                BinaryPrimitives.ReadInt16LittleEndian(fixture.Events.AsSpan(0x08)));
            Assert.Equal(0x21, BinaryPrimitives.ReadUInt16LittleEndian(fixture.Events.AsSpan(0x0A)));
            Assert.Equal(1u, BinaryPrimitives.ReadUInt32LittleEndian(fixture.Events.AsSpan(0x0C)));
            Assert.Equal(0xCCDDUL, BinaryPrimitives.ReadUInt64LittleEndian(fixture.Events.AsSpan(0x10)));
            Assert.Equal(0UL, BinaryPrimitives.ReadUInt64LittleEndian(fixture.Events.AsSpan(0x18)));
        }
        finally
        {
            DeleteQueue(fixture);
        }
    }

    [Fact]
    public void DeleteUserEventRemovesPendingNotification()
    {
        var fixture = CreateQueue(mapEventBuffer: true);
        const ulong ident = 0x55;
        try
        {
            Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_OK, AddUserEvent(fixture, ident));
            Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_OK, TriggerUserEvent(fixture, ident, 0xAA));
            Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_OK, DeleteUserEvent(fixture, ident));

            Assert.Equal(
                (int)OrbisGen2Result.ORBIS_GEN2_ERROR_TIMED_OUT,
                Wait(fixture, EventAddress));
            Assert.Equal(0u, ReadUInt32(fixture.Count));
            Assert.Equal(
                (int)OrbisGen2Result.ORBIS_GEN2_ERROR_NOT_FOUND,
                TriggerUserEvent(fixture, ident, 0xBB));
        }
        finally
        {
            DeleteQueue(fixture);
        }
    }

    [Fact]
    public void FailedEventCopyoutPreservesPendingNotification()
    {
        var fixture = CreateQueue(mapEventBuffer: false);
        const ulong ident = 0x77;
        try
        {
            Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_OK, AddUserEvent(fixture, ident));
            Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_OK, TriggerUserEvent(fixture, ident, 0x1234));

            Assert.Equal(
                (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT,
                Wait(fixture, MissingEventAddress));
            Assert.Equal(0u, ReadUInt32(fixture.Count));

            fixture.Memory.AddRegion(EventAddress, fixture.Events);
            Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_OK, Wait(fixture, EventAddress));
            Assert.Equal(1u, ReadUInt32(fixture.Count));
            Assert.Equal(ident, BinaryPrimitives.ReadUInt64LittleEndian(fixture.Events));
            Assert.Equal(0x1234UL, BinaryPrimitives.ReadUInt64LittleEndian(fixture.Events.AsSpan(0x10)));
        }
        finally
        {
            DeleteQueue(fixture);
        }
    }

    [Fact]
    public void FailedCountCopyoutPreservesPendingNotification()
    {
        var fixture = CreateQueue(mapEventBuffer: true);
        const ulong ident = 0x88;
        try
        {
            Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_OK, AddUserEvent(fixture, ident));
            Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_OK, TriggerUserEvent(fixture, ident, 0x5678));

            Assert.Equal(
                (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT,
                Wait(fixture, EventAddress, MissingCountAddress));

            Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_OK, Wait(fixture, EventAddress));
            Assert.Equal(1u, ReadUInt32(fixture.Count));
            Assert.Equal(ident, BinaryPrimitives.ReadUInt64LittleEndian(fixture.Events));
            Assert.Equal(0x5678UL, BinaryPrimitives.ReadUInt64LittleEndian(fixture.Events.AsSpan(0x10)));
        }
        finally
        {
            DeleteQueue(fixture);
        }
    }

    private static QueueFixture CreateQueue(bool mapEventBuffer)
    {
        var memory = new FakeGuestMemory();
        var handle = new byte[sizeof(ulong)];
        var events = new byte[0x40];
        var count = new byte[sizeof(uint)];
        var timeout = new byte[sizeof(uint)];
        memory.AddRegion(HandleAddress, handle);
        if (mapEventBuffer)
        {
            memory.AddRegion(EventAddress, events);
        }

        memory.AddRegion(CountAddress, count);
        memory.AddRegion(TimeoutAddress, timeout);
        var context = new CpuContext(memory, Generation.Gen5);
        context[CpuRegister.Rdi] = HandleAddress;
        Assert.Equal(
            (int)OrbisGen2Result.ORBIS_GEN2_OK,
            KernelEventQueueCompatExports.KernelCreateEqueue(context));
        return new QueueFixture(
            context,
            memory,
            BinaryPrimitives.ReadUInt64LittleEndian(handle),
            events,
            count);
    }

    private static int AddUserEvent(QueueFixture fixture, ulong ident)
    {
        fixture.Context[CpuRegister.Rdi] = fixture.Handle;
        fixture.Context[CpuRegister.Rsi] = ident;
        return KernelEventQueueCompatExports.KernelAddUserEvent(fixture.Context);
    }

    private static int TriggerUserEvent(QueueFixture fixture, ulong ident, ulong data)
    {
        fixture.Context[CpuRegister.Rdi] = fixture.Handle;
        fixture.Context[CpuRegister.Rsi] = ident;
        fixture.Context[CpuRegister.Rdx] = data;
        return KernelEventQueueCompatExports.KernelTriggerUserEvent(fixture.Context);
    }

    private static int DeleteUserEvent(QueueFixture fixture, ulong ident)
    {
        fixture.Context[CpuRegister.Rdi] = fixture.Handle;
        fixture.Context[CpuRegister.Rsi] = ident;
        return KernelEventQueueCompatExports.KernelDeleteUserEvent(fixture.Context);
    }

    private static int Wait(
        QueueFixture fixture,
        ulong eventsAddress,
        ulong countAddress = CountAddress)
    {
        fixture.Context[CpuRegister.Rdi] = fixture.Handle;
        fixture.Context[CpuRegister.Rsi] = eventsAddress;
        fixture.Context[CpuRegister.Rdx] = 1;
        fixture.Context[CpuRegister.Rcx] = countAddress;
        fixture.Context[CpuRegister.R8] = TimeoutAddress;
        return KernelEventQueueCompatExports.KernelWaitEqueue(fixture.Context);
    }

    private static void DeleteQueue(QueueFixture fixture)
    {
        fixture.Context[CpuRegister.Rdi] = fixture.Handle;
        _ = KernelEventQueueCompatExports.KernelDeleteEqueue(fixture.Context);
    }

    private static uint ReadUInt32(byte[] value) =>
        BinaryPrimitives.ReadUInt32LittleEndian(value);

    private sealed record QueueFixture(
        CpuContext Context,
        FakeGuestMemory Memory,
        ulong Handle,
        byte[] Events,
        byte[] Count);
}
