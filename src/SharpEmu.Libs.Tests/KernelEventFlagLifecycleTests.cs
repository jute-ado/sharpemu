// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using System.Diagnostics;
using System.Text;
using SharpEmu.HLE;
using SharpEmu.Libs.Kernel;
using Xunit;

namespace SharpEmu.Libs.Tests;

public sealed class KernelEventFlagLifecycleTests
{
    private const ulong NameAddress = 0x1000;
    private const ulong HandleAddress = 0x2000;
    private const ulong ResultAddress = 0x3000;
    private const ulong WaiterCountAddress = 0x4000;
    private const ulong TimeoutAddress = 0x5000;

    [Fact]
    public void DeleteReleasesCooperativeWaitWithDeletedResult()
    {
        var memory = new FakeGuestMemory();
        memory.AddRegion(NameAddress, Encoding.UTF8.GetBytes("delete-wake\0"));
        var handleBytes = new byte[sizeof(ulong)];
        var resultBytes = new byte[sizeof(ulong)];
        memory.AddRegion(HandleAddress, handleBytes);
        memory.AddRegion(ResultAddress, resultBytes);
        var context = new CpuContext(memory, Generation.Gen5);
        context[CpuRegister.Rdi] = HandleAddress;
        context[CpuRegister.Rsi] = NameAddress;
        context[CpuRegister.Rdx] = 0;
        context[CpuRegister.Rcx] = 0;
        context[CpuRegister.R8] = 0;
        Assert.Equal(
            (int)OrbisGen2Result.ORBIS_GEN2_OK,
            KernelEventFlagCompatExports.KernelCreateEventFlag(context));
        var handle = BinaryPrimitives.ReadUInt64LittleEndian(handleBytes);
        var previousGuestThread = GuestThreadExecution.EnterGuestThread(0x1234);

        try
        {
            context[CpuRegister.Rdi] = handle;
            context[CpuRegister.Rsi] = 1;
            context[CpuRegister.Rdx] = 2;
            context[CpuRegister.Rcx] = ResultAddress;
            context[CpuRegister.R8] = 0;
            Assert.Equal(
                (int)OrbisGen2Result.ORBIS_GEN2_OK,
                KernelEventFlagCompatExports.KernelWaitEventFlag(context));
            Assert.True(GuestThreadExecution.TryConsumeCurrentThreadBlock(
                out var reason,
                out _,
                out _,
                out var wakeKey,
                out IGuestThreadBlockWaiter? waiter,
                out _));
            Assert.Equal("sceKernelWaitEventFlag", reason);
            Assert.Equal($"event_flag:0x{handle:X16}", wakeKey);
            Assert.NotNull(waiter);

            context[CpuRegister.Rdi] = handle;
            Assert.Equal(
                (int)OrbisGen2Result.ORBIS_GEN2_OK,
                KernelEventFlagCompatExports.KernelDeleteEventFlag(context));
            Assert.True(waiter!.TryWake());
            Assert.Equal(
                (int)OrbisGen2Result.ORBIS_GEN2_ERROR_DELETED,
                waiter!.Resume());
            Assert.Equal(0UL, BinaryPrimitives.ReadUInt64LittleEndian(resultBytes));
        }
        finally
        {
            GuestThreadExecution.RestoreGuestThread(previousGuestThread);
            context[CpuRegister.Rdi] = handle;
            _ = KernelEventFlagCompatExports.KernelDeleteEventFlag(context);
        }
    }

    [Fact]
    public void CancelReleasesCooperativeWaitWithCanceledResult()
    {
        var memory = new FakeGuestMemory();
        memory.AddRegion(NameAddress, Encoding.UTF8.GetBytes("cancel-wake\0"));
        var handleBytes = new byte[sizeof(ulong)];
        var resultBytes = new byte[sizeof(ulong)];
        var waiterCountBytes = new byte[sizeof(uint)];
        memory.AddRegion(HandleAddress, handleBytes);
        memory.AddRegion(ResultAddress, resultBytes);
        memory.AddRegion(WaiterCountAddress, waiterCountBytes);
        var context = new CpuContext(memory, Generation.Gen5);
        context[CpuRegister.Rdi] = HandleAddress;
        context[CpuRegister.Rsi] = NameAddress;
        context[CpuRegister.Rdx] = 0;
        context[CpuRegister.Rcx] = 0;
        context[CpuRegister.R8] = 0;
        Assert.Equal(
            (int)OrbisGen2Result.ORBIS_GEN2_OK,
            KernelEventFlagCompatExports.KernelCreateEventFlag(context));
        var handle = BinaryPrimitives.ReadUInt64LittleEndian(handleBytes);
        var previousGuestThread = GuestThreadExecution.EnterGuestThread(0x5678);

        try
        {
            context[CpuRegister.Rdi] = handle;
            context[CpuRegister.Rsi] = 1;
            context[CpuRegister.Rdx] = 2;
            context[CpuRegister.Rcx] = ResultAddress;
            context[CpuRegister.R8] = 0;
            Assert.Equal(
                (int)OrbisGen2Result.ORBIS_GEN2_OK,
                KernelEventFlagCompatExports.KernelWaitEventFlag(context));
            Assert.True(GuestThreadExecution.TryConsumeCurrentThreadBlock(
                out _,
                out _,
                out _,
                out _,
                out IGuestThreadBlockWaiter? waiter,
                out _));
            Assert.NotNull(waiter);

            context[CpuRegister.Rdi] = handle;
            context[CpuRegister.Rsi] = 0x80;
            context[CpuRegister.Rdx] = WaiterCountAddress;
            Assert.Equal(
                (int)OrbisGen2Result.ORBIS_GEN2_OK,
                KernelEventFlagCompatExports.KernelCancelEventFlag(context));
            Assert.Equal(1u, BinaryPrimitives.ReadUInt32LittleEndian(waiterCountBytes));
            Assert.True(waiter!.TryWake());
            Assert.Equal(
                (int)OrbisGen2Result.ORBIS_GEN2_ERROR_CANCELED,
                waiter!.Resume());
        }
        finally
        {
            GuestThreadExecution.RestoreGuestThread(previousGuestThread);
            context[CpuRegister.Rdi] = handle;
            _ = KernelEventFlagCompatExports.KernelDeleteEventFlag(context);
        }
    }

    [Fact]
    public void TimedWaitWakesBeforeDeadlineAndUpdatesRemainingTimeout()
    {
        const uint timeoutMicros = 5_000_000;
        var fixture = CreateTimedFixture("timed-wake", timeoutMicros);
        var previousGuestThread = GuestThreadExecution.EnterGuestThread(0x9ABC);
        try
        {
            BeginTimedWait(fixture);
            Assert.True(GuestThreadExecution.TryConsumeCurrentThreadBlock(
                out var reason,
                out _,
                out _,
                out _,
                out IGuestThreadBlockWaiter? waiter,
                out var deadlineTimestamp));
            Assert.Equal("sceKernelWaitEventFlag", reason);
            Assert.NotNull(waiter);
            Assert.True(deadlineTimestamp > Stopwatch.GetTimestamp());

            fixture.Context[CpuRegister.Rdi] = fixture.Handle;
            fixture.Context[CpuRegister.Rsi] = 1;
            Assert.Equal(
                (int)OrbisGen2Result.ORBIS_GEN2_OK,
                KernelEventFlagCompatExports.KernelSetEventFlag(fixture.Context));
            Assert.True(waiter!.TryWake());
            Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_OK, waiter!.Resume());
            Assert.InRange(
                BinaryPrimitives.ReadUInt32LittleEndian(fixture.Timeout),
                1u,
                timeoutMicros);
            Assert.Equal(1UL, BinaryPrimitives.ReadUInt64LittleEndian(fixture.Result));
        }
        finally
        {
            GuestThreadExecution.RestoreGuestThread(previousGuestThread);
            DeleteFixture(fixture);
        }
    }

    [Fact]
    public void TimedWaitExpiresWithoutConsumingFutureSignal()
    {
        var fixture = CreateTimedFixture("timed-expire", 1_000_000);
        var previousGuestThread = GuestThreadExecution.EnterGuestThread(0xDEF0);
        try
        {
            BeginTimedWait(fixture);
            Assert.True(GuestThreadExecution.TryConsumeCurrentThreadBlock(
                out _,
                out _,
                out _,
                out _,
                out IGuestThreadBlockWaiter? waiter,
                out _));
            Assert.NotNull(waiter);
            Assert.False(waiter!.TryWake());
            Assert.Equal(
                (int)OrbisGen2Result.ORBIS_GEN2_ERROR_TIMED_OUT,
                waiter!.Resume());
            Assert.Equal(0u, BinaryPrimitives.ReadUInt32LittleEndian(fixture.Timeout));
            Assert.Equal(0UL, BinaryPrimitives.ReadUInt64LittleEndian(fixture.Result));

            fixture.Context[CpuRegister.Rdi] = fixture.Handle;
            fixture.Context[CpuRegister.Rsi] = 1;
            Assert.Equal(
                (int)OrbisGen2Result.ORBIS_GEN2_OK,
                KernelEventFlagCompatExports.KernelSetEventFlag(fixture.Context));
            fixture.Context[CpuRegister.Rdi] = fixture.Handle;
            fixture.Context[CpuRegister.Rsi] = 1;
            fixture.Context[CpuRegister.Rdx] = 2;
            fixture.Context[CpuRegister.Rcx] = ResultAddress;
            Assert.Equal(
                (int)OrbisGen2Result.ORBIS_GEN2_OK,
                KernelEventFlagCompatExports.KernelPollEventFlag(fixture.Context));
        }
        finally
        {
            GuestThreadExecution.RestoreGuestThread(previousGuestThread);
            DeleteFixture(fixture);
        }
    }

    [Fact]
    public void TimedWaitTimeoutCopyoutFailurePreservesClearPatternBits()
    {
        var fixture = CreateTimedFixture("timed-copyout", 100_000);
        try
        {
            var previousGuestThread = GuestThreadExecution.EnterGuestThread(0xFEDC);
            try
            {
                BeginTimedWait(fixture, waitMode: 0x22);
                Assert.True(GuestThreadExecution.TryConsumeCurrentThreadBlock(
                    out _,
                    out _,
                    out _,
                    out _,
                    out IGuestThreadBlockWaiter? waiter,
                    out _));
                Assert.NotNull(waiter);

                Assert.True(fixture.Memory.RemoveRegion(TimeoutAddress));
                fixture.Context[CpuRegister.Rdi] = fixture.Handle;
                fixture.Context[CpuRegister.Rsi] = 1;
                Assert.Equal(
                    (int)OrbisGen2Result.ORBIS_GEN2_OK,
                    KernelEventFlagCompatExports.KernelSetEventFlag(fixture.Context));
                Assert.True(waiter!.TryWake());
                Assert.Equal(
                    (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT,
                    waiter!.Resume());
            }
            finally
            {
                GuestThreadExecution.RestoreGuestThread(previousGuestThread);
            }

            fixture.Context[CpuRegister.Rdi] = fixture.Handle;
            fixture.Context[CpuRegister.Rsi] = 1;
            fixture.Context[CpuRegister.Rdx] = 2;
            fixture.Context[CpuRegister.Rcx] = ResultAddress;
            Assert.Equal(
                (int)OrbisGen2Result.ORBIS_GEN2_OK,
                KernelEventFlagCompatExports.KernelPollEventFlag(fixture.Context));
            Assert.Equal(1UL, BinaryPrimitives.ReadUInt64LittleEndian(fixture.Result));
        }
        finally
        {
            DeleteFixture(fixture);
        }
    }

    private static TimedFixture CreateTimedFixture(string name, uint timeoutMicros)
    {
        var memory = new FakeGuestMemory();
        memory.AddRegion(NameAddress, Encoding.UTF8.GetBytes($"{name}\0"));
        var handleBytes = new byte[sizeof(ulong)];
        var resultBytes = new byte[sizeof(ulong)];
        var timeoutBytes = new byte[sizeof(uint)];
        BinaryPrimitives.WriteUInt32LittleEndian(timeoutBytes, timeoutMicros);
        memory.AddRegion(HandleAddress, handleBytes);
        memory.AddRegion(ResultAddress, resultBytes);
        memory.AddRegion(TimeoutAddress, timeoutBytes);
        var context = new CpuContext(memory, Generation.Gen5);
        context[CpuRegister.Rdi] = HandleAddress;
        context[CpuRegister.Rsi] = NameAddress;
        context[CpuRegister.Rdx] = 0;
        context[CpuRegister.Rcx] = 0;
        context[CpuRegister.R8] = 0;
        Assert.Equal(
            (int)OrbisGen2Result.ORBIS_GEN2_OK,
            KernelEventFlagCompatExports.KernelCreateEventFlag(context));
        return new TimedFixture(
            context,
            memory,
            BinaryPrimitives.ReadUInt64LittleEndian(handleBytes),
            resultBytes,
            timeoutBytes);
    }

    private static void BeginTimedWait(TimedFixture fixture, uint waitMode = 2)
    {
        fixture.Context[CpuRegister.Rdi] = fixture.Handle;
        fixture.Context[CpuRegister.Rsi] = 1;
        fixture.Context[CpuRegister.Rdx] = waitMode;
        fixture.Context[CpuRegister.Rcx] = ResultAddress;
        fixture.Context[CpuRegister.R8] = TimeoutAddress;
        Assert.Equal(
            (int)OrbisGen2Result.ORBIS_GEN2_OK,
            KernelEventFlagCompatExports.KernelWaitEventFlag(fixture.Context));
    }

    private static void DeleteFixture(TimedFixture fixture)
    {
        fixture.Context[CpuRegister.Rdi] = fixture.Handle;
        _ = KernelEventFlagCompatExports.KernelDeleteEventFlag(fixture.Context);
    }

    private sealed record TimedFixture(
        CpuContext Context,
        FakeGuestMemory Memory,
        ulong Handle,
        byte[] Result,
        byte[] Timeout);
}
