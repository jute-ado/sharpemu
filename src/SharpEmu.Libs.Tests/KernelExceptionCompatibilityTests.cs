// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using SharpEmu.Libs.Kernel;
using Xunit;

namespace SharpEmu.Libs.Tests;

[CollectionDefinition(
    SchedulerCollection.Name,
    DisableParallelization = true)]
public sealed class SchedulerCollection
{
    public const string Name = "Guest thread scheduler";
}

[Collection(SchedulerCollection.Name)]
public sealed class KernelExceptionCompatibilityTests
{
    [Fact]
    public void PthreadSelfRegistersPrimaryExecutorContext()
    {
        var scheduler = new RecordingScheduler();
        var previous = GuestThreadExecution.Scheduler;
        GuestThreadExecution.Scheduler = scheduler;
        try
        {
            var context =
                new CpuContext(new FakeGuestMemory(), Generation.Gen5);

            Assert.Equal(
                (int)OrbisGen2Result.ORBIS_GEN2_OK,
                KernelPthreadCompatExports.PthreadSelf(context));
            Assert.NotEqual(0UL, context[CpuRegister.Rax]);
            Assert.Equal(
                context[CpuRegister.Rax],
                scheduler.RegisteredThreadHandle);
            Assert.Same(context, scheduler.RegisteredContext);
        }
        finally
        {
            GuestThreadExecution.Scheduler = previous;
        }
    }

    [Fact]
    public void RaiseExceptionDelegatesToRegisteredTargetThread()
    {
        const int exceptionType = 30;
        const ulong handler = 0x1234_0000;
        const ulong target = 0x5678;
        var scheduler = new RecordingScheduler();
        var previous = GuestThreadExecution.Scheduler;
        GuestThreadExecution.Scheduler = scheduler;
        var context =
            new CpuContext(new FakeGuestMemory(), Generation.Gen5);
        try
        {
            context[CpuRegister.Rdi] = exceptionType;
            _ = KernelExceptionCompatExports.RemoveExceptionHandler(context);
            context[CpuRegister.Rdi] = exceptionType;
            context[CpuRegister.Rsi] = handler;
            Assert.Equal(
                (int)OrbisGen2Result.ORBIS_GEN2_OK,
                KernelExceptionCompatExports.InstallExceptionHandler(context));

            context[CpuRegister.Rdi] = target;
            context[CpuRegister.Rsi] = exceptionType;
            Assert.Equal(
                (int)OrbisGen2Result.ORBIS_GEN2_OK,
                KernelExceptionCompatExports.RaiseException(context));

            Assert.Equal(target, scheduler.RaisedThreadHandle);
            Assert.Equal(handler, scheduler.RaisedHandler);
            Assert.Equal(exceptionType, scheduler.RaisedExceptionType);
        }
        finally
        {
            context[CpuRegister.Rdi] = exceptionType;
            _ = KernelExceptionCompatExports.RemoveExceptionHandler(context);
            GuestThreadExecution.Scheduler = previous;
        }
    }

    private sealed class RecordingScheduler : IGuestThreadScheduler
    {
        public bool SupportsGuestContextTransfer => true;
        public ulong RegisteredThreadHandle { get; private set; }
        public CpuContext? RegisteredContext { get; private set; }
        public ulong RaisedThreadHandle { get; private set; }
        public ulong RaisedHandler { get; private set; }
        public int RaisedExceptionType { get; private set; }

        public void RegisterGuestThreadContext(
            ulong threadHandle,
            CpuContext context)
        {
            RegisteredThreadHandle = threadHandle;
            RegisteredContext = context;
        }

        public bool TryRaiseGuestException(
            CpuContext callerContext,
            ulong threadHandle,
            ulong handler,
            int exceptionType,
            out string? error)
        {
            RaisedThreadHandle = threadHandle;
            RaisedHandler = handler;
            RaisedExceptionType = exceptionType;
            error = null;
            return true;
        }

        public bool TryStartThread(
            CpuContext creatorContext,
            GuestThreadStartRequest request,
            out string? error)
        {
            error = "not used";
            return false;
        }

        public bool TryJoinThread(
            CpuContext callerContext,
            ulong threadHandle,
            out ulong returnValue,
            out string? error)
        {
            returnValue = 0;
            error = "not used";
            return false;
        }

        public void Pump(CpuContext callerContext, string reason)
        {
        }

        public IReadOnlyList<GuestThreadSnapshot> SnapshotThreads() => [];

        public bool TryCallGuestFunction(
            CpuContext callerContext,
            ulong entryPoint,
            ulong arg0,
            ulong arg1,
            ulong stackAddress,
            ulong stackSize,
            string reason,
            out string? error)
        {
            error = "not used";
            return false;
        }

        public bool TryCallGuestContinuation(
            CpuContext callerContext,
            GuestCpuContinuation continuation,
            string reason,
            out string? error)
        {
            error = "not used";
            return false;
        }
    }
}
