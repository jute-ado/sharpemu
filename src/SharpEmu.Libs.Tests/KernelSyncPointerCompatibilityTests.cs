// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using SharpEmu.HLE;
using SharpEmu.Libs.Kernel;
using Xunit;

namespace SharpEmu.Libs.Tests;

public sealed class KernelSyncPointerCompatibilityTests
{
    [Fact]
    public void EventFlagUsesTrackedLibcNameHandleAndResultBuffers()
    {
        var context = new CpuContext(new FakeGuestMemory(), Generation.Gen5);
        var nameAddress = AllocateTrackedBuffer(context, 32);
        var handleAddress = AllocateTrackedBuffer(context, sizeof(ulong));
        var resultAddress = AllocateTrackedBuffer(context, sizeof(ulong));
        ulong handle = 0;

        try
        {
            WriteCString(nameAddress, "host-event");
            context[CpuRegister.Rdi] = handleAddress;
            context[CpuRegister.Rsi] = nameAddress;
            context[CpuRegister.Rdx] = 0;
            context[CpuRegister.Rcx] = 0x20;
            context[CpuRegister.R8] = 0;

            Assert.Equal(
                (int)OrbisGen2Result.ORBIS_GEN2_OK,
                KernelEventFlagCompatExports.KernelCreateEventFlag(context));

            handle = unchecked((ulong)Marshal.ReadInt64((nint)handleAddress));
            Assert.NotEqual(0UL, handle);

            context[CpuRegister.Rdi] = handle;
            context[CpuRegister.Rsi] = 0x20;
            context[CpuRegister.Rdx] = 0x01;
            context[CpuRegister.Rcx] = resultAddress;

            Assert.Equal(
                (int)OrbisGen2Result.ORBIS_GEN2_OK,
                KernelEventFlagCompatExports.KernelPollEventFlag(context));
            Assert.Equal(0x20L, Marshal.ReadInt64((nint)resultAddress));
        }
        finally
        {
            if (handle != 0)
            {
                context[CpuRegister.Rdi] = handle;
                _ = KernelEventFlagCompatExports.KernelDeleteEventFlag(context);
            }

            FreeTrackedBuffer(context, resultAddress);
            FreeTrackedBuffer(context, handleAddress);
            FreeTrackedBuffer(context, nameAddress);
        }
    }

    [Fact]
    public void EventFlagTimeoutUpdatesTrackedLibcTimeoutAndResultBuffers()
    {
        var memory = new FakeGuestMemory();
        const ulong nameAddress = 0x1000;
        memory.AddRegion(nameAddress, Encoding.UTF8.GetBytes("timeout-event\0"));
        var context = new CpuContext(memory, Generation.Gen5);
        var handleAddress = AllocateTrackedBuffer(context, sizeof(ulong));
        var timeoutAddress = AllocateTrackedBuffer(context, sizeof(uint));
        var resultAddress = AllocateTrackedBuffer(context, sizeof(ulong));
        ulong handle = 0;

        try
        {
            context[CpuRegister.Rdi] = handleAddress;
            context[CpuRegister.Rsi] = nameAddress;
            context[CpuRegister.Rdx] = 0;
            context[CpuRegister.Rcx] = 0;
            context[CpuRegister.R8] = 0;
            Assert.Equal(
                (int)OrbisGen2Result.ORBIS_GEN2_OK,
                KernelEventFlagCompatExports.KernelCreateEventFlag(context));
            handle = unchecked((ulong)Marshal.ReadInt64((nint)handleAddress));

            Marshal.WriteInt32((nint)timeoutAddress, 5_000);
            Marshal.WriteInt64((nint)resultAddress, -1);
            context[CpuRegister.Rdi] = handle;
            context[CpuRegister.Rsi] = 1;
            context[CpuRegister.Rdx] = 0x01;
            context[CpuRegister.Rcx] = resultAddress;
            context[CpuRegister.R8] = timeoutAddress;

            Assert.Equal(
                (int)OrbisGen2Result.ORBIS_GEN2_ERROR_TIMED_OUT,
                KernelEventFlagCompatExports.KernelWaitEventFlag(context));
            Assert.Equal(0, Marshal.ReadInt32((nint)timeoutAddress));
            Assert.Equal(0L, Marshal.ReadInt64((nint)resultAddress));
        }
        finally
        {
            if (handle != 0)
            {
                context[CpuRegister.Rdi] = handle;
                _ = KernelEventFlagCompatExports.KernelDeleteEventFlag(context);
            }

            FreeTrackedBuffer(context, resultAddress);
            FreeTrackedBuffer(context, timeoutAddress);
            FreeTrackedBuffer(context, handleAddress);
        }
    }

    [Fact]
    public void SemaphoreUsesTrackedLibcNameHandleTimeoutAndWaiterBuffers()
    {
        var context = new CpuContext(new FakeGuestMemory(), Generation.Gen5);
        var nameAddress = AllocateTrackedBuffer(context, 32);
        var handleAddress = AllocateTrackedBuffer(context, sizeof(uint));
        var timeoutAddress = AllocateTrackedBuffer(context, sizeof(uint));
        var waiterCountAddress = AllocateTrackedBuffer(context, sizeof(uint));
        uint handle = 0;

        try
        {
            WriteCString(nameAddress, "host-semaphore");
            context[CpuRegister.Rdi] = handleAddress;
            context[CpuRegister.Rsi] = nameAddress;
            context[CpuRegister.Rdx] = 0;
            context[CpuRegister.Rcx] = 0;
            context[CpuRegister.R8] = 2;
            context[CpuRegister.R9] = 0;

            Assert.Equal(
                (int)OrbisGen2Result.ORBIS_GEN2_OK,
                KernelSemaphoreCompatExports.KernelCreateSema(context));
            handle = unchecked((uint)Marshal.ReadInt32((nint)handleAddress));
            Assert.NotEqual(0U, handle);

            Marshal.WriteInt32((nint)timeoutAddress, 5_000);
            context[CpuRegister.Rdi] = handle;
            context[CpuRegister.Rsi] = 1;
            context[CpuRegister.Rdx] = timeoutAddress;
            Assert.Equal(
                (int)OrbisGen2Result.ORBIS_GEN2_ERROR_TIMED_OUT,
                KernelSemaphoreCompatExports.KernelWaitSema(context));
            Assert.Equal(0, Marshal.ReadInt32((nint)timeoutAddress));

            Marshal.WriteInt32((nint)waiterCountAddress, -1);
            context[CpuRegister.Rdi] = handle;
            context[CpuRegister.Rsi] = 0;
            context[CpuRegister.Rdx] = waiterCountAddress;
            Assert.Equal(
                (int)OrbisGen2Result.ORBIS_GEN2_OK,
                KernelSemaphoreCompatExports.KernelCancelSema(context));
            Assert.Equal(0, Marshal.ReadInt32((nint)waiterCountAddress));
        }
        finally
        {
            if (handle != 0)
            {
                context[CpuRegister.Rdi] = handle;
                _ = KernelSemaphoreCompatExports.KernelDeleteSema(context);
            }

            FreeTrackedBuffer(context, waiterCountAddress);
            FreeTrackedBuffer(context, timeoutAddress);
            FreeTrackedBuffer(context, handleAddress);
            FreeTrackedBuffer(context, nameAddress);
        }
    }

    [Fact]
    public void TimedSemaphoreWaitWakesBeforeDeadlineAndUpdatesRemainingTimeout()
    {
        var context = new CpuContext(new FakeGuestMemory(), Generation.Gen5);
        var nameAddress = AllocateTrackedBuffer(context, 32);
        var handleAddress = AllocateTrackedBuffer(context, sizeof(uint));
        var timeoutAddress = AllocateTrackedBuffer(context, sizeof(uint));
        uint handle = 0;
        var previousGuestThread = GuestThreadExecution.EnterGuestThread(0x1234);

        try
        {
            WriteCString(nameAddress, "timed-wake-semaphore");
            context[CpuRegister.Rdi] = handleAddress;
            context[CpuRegister.Rsi] = nameAddress;
            context[CpuRegister.Rdx] = 0;
            context[CpuRegister.Rcx] = 0;
            context[CpuRegister.R8] = 2;
            context[CpuRegister.R9] = 0;
            Assert.Equal(
                (int)OrbisGen2Result.ORBIS_GEN2_OK,
                KernelSemaphoreCompatExports.KernelCreateSema(context));
            handle = unchecked((uint)Marshal.ReadInt32((nint)handleAddress));

            const int timeoutMicros = 5_000_000;
            Marshal.WriteInt32((nint)timeoutAddress, timeoutMicros);
            context[CpuRegister.Rdi] = handle;
            context[CpuRegister.Rsi] = 1;
            context[CpuRegister.Rdx] = timeoutAddress;
            Assert.Equal(
                (int)OrbisGen2Result.ORBIS_GEN2_OK,
                KernelSemaphoreCompatExports.KernelWaitSema(context));

            Assert.True(GuestThreadExecution.TryConsumeCurrentThreadBlock(
                out var reason,
                out _,
                out var hasContinuation,
                out var wakeKey,
                out var resumeHandler,
                out var wakeHandler,
                out var deadlineTimestamp));
            Assert.Equal("sceKernelWaitSema", reason);
            Assert.False(hasContinuation);
            Assert.Equal($"kernel_sema:0x{handle:X8}", wakeKey);
            Assert.NotNull(resumeHandler);
            Assert.NotNull(wakeHandler);
            Assert.True(deadlineTimestamp > Stopwatch.GetTimestamp());

            context[CpuRegister.Rdi] = handle;
            context[CpuRegister.Rsi] = 1;
            Assert.Equal(
                (int)OrbisGen2Result.ORBIS_GEN2_OK,
                KernelSemaphoreCompatExports.KernelSignalSema(context));
            Assert.True(wakeHandler!());
            Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_OK, resumeHandler!());

            var remainingTimeout = Marshal.ReadInt32((nint)timeoutAddress);
            Assert.InRange(remainingTimeout, 1, timeoutMicros);

            context[CpuRegister.Rdi] = handle;
            context[CpuRegister.Rsi] = 1;
            Assert.Equal(
                (int)OrbisGen2Result.ORBIS_GEN2_ERROR_BUSY,
                KernelSemaphoreCompatExports.KernelPollSema(context));
        }
        finally
        {
            GuestThreadExecution.RestoreGuestThread(previousGuestThread);
            if (handle != 0)
            {
                context[CpuRegister.Rdi] = handle;
                _ = KernelSemaphoreCompatExports.KernelDeleteSema(context);
            }

            FreeTrackedBuffer(context, timeoutAddress);
            FreeTrackedBuffer(context, handleAddress);
            FreeTrackedBuffer(context, nameAddress);
        }
    }

    [Fact]
    public void TimedSemaphoreWaitExpiresAndDoesNotConsumeLateSignal()
    {
        var context = new CpuContext(new FakeGuestMemory(), Generation.Gen5);
        var nameAddress = AllocateTrackedBuffer(context, 32);
        var handleAddress = AllocateTrackedBuffer(context, sizeof(uint));
        var timeoutAddress = AllocateTrackedBuffer(context, sizeof(uint));
        uint handle = 0;
        var previousGuestThread = GuestThreadExecution.EnterGuestThread(0x5678);

        try
        {
            WriteCString(nameAddress, "timed-expiry-semaphore");
            context[CpuRegister.Rdi] = handleAddress;
            context[CpuRegister.Rsi] = nameAddress;
            context[CpuRegister.Rdx] = 0;
            context[CpuRegister.Rcx] = 0;
            context[CpuRegister.R8] = 2;
            context[CpuRegister.R9] = 0;
            Assert.Equal(
                (int)OrbisGen2Result.ORBIS_GEN2_OK,
                KernelSemaphoreCompatExports.KernelCreateSema(context));
            handle = unchecked((uint)Marshal.ReadInt32((nint)handleAddress));

            Marshal.WriteInt32((nint)timeoutAddress, 1_000);
            context[CpuRegister.Rdi] = handle;
            context[CpuRegister.Rsi] = 1;
            context[CpuRegister.Rdx] = timeoutAddress;
            Assert.Equal(
                (int)OrbisGen2Result.ORBIS_GEN2_OK,
                KernelSemaphoreCompatExports.KernelWaitSema(context));

            Assert.True(GuestThreadExecution.TryConsumeCurrentThreadBlock(
                out _,
                out _,
                out _,
                out _,
                out var resumeHandler,
                out var wakeHandler,
                out var deadlineTimestamp));
            Assert.NotNull(resumeHandler);
            Assert.NotNull(wakeHandler);
            Assert.True(SpinWait.SpinUntil(
                () => Stopwatch.GetTimestamp() >= deadlineTimestamp,
                TimeSpan.FromSeconds(1)));

            Assert.Equal(
                (int)OrbisGen2Result.ORBIS_GEN2_ERROR_TIMED_OUT,
                resumeHandler!());
            Assert.Equal(0, Marshal.ReadInt32((nint)timeoutAddress));

            context[CpuRegister.Rdi] = handle;
            context[CpuRegister.Rsi] = 1;
            Assert.Equal(
                (int)OrbisGen2Result.ORBIS_GEN2_OK,
                KernelSemaphoreCompatExports.KernelSignalSema(context));
            Assert.True(wakeHandler!());

            context[CpuRegister.Rdi] = handle;
            context[CpuRegister.Rsi] = 1;
            Assert.Equal(
                (int)OrbisGen2Result.ORBIS_GEN2_OK,
                KernelSemaphoreCompatExports.KernelPollSema(context));
        }
        finally
        {
            GuestThreadExecution.RestoreGuestThread(previousGuestThread);
            if (handle != 0)
            {
                context[CpuRegister.Rdi] = handle;
                _ = KernelSemaphoreCompatExports.KernelDeleteSema(context);
            }

            FreeTrackedBuffer(context, timeoutAddress);
            FreeTrackedBuffer(context, handleAddress);
            FreeTrackedBuffer(context, nameAddress);
        }
    }

    [Fact]
    public void EventFlagStillRejectsUnmappedOutputBuffer()
    {
        var memory = new FakeGuestMemory();
        const ulong nameAddress = 0x1000;
        memory.AddRegion(nameAddress, Encoding.UTF8.GetBytes("bad-output\0"));
        var context = new CpuContext(memory, Generation.Gen5);
        context[CpuRegister.Rdi] = 0xDEAD_0000;
        context[CpuRegister.Rsi] = nameAddress;
        context[CpuRegister.Rdx] = 0;
        context[CpuRegister.Rcx] = 0;
        context[CpuRegister.R8] = 0;

        Assert.Equal(
            (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT,
            KernelEventFlagCompatExports.KernelCreateEventFlag(context));
    }

    private static ulong AllocateTrackedBuffer(CpuContext context, ulong size)
    {
        context[CpuRegister.Rdi] = size;
        Assert.Equal(
            (int)OrbisGen2Result.ORBIS_GEN2_OK,
            KernelMemoryCompatExports.Malloc(context));
        var address = context[CpuRegister.Rax];
        Assert.NotEqual(0UL, address);
        return address;
    }

    private static void FreeTrackedBuffer(CpuContext context, ulong address)
    {
        context[CpuRegister.Rdi] = address;
        _ = KernelMemoryCompatExports.Free(context);
    }

    private static void WriteCString(ulong address, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value + "\0");
        Marshal.Copy(bytes, 0, (nint)address, bytes.Length);
    }
}
