// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Text;
using SharpEmu.HLE;
using SharpEmu.Libs.Kernel;
using Xunit;

namespace SharpEmu.Libs.Tests;

[CollectionDefinition(
    KernelEventObjectSessionStateCollection.Name,
    DisableParallelization = true)]
public sealed class KernelEventObjectSessionStateCollection
{
    public const string Name = "Kernel event-object session state";
}

[Collection(KernelEventObjectSessionStateCollection.Name)]
public sealed class KernelEventObjectSessionResetTests
{
    private const ulong EqueueAddress = 0x1000;
    private const ulong EventFlagAddress = 0x2000;
    private const ulong SemaphoreAddress = 0x3000;
    private const ulong NameAddress = 0x4000;

    [Fact]
    public void ResetRuntimeStateInvalidatesObjectsHandlersAndRestartsHandles()
    {
        var memory = new FakeGuestMemory();
        memory.AddRegion(EqueueAddress, new byte[sizeof(ulong)]);
        memory.AddRegion(EventFlagAddress, new byte[sizeof(ulong)]);
        memory.AddRegion(SemaphoreAddress, new byte[sizeof(uint)]);
        memory.AddRegion(NameAddress, Encoding.UTF8.GetBytes("session-object\0"));
        var context = new CpuContext(memory, Generation.Gen5);

        KernelEventObjectLifecycle.ResetRuntimeState();
        try
        {
            var firstEqueue = CreateEqueue(context);
            var firstEventFlag = CreateEventFlag(context);
            var firstSemaphore = CreateSemaphore(context);

            context[CpuRegister.Rdi] = 30;
            context[CpuRegister.Rsi] = 0x1234_0000;
            Assert.Equal(
                (int)OrbisGen2Result.ORBIS_GEN2_OK,
                KernelExceptionCompatExports.InstallExceptionHandler(context));

            KernelEventObjectLifecycle.ResetRuntimeState();

            context[CpuRegister.Rdi] = firstEqueue;
            Assert.Equal(
                (int)OrbisGen2Result.ORBIS_GEN2_ERROR_NOT_FOUND,
                KernelEventQueueCompatExports.KernelDeleteEqueue(context));
            context[CpuRegister.Rdi] = firstEventFlag;
            Assert.Equal(
                (int)OrbisGen2Result.ORBIS_GEN2_ERROR_NOT_FOUND,
                KernelEventFlagCompatExports.KernelDeleteEventFlag(context));
            context[CpuRegister.Rdi] = firstSemaphore;
            Assert.Equal(
                (int)OrbisGen2Result.ORBIS_GEN2_ERROR_NOT_FOUND,
                KernelSemaphoreCompatExports.KernelDeleteSema(context));

            context[CpuRegister.Rdi] = 30;
            context[CpuRegister.Rsi] = 0x5678_0000;
            Assert.Equal(
                (int)OrbisGen2Result.ORBIS_GEN2_OK,
                KernelExceptionCompatExports.InstallExceptionHandler(context));

            Assert.Equal(firstEqueue, CreateEqueue(context));
            Assert.Equal(firstEventFlag, CreateEventFlag(context));
            Assert.Equal(firstSemaphore, CreateSemaphore(context));
        }
        finally
        {
            KernelEventObjectLifecycle.ResetRuntimeState();
        }
    }

    private static ulong CreateEqueue(CpuContext context)
    {
        context[CpuRegister.Rdi] = EqueueAddress;
        Assert.Equal(
            (int)OrbisGen2Result.ORBIS_GEN2_OK,
            KernelEventQueueCompatExports.KernelCreateEqueue(context));
        Assert.True(context.TryReadUInt64(EqueueAddress, out var handle));
        return handle;
    }

    private static ulong CreateEventFlag(CpuContext context)
    {
        context[CpuRegister.Rdi] = EventFlagAddress;
        context[CpuRegister.Rsi] = NameAddress;
        context[CpuRegister.Rdx] = 0x20;
        context[CpuRegister.Rcx] = 0;
        context[CpuRegister.R8] = 0;
        Assert.Equal(
            (int)OrbisGen2Result.ORBIS_GEN2_OK,
            KernelEventFlagCompatExports.KernelCreateEventFlag(context));
        Assert.True(context.TryReadUInt64(EventFlagAddress, out var handle));
        return handle;
    }

    private static uint CreateSemaphore(CpuContext context)
    {
        context[CpuRegister.Rdi] = SemaphoreAddress;
        context[CpuRegister.Rsi] = NameAddress;
        context[CpuRegister.Rdx] = 0;
        context[CpuRegister.Rcx] = 0;
        context[CpuRegister.R8] = 1;
        context[CpuRegister.R9] = 0;
        Assert.Equal(
            (int)OrbisGen2Result.ORBIS_GEN2_OK,
            KernelSemaphoreCompatExports.KernelCreateSema(context));
        Assert.True(context.TryReadUInt32(SemaphoreAddress, out var handle));
        return handle;
    }
}
