// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using SharpEmu.Libs.Kernel;
using Xunit;

namespace SharpEmu.Libs.Tests;

[CollectionDefinition(PthreadOnceLifecycleCollection.Name, DisableParallelization = true)]
public sealed class PthreadOnceLifecycleCollection
{
    public const string Name = "Pthread once lifecycle";
}

[Collection(PthreadOnceLifecycleCollection.Name)]
public sealed class PthreadOnceLifecycleTests : IDisposable
{
    private const ulong OnceAddress = 0x1000;
    private const ulong CallbackAddress = 0x2000;

    private readonly IGuestThreadScheduler? _previousScheduler = GuestThreadExecution.Scheduler;

    public PthreadOnceLifecycleTests()
    {
        GuestThreadExecution.Scheduler = null;
        KernelPthreadLifecycle.ResetRuntimeState();
    }

    public void Dispose()
    {
        GuestThreadExecution.Scheduler = _previousScheduler;
        KernelPthreadLifecycle.ResetRuntimeState();
    }

    [Fact]
    public void SuccessfulOnce_CallsInitializerOnceAndReleasesHostGate()
    {
        var (context, _) = CreateContext();
        var scheduler = new OnceScheduler();
        GuestThreadExecution.Scheduler = scheduler;

        Assert.Equal(
            (int)OrbisGen2Result.ORBIS_GEN2_OK,
            CallOnce(context));
        Assert.Equal(1, scheduler.CallCount);
        Assert.Equal(CallbackAddress, scheduler.EntryPoint);
        Assert.Equal("pthread_once", scheduler.Reason);
        Assert.Equal(0UL, scheduler.Arg0);
        Assert.True(context.TryReadInt32(OnceAddress, out var onceValue));
        Assert.Equal(2, onceValue);
        Assert.Equal(0, KernelPthreadCompatExports.PthreadOnceGateCount);

        Assert.Equal(
            (int)OrbisGen2Result.ORBIS_GEN2_OK,
            CallOnce(context));
        Assert.Equal(1, scheduler.CallCount);
        Assert.Equal(0, KernelPthreadCompatExports.PthreadOnceGateCount);
    }

    [Fact]
    public void FailedInitializer_ResetsFlagAndCanRetry()
    {
        var (context, _) = CreateContext();
        var scheduler = new OnceScheduler { Succeeds = false };
        GuestThreadExecution.Scheduler = scheduler;

        Assert.Equal(
            (int)OrbisGen2Result.ORBIS_GEN2_ERROR_TRY_AGAIN,
            CallOnce(context));
        Assert.True(context.TryReadInt32(OnceAddress, out var failedValue));
        Assert.Equal(0, failedValue);
        Assert.Equal(1, scheduler.CallCount);

        scheduler.Succeeds = true;
        Assert.Equal(
            (int)OrbisGen2Result.ORBIS_GEN2_OK,
            CallOnce(context));
        Assert.True(context.TryReadInt32(OnceAddress, out var completedValue));
        Assert.Equal(2, completedValue);
        Assert.Equal(2, scheduler.CallCount);
        Assert.Equal(0, KernelPthreadCompatExports.PthreadOnceGateCount);
    }

    [Fact]
    public void InvalidOrUnreadableArguments_DoNotInvokeScheduler()
    {
        var context = new CpuContext(new FakeGuestMemory(), Generation.Gen5);
        var scheduler = new OnceScheduler();
        GuestThreadExecution.Scheduler = scheduler;

        context[CpuRegister.Rdi] = 0;
        context[CpuRegister.Rsi] = CallbackAddress;
        Assert.Equal(
            (int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT,
            KernelPthreadCompatExports.PthreadOnce(context));

        context[CpuRegister.Rdi] = OnceAddress;
        context[CpuRegister.Rsi] = CallbackAddress;
        Assert.Equal(
            (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT,
            KernelPthreadCompatExports.PthreadOnce(context));
        Assert.Equal(0, scheduler.CallCount);
        Assert.Equal(0, KernelPthreadCompatExports.PthreadOnceGateCount);
    }

    private static (CpuContext Context, FakeGuestMemory Memory) CreateContext()
    {
        var memory = new FakeGuestMemory();
        memory.AddRegion(OnceAddress, new byte[sizeof(int)]);
        return (new CpuContext(memory, Generation.Gen5), memory);
    }

    private static int CallOnce(CpuContext context)
    {
        context[CpuRegister.Rdi] = OnceAddress;
        context[CpuRegister.Rsi] = CallbackAddress;
        return KernelPthreadCompatExports.PthreadOnce(context);
    }

    private sealed class OnceScheduler : IGuestThreadScheduler
    {
        public bool SupportsGuestContextTransfer => true;

        public bool Succeeds { get; set; } = true;

        public int CallCount { get; private set; }

        public ulong EntryPoint { get; private set; }

        public ulong Arg0 { get; private set; }

        public string? Reason { get; private set; }

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
            CallCount++;
            EntryPoint = entryPoint;
            Arg0 = arg0;
            Reason = reason;
            returnValue = 0;
            error = Succeeds ? null : "intentional failure";
            return Succeeds;
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
