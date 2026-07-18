// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
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
    public async Task DeleteReleasesInPlaceWaitWithDeletedResult()
    {
        var fixture = CreateFixture("delete-wake");
        try
        {
            var wait = StartWait(fixture, 0x1234);
            AssertWaitBlocked(0x1234);

            var control = CreateContext(fixture.Memory);
            control[CpuRegister.Rdi] = fixture.Handle;
            Assert.Equal(
                (int)OrbisGen2Result.ORBIS_GEN2_OK,
                KernelEventFlagCompatExports.KernelDeleteEventFlag(control));

            Assert.Equal(
                (int)OrbisGen2Result.ORBIS_GEN2_ERROR_DELETED,
                await wait.WaitAsync(TimeSpan.FromSeconds(1)));
            Assert.Equal(0UL, BinaryPrimitives.ReadUInt64LittleEndian(fixture.Result));
        }
        finally
        {
            DeleteFixture(fixture);
        }
    }

    [Fact]
    public async Task CancelReleasesInPlaceWaitWithCanceledResult()
    {
        var fixture = CreateFixture("cancel-wake");
        var waiterCount = new byte[sizeof(uint)];
        fixture.Memory.AddRegion(WaiterCountAddress, waiterCount);
        try
        {
            var wait = StartWait(fixture, 0x5678);
            AssertWaitBlocked(0x5678);

            var control = CreateContext(fixture.Memory);
            control[CpuRegister.Rdi] = fixture.Handle;
            control[CpuRegister.Rsi] = 0x80;
            control[CpuRegister.Rdx] = WaiterCountAddress;
            Assert.Equal(
                (int)OrbisGen2Result.ORBIS_GEN2_OK,
                KernelEventFlagCompatExports.KernelCancelEventFlag(control));

            Assert.Equal(1u, BinaryPrimitives.ReadUInt32LittleEndian(waiterCount));
            Assert.Equal(
                (int)OrbisGen2Result.ORBIS_GEN2_ERROR_CANCELED,
                await wait.WaitAsync(TimeSpan.FromSeconds(1)));
        }
        finally
        {
            DeleteFixture(fixture);
        }
    }

    [Fact]
    public async Task TimedWaitWakesBeforeDeadlineAndUpdatesRemainingTimeout()
    {
        const uint timeoutMicros = 5_000_000;
        var fixture = CreateFixture("timed-wake", timeoutMicros);
        try
        {
            var wait = StartWait(fixture, 0x9ABC, timed: true);
            AssertWaitBlocked(0x9ABC);
            SetFlag(fixture);

            Assert.Equal(
                (int)OrbisGen2Result.ORBIS_GEN2_OK,
                await wait.WaitAsync(TimeSpan.FromSeconds(1)));
            Assert.InRange(
                BinaryPrimitives.ReadUInt32LittleEndian(fixture.Timeout!),
                1u,
                timeoutMicros);
            Assert.Equal(1UL, BinaryPrimitives.ReadUInt64LittleEndian(fixture.Result));
        }
        finally
        {
            DeleteFixture(fixture);
        }
    }

    [Fact]
    public async Task TimedWaitExpiresWithoutConsumingFutureSignal()
    {
        var fixture = CreateFixture("timed-expire", 20_000);
        try
        {
            Assert.Equal(
                (int)OrbisGen2Result.ORBIS_GEN2_ERROR_TIMED_OUT,
                await StartWait(fixture, 0xDEF0, timed: true)
                    .WaitAsync(TimeSpan.FromSeconds(1)));
            Assert.Equal(0u, BinaryPrimitives.ReadUInt32LittleEndian(fixture.Timeout!));
            Assert.Equal(0UL, BinaryPrimitives.ReadUInt64LittleEndian(fixture.Result));

            SetFlag(fixture);
            var poll = CreateContext(fixture.Memory);
            poll[CpuRegister.Rdi] = fixture.Handle;
            poll[CpuRegister.Rsi] = 1;
            poll[CpuRegister.Rdx] = 2;
            poll[CpuRegister.Rcx] = ResultAddress;
            Assert.Equal(
                (int)OrbisGen2Result.ORBIS_GEN2_OK,
                KernelEventFlagCompatExports.KernelPollEventFlag(poll));
        }
        finally
        {
            DeleteFixture(fixture);
        }
    }

    [Fact]
    public async Task TimeoutCopyoutFailurePreservesClearPatternBits()
    {
        var fixture = CreateFixture("timed-copyout", 5_000_000);
        try
        {
            var wait = StartWait(
                fixture,
                0xFEDC,
                timed: true,
                waitMode: 0x22);
            AssertWaitBlocked(0xFEDC);
            Assert.True(fixture.Memory.RemoveRegion(TimeoutAddress));
            SetFlag(fixture);

            Assert.Equal(
                (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT,
                await wait.WaitAsync(TimeSpan.FromSeconds(1)));

            var poll = CreateContext(fixture.Memory);
            poll[CpuRegister.Rdi] = fixture.Handle;
            poll[CpuRegister.Rsi] = 1;
            poll[CpuRegister.Rdx] = 2;
            poll[CpuRegister.Rcx] = ResultAddress;
            Assert.Equal(
                (int)OrbisGen2Result.ORBIS_GEN2_OK,
                KernelEventFlagCompatExports.KernelPollEventFlag(poll));
            Assert.Equal(1UL, BinaryPrimitives.ReadUInt64LittleEndian(fixture.Result));
        }
        finally
        {
            DeleteFixture(fixture);
        }
    }

    private static Fixture CreateFixture(string name, uint? timeoutMicros = null)
    {
        GuestThreadBlocking.BeginExecution();
        var memory = new FakeGuestMemory();
        memory.AddRegion(NameAddress, Encoding.UTF8.GetBytes($"{name}\0"));
        var handle = new byte[sizeof(ulong)];
        var result = new byte[sizeof(ulong)];
        memory.AddRegion(HandleAddress, handle);
        memory.AddRegion(ResultAddress, result);
        byte[]? timeout = null;
        if (timeoutMicros is { } value)
        {
            timeout = new byte[sizeof(uint)];
            BinaryPrimitives.WriteUInt32LittleEndian(timeout, value);
            memory.AddRegion(TimeoutAddress, timeout);
        }

        var context = CreateContext(memory);
        context[CpuRegister.Rdi] = HandleAddress;
        context[CpuRegister.Rsi] = NameAddress;
        context[CpuRegister.Rdx] = 0;
        context[CpuRegister.Rcx] = 0;
        context[CpuRegister.R8] = 0;
        Assert.Equal(
            (int)OrbisGen2Result.ORBIS_GEN2_OK,
            KernelEventFlagCompatExports.KernelCreateEventFlag(context));
        return new Fixture(
            memory,
            BinaryPrimitives.ReadUInt64LittleEndian(handle),
            result,
            timeout);
    }

    private static Task<int> StartWait(
        Fixture fixture,
        ulong threadHandle,
        bool timed = false,
        uint waitMode = 2) =>
        Task.Run(() =>
        {
            var context = CreateContext(fixture.Memory);
            context[CpuRegister.Rdi] = fixture.Handle;
            context[CpuRegister.Rsi] = 1;
            context[CpuRegister.Rdx] = waitMode;
            context[CpuRegister.Rcx] = ResultAddress;
            context[CpuRegister.R8] = timed ? TimeoutAddress : 0;
            var previous = GuestThreadExecution.EnterGuestThread(threadHandle);
            try
            {
                return KernelEventFlagCompatExports.KernelWaitEventFlag(context);
            }
            finally
            {
                GuestThreadExecution.RestoreGuestThread(previous);
            }
        });

    private static void SetFlag(Fixture fixture)
    {
        var context = CreateContext(fixture.Memory);
        context[CpuRegister.Rdi] = fixture.Handle;
        context[CpuRegister.Rsi] = 1;
        Assert.Equal(
            (int)OrbisGen2Result.ORBIS_GEN2_OK,
            KernelEventFlagCompatExports.KernelSetEventFlag(context));
    }

    private static void AssertWaitBlocked(ulong threadHandle) =>
        Assert.True(SpinWait.SpinUntil(
            () => GuestThreadBlocking.DescribeBlock(threadHandle) is not null,
            TimeSpan.FromSeconds(1)));

    private static CpuContext CreateContext(FakeGuestMemory memory) =>
        new(memory, Generation.Gen5);

    private static void DeleteFixture(Fixture fixture)
    {
        var context = CreateContext(fixture.Memory);
        context[CpuRegister.Rdi] = fixture.Handle;
        _ = KernelEventFlagCompatExports.KernelDeleteEventFlag(context);
        GuestThreadBlocking.BeginExecution();
    }

    private sealed record Fixture(
        FakeGuestMemory Memory,
        ulong Handle,
        byte[] Result,
        byte[]? Timeout);
}
