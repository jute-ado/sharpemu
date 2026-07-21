// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using SharpEmu.Libs.Kernel;
using Xunit;

namespace SharpEmu.Libs.Tests;

public sealed class PthreadDetachCompatibilityTests
{
    [Fact]
    public void PosixDetachMarksThreadAsDetachedAndPreventsJoin()
    {
        const ulong thread = 0xD37A_C001;
        var context = CreateContext(thread);

        var detachResult = KernelPthreadExtendedCompatExports.PosixPthreadDetach(context);

        Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_OK, detachResult);
        Assert.Equal(0UL, context[CpuRegister.Rax]);
        Assert.True(KernelPthreadExtendedCompatExports.IsThreadDetached(thread));

        context[CpuRegister.Rdi] = thread;
        context[CpuRegister.Rsi] = 0;
        var joinResult = KernelExports.PthreadJoin(context);

        Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT, joinResult);
        Assert.Equal(
            unchecked((ulong)(int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT),
            context[CpuRegister.Rax]);
    }

    [Fact]
    public void DetachingThreadTwiceReturnsInvalidArgument()
    {
        var context = CreateContext(0xD37A_C002);
        Assert.Equal(
            (int)OrbisGen2Result.ORBIS_GEN2_OK,
            KernelPthreadExtendedCompatExports.PthreadDetach(context));

        var result = KernelPthreadExtendedCompatExports.PosixPthreadDetach(context);

        Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT, result);
        Assert.Equal(
            unchecked((ulong)(int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT),
            context[CpuRegister.Rax]);
    }

    [Fact]
    public void DetachingNullThreadReturnsInvalidArgument()
    {
        var context = CreateContext(0);

        var result = KernelPthreadExtendedCompatExports.PosixPthreadDetach(context);

        Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT, result);
        Assert.Equal(
            unchecked((ulong)(int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT),
            context[CpuRegister.Rax]);
    }

    [Fact]
    public void DetachedExitRequestsDeferredReapingAndReleasesKernelHandle()
    {
        var thread = KernelPthreadState.CreateThreadHandle("detached-reap");
        var context = CreateContext(thread);
        var scheduler = new DeferredReapScheduler();
        var previousScheduler = GuestThreadExecution.Scheduler;
        GuestThreadExecution.Scheduler = scheduler;
        try
        {
            Assert.Equal(
                (int)OrbisGen2Result.ORBIS_GEN2_OK,
                KernelPthreadExtendedCompatExports.PosixPthreadDetach(context));

            GuestThreadExecution.NotifyGuestThreadExited(thread);

            Assert.Equal(thread, scheduler.RequestedThreadHandle);
            Assert.True(KernelPthreadState.TryGetThreadIdentity(thread, out _));

            GuestThreadExecution.NotifyGuestThreadReaped(thread);

            Assert.False(KernelPthreadState.TryGetThreadIdentity(thread, out _));
        }
        finally
        {
            GuestThreadExecution.Scheduler = previousScheduler;
            KernelPthreadExtendedCompatExports.ReleaseThreadState(thread);
            KernelPthreadState.ReleaseThreadHandle(thread);
        }
    }

    [Fact]
    public void DetachingAlreadyExitedThreadReapsItImmediately()
    {
        var thread = KernelPthreadState.CreateThreadHandle("late-detach-reap");
        var context = CreateContext(thread);
        var scheduler = new DeferredReapScheduler
        {
            CompleteSynchronously = true,
        };
        var previousScheduler = GuestThreadExecution.Scheduler;
        GuestThreadExecution.Scheduler = scheduler;
        try
        {
            Assert.Equal(
                (int)OrbisGen2Result.ORBIS_GEN2_OK,
                KernelPthreadExtendedCompatExports.PosixPthreadDetach(context));

            Assert.Equal(thread, scheduler.RequestedThreadHandle);
            Assert.False(KernelPthreadState.TryGetThreadIdentity(thread, out _));
        }
        finally
        {
            GuestThreadExecution.Scheduler = previousScheduler;
            KernelPthreadExtendedCompatExports.ReleaseThreadState(thread);
            KernelPthreadState.ReleaseThreadHandle(thread);
        }
    }

    [Theory]
    [InlineData("4qGrR6eoP9Y", "scePthreadDetach", "libKernel")]
    [InlineData("+U1R4WtXvoc", "pthread_detach", "libScePosix")]
    public void DetachExportMetadataIsExact(
        string nid,
        string exportName,
        string libraryName)
    {
        ExportMetadataAssert.Exact(
            nid,
            exportName,
            libraryName,
            Generation.Gen4 | Generation.Gen5);
    }

    private static CpuContext CreateContext(ulong thread)
    {
        var context = new CpuContext(new FakeGuestMemory(), Generation.Gen5);
        context[CpuRegister.Rdi] = thread;
        return context;
    }

    private sealed class DeferredReapScheduler : IGuestThreadScheduler
    {
        public bool SupportsGuestContextTransfer => true;

        public ulong RequestedThreadHandle { get; private set; }

        public bool CompleteSynchronously { get; init; }

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
            returnValue = 0;
            error = "not used";
            return false;
        }

        public bool RequestThreadReap(ulong threadHandle)
        {
            RequestedThreadHandle = threadHandle;
            if (CompleteSynchronously)
            {
                GuestThreadExecution.NotifyGuestThreadReaped(threadHandle);
            }
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
