// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using System.Diagnostics;
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
    public void FailedCreateDoesNotPublishQueue()
    {
        var memory = new FakeGuestMemory();
        var context = new CpuContext(memory, Generation.Gen5);
        context[CpuRegister.Rdi] = HandleAddress;

        Assert.Equal(
            (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT,
            KernelEventQueueCompatExports.KernelCreateEqueue(context));

        var handleBytes = new byte[sizeof(ulong)];
        memory.AddRegion(HandleAddress, handleBytes);
        Assert.Equal(
            (int)OrbisGen2Result.ORBIS_GEN2_OK,
            KernelEventQueueCompatExports.KernelCreateEqueue(context));
        var handle = BinaryPrimitives.ReadUInt64LittleEndian(handleBytes);
        try
        {
            Assert.False(KernelEventQueueCompatExports.IsValidEqueue(handle - 1));
        }
        finally
        {
            context[CpuRegister.Rdi] = handle;
            _ = KernelEventQueueCompatExports.KernelDeleteEqueue(context);
        }
    }

    [Fact]
    public void DeleteUnknownQueueReturnsNotFound()
    {
        var context = new CpuContext(new FakeGuestMemory(), Generation.Gen5);
        context[CpuRegister.Rdi] = ulong.MaxValue;

        Assert.Equal(
            (int)OrbisGen2Result.ORBIS_GEN2_ERROR_NOT_FOUND,
            KernelEventQueueCompatExports.KernelDeleteEqueue(context));
    }

    [Fact]
    public void DeleteQueueReleasesCooperativeWaitWithNotFound()
    {
        var fixture = CreateQueue(mapEventBuffer: true);
        var previousGuestThread = GuestThreadExecution.EnterGuestThread(0x1234);
        try
        {
            fixture.Context[CpuRegister.Rdi] = fixture.Handle;
            fixture.Context[CpuRegister.Rsi] = EventAddress;
            fixture.Context[CpuRegister.Rdx] = 1;
            fixture.Context[CpuRegister.Rcx] = CountAddress;
            fixture.Context[CpuRegister.R8] = 0;
            Assert.Equal(
                (int)OrbisGen2Result.ORBIS_GEN2_OK,
                KernelEventQueueCompatExports.KernelWaitEqueue(fixture.Context));
            Assert.True(GuestThreadExecution.TryConsumeCurrentThreadBlock(
                out var reason,
                out _,
                out _,
                out var wakeKey,
                out var resumeHandler,
                out var wakeHandler,
                out _));
            Assert.Equal("sceKernelWaitEqueue", reason);
            Assert.Equal($"sceKernelWaitEqueue:{fixture.Handle:X16}", wakeKey);
            Assert.NotNull(resumeHandler);
            Assert.NotNull(wakeHandler);

            Assert.Equal(
                (int)OrbisGen2Result.ORBIS_GEN2_OK,
                DeleteQueue(fixture));
            Assert.True(wakeHandler!());
            Assert.Equal(
                (int)OrbisGen2Result.ORBIS_GEN2_ERROR_NOT_FOUND,
                resumeHandler!());
            Assert.Equal(0u, ReadUInt32(fixture.Count));
        }
        finally
        {
            GuestThreadExecution.RestoreGuestThread(previousGuestThread);
            if (KernelEventQueueCompatExports.IsValidEqueue(fixture.Handle))
            {
                _ = DeleteQueue(fixture);
            }
        }
    }

    [Fact]
    public async Task DeleteQueueInterruptsHostTimedWait()
    {
        var fixture = CreateQueue(mapEventBuffer: true);
        BinaryPrimitives.WriteUInt32LittleEndian(fixture.Timeout, 30_000_000);
        var waiterContext = new CpuContext(fixture.Memory, Generation.Gen5);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            var waitTask = Task.Run(() =>
            {
                started.SetResult();
                return Wait(fixture, EventAddress, context: waiterContext);
            });
            await started.Task.WaitAsync(TimeSpan.FromSeconds(1));
            await Task.Delay(50);
            var stopwatch = Stopwatch.StartNew();

            Assert.Equal(
                (int)OrbisGen2Result.ORBIS_GEN2_OK,
                DeleteQueue(fixture));
            var waitResult = await waitTask.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(
                (int)OrbisGen2Result.ORBIS_GEN2_ERROR_NOT_FOUND,
                waitResult);
            Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(5));
        }
        finally
        {
            if (KernelEventQueueCompatExports.IsValidEqueue(fixture.Handle))
            {
                _ = DeleteQueue(fixture);
            }
        }
    }

    [Fact]
    public void TimedGuestWaitWakesBeforeDeadlineAndUpdatesRemainingTimeout()
    {
        const uint timeoutMicros = 100_000;
        var fixture = CreateQueue(mapEventBuffer: true);
        BinaryPrimitives.WriteUInt32LittleEndian(fixture.Timeout, timeoutMicros);
        const ulong ident = 0x99;
        var previousGuestThread = GuestThreadExecution.EnterGuestThread(0x2468);
        try
        {
            Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_OK, AddUserEvent(fixture, ident));
            Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_OK, Wait(fixture, EventAddress));
            Assert.True(GuestThreadExecution.TryConsumeCurrentThreadBlock(
                out var reason,
                out _,
                out _,
                out _,
                out var resumeHandler,
                out var wakeHandler,
                out var deadlineTimestamp));
            Assert.Equal("sceKernelWaitEqueue", reason);
            Assert.NotNull(resumeHandler);
            Assert.NotNull(wakeHandler);
            Assert.True(deadlineTimestamp > Stopwatch.GetTimestamp());

            Assert.Equal(
                (int)OrbisGen2Result.ORBIS_GEN2_OK,
                TriggerUserEvent(fixture, ident, 0x9876));
            Assert.True(wakeHandler!());
            Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_OK, resumeHandler!());
            Assert.InRange(
                BinaryPrimitives.ReadUInt32LittleEndian(fixture.Timeout),
                1u,
                timeoutMicros);
            Assert.Equal(1u, ReadUInt32(fixture.Count));
            Assert.Equal(ident, BinaryPrimitives.ReadUInt64LittleEndian(fixture.Events));
            Assert.Equal(0x9876UL, BinaryPrimitives.ReadUInt64LittleEndian(fixture.Events.AsSpan(0x10)));
        }
        finally
        {
            GuestThreadExecution.RestoreGuestThread(previousGuestThread);
            DeleteQueue(fixture);
        }
    }

    [Fact]
    public void TimedGuestWaitExpiresWithoutConsumingFutureEvent()
    {
        var fixture = CreateQueue(mapEventBuffer: true);
        BinaryPrimitives.WriteUInt32LittleEndian(fixture.Timeout, 10_000);
        const ulong ident = 0xAA;
        var previousGuestThread = GuestThreadExecution.EnterGuestThread(0x1357);
        try
        {
            Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_OK, AddUserEvent(fixture, ident));
            Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_OK, Wait(fixture, EventAddress));
            Assert.True(GuestThreadExecution.TryConsumeCurrentThreadBlock(
                out _,
                out _,
                out _,
                out _,
                out var resumeHandler,
                out var wakeHandler,
                out _));
            Assert.NotNull(resumeHandler);
            Assert.NotNull(wakeHandler);
            Assert.False(wakeHandler!());
            Assert.Equal(
                (int)OrbisGen2Result.ORBIS_GEN2_ERROR_TIMED_OUT,
                resumeHandler!());
            Assert.Equal(0u, BinaryPrimitives.ReadUInt32LittleEndian(fixture.Timeout));
            Assert.Equal(0u, ReadUInt32(fixture.Count));

            Assert.Equal(
                (int)OrbisGen2Result.ORBIS_GEN2_OK,
                TriggerUserEvent(fixture, ident, 0xABCD));
            Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_OK, Wait(fixture, EventAddress));
            Assert.Equal(1u, ReadUInt32(fixture.Count));
            Assert.Equal(0xABCDUL, BinaryPrimitives.ReadUInt64LittleEndian(fixture.Events.AsSpan(0x10)));
        }
        finally
        {
            GuestThreadExecution.RestoreGuestThread(previousGuestThread);
            DeleteQueue(fixture);
        }
    }

    [Fact]
    public void TimedGuestWaitTimeoutCopyoutFailurePreservesPendingEvent()
    {
        const uint timeoutMicros = 100_000;
        const ulong ident = 0xBB;
        var fixture = CreateQueue(mapEventBuffer: true);
        BinaryPrimitives.WriteUInt32LittleEndian(fixture.Timeout, timeoutMicros);
        try
        {
            var previousGuestThread = GuestThreadExecution.EnterGuestThread(0xACE0);
            try
            {
                Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_OK, AddUserEvent(fixture, ident));
                Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_OK, Wait(fixture, EventAddress));
                Assert.True(GuestThreadExecution.TryConsumeCurrentThreadBlock(
                    out _,
                    out _,
                    out _,
                    out _,
                    out var resumeHandler,
                    out var wakeHandler,
                    out _));
                Assert.NotNull(resumeHandler);
                Assert.NotNull(wakeHandler);

                Assert.True(fixture.Memory.RemoveRegion(TimeoutAddress));
                Assert.Equal(
                    (int)OrbisGen2Result.ORBIS_GEN2_OK,
                    TriggerUserEvent(fixture, ident, 0xBCDE));
                Assert.True(wakeHandler!());
                Assert.Equal(
                    (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT,
                    resumeHandler!());
            }
            finally
            {
                GuestThreadExecution.RestoreGuestThread(previousGuestThread);
            }

            fixture.Memory.AddRegion(TimeoutAddress, fixture.Timeout);
            BinaryPrimitives.WriteUInt32LittleEndian(fixture.Timeout, 0);
            BinaryPrimitives.WriteUInt32LittleEndian(fixture.Count, 0);

            Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_OK, Wait(fixture, EventAddress));
            Assert.Equal(1u, ReadUInt32(fixture.Count));
            Assert.Equal(ident, BinaryPrimitives.ReadUInt64LittleEndian(fixture.Events));
            Assert.Equal(0xBCDEUL, BinaryPrimitives.ReadUInt64LittleEndian(fixture.Events.AsSpan(0x10)));
        }
        finally
        {
            if (KernelEventQueueCompatExports.IsValidEqueue(fixture.Handle))
            {
                DeleteQueue(fixture);
            }
        }
    }

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
            count,
            timeout);
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
        ulong countAddress = CountAddress,
        CpuContext? context = null)
    {
        context ??= fixture.Context;
        context[CpuRegister.Rdi] = fixture.Handle;
        context[CpuRegister.Rsi] = eventsAddress;
        context[CpuRegister.Rdx] = 1;
        context[CpuRegister.Rcx] = countAddress;
        context[CpuRegister.R8] = TimeoutAddress;
        return KernelEventQueueCompatExports.KernelWaitEqueue(context);
    }

    private static int DeleteQueue(QueueFixture fixture)
    {
        fixture.Context[CpuRegister.Rdi] = fixture.Handle;
        return KernelEventQueueCompatExports.KernelDeleteEqueue(fixture.Context);
    }

    private static uint ReadUInt32(byte[] value) =>
        BinaryPrimitives.ReadUInt32LittleEndian(value);

    private sealed record QueueFixture(
        CpuContext Context,
        FakeGuestMemory Memory,
        ulong Handle,
        byte[] Events,
        byte[] Count,
        byte[] Timeout);
}
