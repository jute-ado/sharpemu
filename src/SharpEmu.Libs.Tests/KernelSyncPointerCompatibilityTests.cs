// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
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
    public async Task TimedSemaphoreWaitWakesBeforeDeadlineAndUpdatesRemainingTimeout()
    {
        GuestThreadBlocking.BeginExecution();
        var context = new CpuContext(new FakeGuestMemory(), Generation.Gen5);
        var nameAddress = AllocateTrackedBuffer(context, 32);
        var handleAddress = AllocateTrackedBuffer(context, sizeof(uint));
        var timeoutAddress = AllocateTrackedBuffer(context, sizeof(uint));
        uint handle = 0;

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
            var wait = StartSemaphoreWait(
                context.Memory,
                handle,
                timeoutAddress,
                0x1234);
            AssertWaitBlocked(0x1234);

            context[CpuRegister.Rdi] = handle;
            context[CpuRegister.Rsi] = 1;
            Assert.Equal(
                (int)OrbisGen2Result.ORBIS_GEN2_OK,
                KernelSemaphoreCompatExports.KernelSignalSema(context));
            Assert.Equal(
                (int)OrbisGen2Result.ORBIS_GEN2_OK,
                await wait.WaitAsync(TimeSpan.FromSeconds(1)));

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
    public async Task TimedSemaphoreWaitExpiresAndDoesNotConsumeLateSignal()
    {
        GuestThreadBlocking.BeginExecution();
        var context = new CpuContext(new FakeGuestMemory(), Generation.Gen5);
        var nameAddress = AllocateTrackedBuffer(context, 32);
        var handleAddress = AllocateTrackedBuffer(context, sizeof(uint));
        var timeoutAddress = AllocateTrackedBuffer(context, sizeof(uint));
        uint handle = 0;

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
            Assert.Equal(
                (int)OrbisGen2Result.ORBIS_GEN2_ERROR_TIMED_OUT,
                await StartSemaphoreWait(
                        context.Memory,
                        handle,
                        timeoutAddress,
                        0x5678)
                    .WaitAsync(TimeSpan.FromSeconds(1)));
            Assert.Equal(0, Marshal.ReadInt32((nint)timeoutAddress));

            context[CpuRegister.Rdi] = handle;
            context[CpuRegister.Rsi] = 1;
            Assert.Equal(
                (int)OrbisGen2Result.ORBIS_GEN2_OK,
                KernelSemaphoreCompatExports.KernelSignalSema(context));

            context[CpuRegister.Rdi] = handle;
            context[CpuRegister.Rsi] = 1;
            Assert.Equal(
                (int)OrbisGen2Result.ORBIS_GEN2_OK,
                KernelSemaphoreCompatExports.KernelPollSema(context));
        }
        finally
        {
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
    public async Task TimedSemaphoreTimeoutCopyoutFailurePreservesSignalCount()
    {
        GuestThreadBlocking.BeginExecution();
        const ulong nameAddress = 0x1000;
        const ulong handleAddress = 0x2000;
        const ulong timeoutAddress = 0x3000;
        var memory = new FakeGuestMemory();
        var handleBytes = new byte[sizeof(uint)];
        var timeoutBytes = new byte[sizeof(uint)];
        memory.AddRegion(nameAddress, Encoding.UTF8.GetBytes("timed-copyout-semaphore\0"));
        memory.AddRegion(handleAddress, handleBytes);
        memory.AddRegion(timeoutAddress, timeoutBytes);
        var context = new CpuContext(memory, Generation.Gen5);
        uint handle = 0;

        try
        {
            context[CpuRegister.Rdi] = handleAddress;
            context[CpuRegister.Rsi] = nameAddress;
            context[CpuRegister.Rdx] = 0;
            context[CpuRegister.Rcx] = 0;
            context[CpuRegister.R8] = 1;
            context[CpuRegister.R9] = 0;
            Assert.Equal(
                (int)OrbisGen2Result.ORBIS_GEN2_OK,
                KernelSemaphoreCompatExports.KernelCreateSema(context));
            handle = BinaryPrimitives.ReadUInt32LittleEndian(handleBytes);

            BinaryPrimitives.WriteUInt32LittleEndian(timeoutBytes, 100_000);
            var wait = StartSemaphoreWait(
                memory,
                handle,
                timeoutAddress,
                0xBEEF);
            AssertWaitBlocked(0xBEEF);

            Assert.True(memory.RemoveRegion(timeoutAddress));
            context[CpuRegister.Rdi] = handle;
            context[CpuRegister.Rsi] = 1;
            Assert.Equal(
                (int)OrbisGen2Result.ORBIS_GEN2_OK,
                KernelSemaphoreCompatExports.KernelSignalSema(context));
            Assert.Equal(
                (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT,
                await wait.WaitAsync(TimeSpan.FromSeconds(1)));

            context[CpuRegister.Rdi] = handle;
            context[CpuRegister.Rsi] = 1;
            Assert.Equal(
                (int)OrbisGen2Result.ORBIS_GEN2_OK,
                KernelSemaphoreCompatExports.KernelPollSema(context));
        }
        finally
        {
            if (handle != 0)
            {
                context[CpuRegister.Rdi] = handle;
                _ = KernelSemaphoreCompatExports.KernelDeleteSema(context);
            }
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

    private static Task<int> StartSemaphoreWait(
        ICpuMemory memory,
        uint handle,
        ulong timeoutAddress,
        ulong threadHandle) =>
        Task.Run(() =>
        {
            var context = new CpuContext(memory, Generation.Gen5);
            context[CpuRegister.Rdi] = handle;
            context[CpuRegister.Rsi] = 1;
            context[CpuRegister.Rdx] = timeoutAddress;
            var previousGuestThread =
                GuestThreadExecution.EnterGuestThread(threadHandle);
            try
            {
                return KernelSemaphoreCompatExports.KernelWaitSema(context);
            }
            finally
            {
                GuestThreadExecution.RestoreGuestThread(previousGuestThread);
            }
        });

    private static void AssertWaitBlocked(ulong threadHandle) =>
        Assert.True(SpinWait.SpinUntil(
            () => GuestThreadBlocking.DescribeBlock(threadHandle) is not null,
            TimeSpan.FromSeconds(1)));

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
