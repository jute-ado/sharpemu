// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using SharpEmu.Libs.Kernel;
using Xunit;

namespace SharpEmu.Libs.Tests;

public sealed class PthreadSpecificLifecycleTests
{
    [Fact]
    public void GuestThreadExitLimitsRestoredDestructorValuesToFourPasses()
    {
        const ulong keyAddress = 0x3000;
        const ulong destructor = 0x1234_0000;
        const ulong firstValue = 0x1111;
        var memory = new FakeGuestMemory();
        memory.AddRegion(keyAddress, new byte[sizeof(int)]);
        var context = new CpuContext(memory, Generation.Gen5);

        context[CpuRegister.Rdi] = keyAddress;
        context[CpuRegister.Rsi] = destructor;
        Assert.Equal(
            (int)OrbisGen2Result.ORBIS_GEN2_OK,
            KernelPthreadExtendedCompatExports.PosixPthreadKeyCreate(context));
        Assert.True(context.TryReadInt32(keyAddress, out var key));

        var threadHandle = KernelPthreadState.CreateThreadHandle("tls-destructor");
        var previousGuestThread = GuestThreadExecution.EnterGuestThread(threadHandle);
        var previousScheduler = GuestThreadExecution.Scheduler;
        var scheduler = new RecordingScheduler();
        GuestThreadExecution.Scheduler = scheduler;
        try
        {
            context[CpuRegister.Rdi] = unchecked((ulong)key);
            context[CpuRegister.Rsi] = firstValue;
            Assert.Equal(
                (int)OrbisGen2Result.ORBIS_GEN2_OK,
                KernelPthreadExtendedCompatExports.PosixPthreadSetspecific(context));
            scheduler.Callback = call =>
            {
                context[CpuRegister.Rdi] = unchecked((ulong)key);
                context[CpuRegister.Rsi] = call.Argument + 0x1111;
                Assert.Equal(
                    (int)OrbisGen2Result.ORBIS_GEN2_OK,
                    KernelPthreadExtendedCompatExports.PosixPthreadSetspecific(context));
            };

            GuestThreadExecution.NotifyGuestThreadExited(threadHandle, context);

            Assert.Equal(
                [
                    (destructor, 0x1111UL),
                    (destructor, 0x2222UL),
                    (destructor, 0x3333UL),
                    (destructor, 0x4444UL),
                ],
                scheduler.Calls.Select(static call => (call.EntryPoint, call.Argument)));
            context[CpuRegister.Rdi] = unchecked((ulong)key);
            Assert.Equal(
                (int)OrbisGen2Result.ORBIS_GEN2_OK,
                KernelPthreadExtendedCompatExports.PosixPthreadGetspecific(context));
            Assert.Equal(0UL, context[CpuRegister.Rax]);
        }
        finally
        {
            GuestThreadExecution.Scheduler = previousScheduler;
            GuestThreadExecution.RestoreGuestThread(previousGuestThread);
            context[CpuRegister.Rdi] = unchecked((ulong)key);
            _ = KernelPthreadExtendedCompatExports.PosixPthreadKeyDelete(context);
        }
    }

    [Fact]
    public void GuestThreadExitDiscardsThreadSpecificValues()
    {
        const ulong keyAddress = 0x1000;
        const ulong value = 0x1234_5678_9ABC_DEF0;
        var memory = new FakeGuestMemory();
        memory.AddRegion(keyAddress, new byte[sizeof(int)]);
        var context = new CpuContext(memory, Generation.Gen5);

        context[CpuRegister.Rdi] = keyAddress;
        context[CpuRegister.Rsi] = 0;
        Assert.Equal(
            (int)OrbisGen2Result.ORBIS_GEN2_OK,
            KernelPthreadExtendedCompatExports.PosixPthreadKeyCreate(context));
        Assert.True(context.TryReadInt32(keyAddress, out var key));

        var threadHandle = KernelPthreadState.CreateThreadHandle("tls-exit-test");
        var previousGuestThread = GuestThreadExecution.EnterGuestThread(threadHandle);
        try
        {
            context[CpuRegister.Rdi] = unchecked((ulong)key);
            context[CpuRegister.Rsi] = value;
            Assert.Equal(
                (int)OrbisGen2Result.ORBIS_GEN2_OK,
                KernelPthreadExtendedCompatExports.PosixPthreadSetspecific(context));

            GuestThreadExecution.NotifyGuestThreadExited(threadHandle);

            context[CpuRegister.Rdi] = unchecked((ulong)key);
            Assert.Equal(
                (int)OrbisGen2Result.ORBIS_GEN2_OK,
                KernelPthreadExtendedCompatExports.PosixPthreadGetspecific(context));
            Assert.Equal(0UL, context[CpuRegister.Rax]);
        }
        finally
        {
            GuestThreadExecution.RestoreGuestThread(previousGuestThread);
            context[CpuRegister.Rdi] = unchecked((ulong)key);
            _ = KernelPthreadExtendedCompatExports.PosixPthreadKeyDelete(context);
        }
    }

    [Fact]
    public void GuestThreadExitPreservesOtherThreadsSpecificValues()
    {
        const ulong keyAddress = 0x2000;
        const ulong firstValue = 0x1111;
        const ulong secondValue = 0x2222;
        var memory = new FakeGuestMemory();
        memory.AddRegion(keyAddress, new byte[sizeof(int)]);
        var context = new CpuContext(memory, Generation.Gen5);

        context[CpuRegister.Rdi] = keyAddress;
        Assert.Equal(
            (int)OrbisGen2Result.ORBIS_GEN2_OK,
            KernelPthreadExtendedCompatExports.PosixPthreadKeyCreate(context));
        Assert.True(context.TryReadInt32(keyAddress, out var key));

        var firstThread = KernelPthreadState.CreateThreadHandle("tls-first");
        var secondThread = KernelPthreadState.CreateThreadHandle("tls-second");
        var previousGuestThread = GuestThreadExecution.EnterGuestThread(firstThread);
        try
        {
            context[CpuRegister.Rdi] = unchecked((ulong)key);
            context[CpuRegister.Rsi] = firstValue;
            Assert.Equal(
                (int)OrbisGen2Result.ORBIS_GEN2_OK,
                KernelPthreadExtendedCompatExports.PosixPthreadSetspecific(context));

            _ = GuestThreadExecution.EnterGuestThread(secondThread);
            context[CpuRegister.Rsi] = secondValue;
            Assert.Equal(
                (int)OrbisGen2Result.ORBIS_GEN2_OK,
                KernelPthreadExtendedCompatExports.PosixPthreadSetspecific(context));

            GuestThreadExecution.NotifyGuestThreadExited(firstThread);

            context[CpuRegister.Rdi] = unchecked((ulong)key);
            Assert.Equal(
                (int)OrbisGen2Result.ORBIS_GEN2_OK,
                KernelPthreadExtendedCompatExports.PosixPthreadGetspecific(context));
            Assert.Equal(secondValue, context[CpuRegister.Rax]);
        }
        finally
        {
            GuestThreadExecution.NotifyGuestThreadExited(secondThread);
            GuestThreadExecution.RestoreGuestThread(previousGuestThread);
            context[CpuRegister.Rdi] = unchecked((ulong)key);
            _ = KernelPthreadExtendedCompatExports.PosixPthreadKeyDelete(context);
        }
    }

    private sealed class RecordingScheduler : IGuestThreadScheduler
    {
        public bool SupportsGuestContextTransfer => true;

        public List<(ulong EntryPoint, ulong Argument)> Calls { get; } = [];

        public Action<(ulong EntryPoint, ulong Argument)>? Callback { get; set; }

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
            var call = (entryPoint, arg0);
            Calls.Add(call);
            Callback?.Invoke(call);
            returnValue = 0;
            error = null;
            return true;
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
