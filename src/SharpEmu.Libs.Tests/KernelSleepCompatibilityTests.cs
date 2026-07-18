// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using System.Diagnostics;
using SharpEmu.HLE;
using SharpEmu.Libs.Kernel;
using Xunit;

namespace SharpEmu.Libs.Tests;

public sealed class KernelSleepCompatibilityTests
{
    private const ulong RequestAddress = 0x1000;
    private const ulong RemainAddress = 0x2000;

    [Fact]
    public void UsleepParksGuestThreadUntilDeadline()
    {
        var context = new CpuContext(new FakeGuestMemory(), Generation.Gen5);
        context[CpuRegister.Rdi] = 100_000;
        var previousGuestThread = GuestThreadExecution.EnterGuestThread(0x1234);
        try
        {
            var startedAt = Stopwatch.GetTimestamp();

            Assert.Equal(
                (int)OrbisGen2Result.ORBIS_GEN2_OK,
                KernelRuntimeCompatExports.KernelUsleep(context));
            Assert.True(GuestThreadExecution.TryConsumeCurrentThreadBlock(
                out var reason,
                out _,
                out _,
                out var wakeKey,
                out IGuestThreadBlockWaiter? waiter,
                out var deadlineTimestamp));
            Assert.Equal("sceKernelUsleep", reason);
            Assert.Equal("sceKernelUsleep:0000000000001234", wakeKey);
            Assert.NotNull(waiter);
            Assert.False(waiter!.TryWake());
            Assert.True(deadlineTimestamp > startedAt);
            Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_OK, waiter!.Resume());
        }
        finally
        {
            GuestThreadExecution.RestoreGuestThread(previousGuestThread);
        }
    }

    [Fact]
    public void NanosleepParksGuestThreadAndClearsRemainderOnResume()
    {
        var memory = new FakeGuestMemory();
        var request = new byte[16];
        var remain = new byte[16];
        BinaryPrimitives.WriteInt64LittleEndian(request.AsSpan(sizeof(long)), 100_000_000);
        remain.AsSpan().Fill(0xA5);
        memory.AddRegion(RequestAddress, request);
        memory.AddRegion(RemainAddress, remain);
        var context = new CpuContext(memory, Generation.Gen5);
        context[CpuRegister.Rdi] = RequestAddress;
        context[CpuRegister.Rsi] = RemainAddress;
        var previousGuestThread = GuestThreadExecution.EnterGuestThread(0x5678);
        try
        {
            var startedAt = Stopwatch.GetTimestamp();

            Assert.Equal(
                (int)OrbisGen2Result.ORBIS_GEN2_OK,
                KernelRuntimeCompatExports.KernelNanosleep(context));
            Assert.True(GuestThreadExecution.TryConsumeCurrentThreadBlock(
                out var reason,
                out _,
                out _,
                out var wakeKey,
                out IGuestThreadBlockWaiter? waiter,
                out var deadlineTimestamp));
            Assert.Equal("sceKernelNanosleep", reason);
            Assert.Equal("sceKernelNanosleep:0000000000005678", wakeKey);
            Assert.NotNull(waiter);
            Assert.False(waiter!.TryWake());
            Assert.True(deadlineTimestamp > startedAt);
            Assert.All(remain, value => Assert.Equal(0xA5, value));

            Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_OK, waiter!.Resume());
            Assert.All(remain, value => Assert.Equal(0, value));
        }
        finally
        {
            GuestThreadExecution.RestoreGuestThread(previousGuestThread);
        }
    }

    [Fact]
    public void VeryLongGuestSleepsSaturateSchedulerDeadlines()
    {
        var memory = new FakeGuestMemory();
        var request = new byte[16];
        BinaryPrimitives.WriteInt64LittleEndian(request, long.MaxValue);
        BinaryPrimitives.WriteInt64LittleEndian(request.AsSpan(sizeof(long)), 999_999_999);
        memory.AddRegion(RequestAddress, request);
        var context = new CpuContext(memory, Generation.Gen5);
        var previousGuestThread = GuestThreadExecution.EnterGuestThread(0x9ABC);
        try
        {
            context[CpuRegister.Rdi] = ulong.MaxValue;
            Assert.Equal(
                (int)OrbisGen2Result.ORBIS_GEN2_OK,
                KernelRuntimeCompatExports.KernelUsleep(context));
            Assert.True(GuestThreadExecution.TryConsumeCurrentThreadBlock(
                out _,
                out _,
                out _,
                out _,
                out IGuestThreadBlockWaiter? usleepWaiter,
                out var usleepDeadline));
            Assert.Equal(long.MaxValue, usleepDeadline);
            Assert.NotNull(usleepWaiter);
            Assert.Equal(
                (int)OrbisGen2Result.ORBIS_GEN2_OK,
                usleepWaiter.Resume());

            context[CpuRegister.Rdi] = RequestAddress;
            context[CpuRegister.Rsi] = 0;
            Assert.Equal(
                (int)OrbisGen2Result.ORBIS_GEN2_OK,
                KernelRuntimeCompatExports.KernelNanosleep(context));
            Assert.True(GuestThreadExecution.TryConsumeCurrentThreadBlock(
                out _,
                out _,
                out _,
                out _,
                out IGuestThreadBlockWaiter? nanosleepWaiter,
                out var nanosleepDeadline));
            Assert.Equal(long.MaxValue, nanosleepDeadline);
            Assert.NotNull(nanosleepWaiter);
            Assert.Equal(
                (int)OrbisGen2Result.ORBIS_GEN2_OK,
                nanosleepWaiter.Resume());
        }
        finally
        {
            GuestThreadExecution.RestoreGuestThread(previousGuestThread);
        }
    }
}
