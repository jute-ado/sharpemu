// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using SharpEmu.Libs.Kernel;
using Xunit;

namespace SharpEmu.Libs.Tests;

public sealed class PthreadJoinLifecycleTests
{
    [Fact]
    public void SuccessfulJoinReapsSchedulerAndKernelThreadState()
    {
        var threadHandle = KernelPthreadState.CreateThreadHandle("join-reap");
        var scheduler = new ReapingScheduler();
        var previousScheduler = GuestThreadExecution.Scheduler;
        GuestThreadExecution.Scheduler = scheduler;
        try
        {
            var context = new CpuContext(new FakeGuestMemory(), Generation.Gen5);
            context[CpuRegister.Rdi] = threadHandle;
            context[CpuRegister.Rsi] = 0;

            Assert.Equal(
                (int)OrbisGen2Result.ORBIS_GEN2_OK,
                KernelExports.PthreadJoin(context));

            Assert.Equal(threadHandle, scheduler.JoinedThreadHandle);
            Assert.Equal(threadHandle, scheduler.ReapedThreadHandle);
            Assert.False(KernelPthreadState.TryGetThreadIdentity(threadHandle, out _));
        }
        finally
        {
            GuestThreadExecution.Scheduler = previousScheduler;
        }
    }

    [Fact]
    public void FailedReturnValueWriteKeepsJoinedThreadAvailableForRetry()
    {
        var threadHandle = KernelPthreadState.CreateThreadHandle("join-retry");
        var scheduler = new ReapingScheduler();
        var previousScheduler = GuestThreadExecution.Scheduler;
        GuestThreadExecution.Scheduler = scheduler;
        try
        {
            var context = new CpuContext(new FakeGuestMemory(), Generation.Gen5);
            context[CpuRegister.Rdi] = threadHandle;
            context[CpuRegister.Rsi] = 0xDEAD_0000;

            Assert.Equal(
                (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT,
                KernelExports.PthreadJoin(context));

            Assert.Equal(0UL, scheduler.ReapedThreadHandle);
            Assert.True(KernelPthreadState.TryGetThreadIdentity(threadHandle, out _));
        }
        finally
        {
            GuestThreadExecution.Scheduler = previousScheduler;
            KernelPthreadExtendedCompatExports.ReleaseThreadState(threadHandle);
            KernelPthreadState.ReleaseThreadHandle(threadHandle);
        }
    }

    private sealed class ReapingScheduler : IGuestThreadScheduler
    {
        public bool SupportsGuestContextTransfer => true;

        public ulong JoinedThreadHandle { get; private set; }

        public ulong ReapedThreadHandle { get; private set; }

        public void RegisterGuestThreadContext(ulong threadHandle, CpuContext context)
        {
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
            JoinedThreadHandle = threadHandle;
            returnValue = 0x1234;
            error = null;
            return true;
        }

        public bool TryReapThread(ulong threadHandle)
        {
            ReapedThreadHandle = threadHandle;
            return true;
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
            ulong arg2,
            ulong stackAddress,
            ulong stackSize,
            string reason,
            out ulong returnValue,
            out string? error)
        {
            returnValue = 0;
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
