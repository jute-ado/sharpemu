// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Text;
using SharpEmu.HLE;
using SharpEmu.Libs.Kernel;
using Xunit;

namespace SharpEmu.Libs.Tests;

public sealed class PthreadCreateAbiTests
{
    private const ulong ThreadOutputAddress = 0x51_0000;
    private const ulong NameAddress = 0x52_0000;

    [Fact]
    public void PosixPthreadCreateIgnoresStaleFifthArgument()
    {
        var context = CreateContext("stale-register-value");
        try
        {
            Assert.Equal(
                (int)OrbisGen2Result.ORBIS_GEN2_OK,
                KernelExports.PosixPthreadCreate(context));

            var identity = ReadCreatedThreadIdentity(context);
            Assert.StartsWith("Thread-", identity.Name, StringComparison.Ordinal);
            Assert.NotEqual("stale-register-value", identity.Name);
        }
        finally
        {
            ReleaseCreatedThread(context);
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void NamedPthreadCreateVariantsConsumeFifthArgument(bool sceVariant)
    {
        var context = CreateContext("named-worker");
        try
        {
            var result = sceVariant
                ? KernelExports.PthreadCreate(context)
                : KernelExports.PosixPthreadCreateNameNp(context);

            Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_OK, result);
            Assert.Equal("named-worker", ReadCreatedThreadIdentity(context).Name);
        }
        finally
        {
            ReleaseCreatedThread(context);
        }
    }

    [Fact]
    public void PthreadCreateRejectsNullThreadOutputBeforeAllocatingAHandle()
    {
        var context = CreateContext("null-output");
        context[CpuRegister.Rdi] = 0;

        Assert.Equal(
            (int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT,
            KernelExports.PosixPthreadCreate(context));
    }

    [Fact]
    public void SchedulerFailureReleasesPublishedThreadHandle()
    {
        var context = CreateContext("schedule-failure");
        context[CpuRegister.Rdx] = 0x1234_0000;
        var scheduler = new FailingScheduler();
        var previousScheduler = GuestThreadExecution.Scheduler;
        GuestThreadExecution.Scheduler = scheduler;
        try
        {
            Assert.Equal(
                (int)OrbisGen2Result.ORBIS_GEN2_ERROR_TRY_AGAIN,
                KernelExports.PthreadCreate(context));
            Assert.True(context.TryReadUInt64(ThreadOutputAddress, out var threadHandle));
            Assert.Equal(0UL, threadHandle);
            Assert.NotEqual(0UL, scheduler.RequestedThreadHandle);
            Assert.False(
                KernelPthreadState.TryGetThreadIdentity(
                    scheduler.RequestedThreadHandle,
                    out _));
        }
        finally
        {
            GuestThreadExecution.Scheduler = previousScheduler;
        }
    }

    [Fact]
    public void PthreadCreateRejectsUnmappedThreadOutputBeforeScheduling()
    {
        var context = CreateContext("bad-output");
        context[CpuRegister.Rdi] = 0xDEAD_0000;
        context[CpuRegister.Rdx] = 0x1234_0000;
        var scheduler = new FailingScheduler();
        var previousScheduler = GuestThreadExecution.Scheduler;
        GuestThreadExecution.Scheduler = scheduler;
        try
        {
            Assert.Equal(
                (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT,
                KernelExports.PosixPthreadCreate(context));
            Assert.Equal(0UL, scheduler.RequestedThreadHandle);
        }
        finally
        {
            GuestThreadExecution.Scheduler = previousScheduler;
        }
    }

    private static CpuContext CreateContext(string name)
    {
        var memory = new FakeGuestMemory();
        memory.AddRegion(ThreadOutputAddress, new byte[sizeof(ulong)]);
        var nameBuffer = new byte[256];
        Encoding.UTF8.GetBytes(name + '\0').CopyTo(nameBuffer, 0);
        memory.AddRegion(NameAddress, nameBuffer);
        return new CpuContext(memory, Generation.Gen5)
        {
            [CpuRegister.Rdi] = ThreadOutputAddress,
            [CpuRegister.Rsi] = 0,
            [CpuRegister.Rdx] = 0,
            [CpuRegister.Rcx] = 0,
            [CpuRegister.R8] = NameAddress,
        };
    }

    private static KernelPthreadState.ThreadIdentity ReadCreatedThreadIdentity(
        CpuContext context)
    {
        Assert.True(context.TryReadUInt64(ThreadOutputAddress, out var handle));
        Assert.NotEqual(0UL, handle);
        Assert.True(KernelPthreadState.TryGetThreadIdentity(handle, out var identity));
        return identity;
    }

    private static void ReleaseCreatedThread(CpuContext context)
    {
        if (!context.TryReadUInt64(ThreadOutputAddress, out var handle) || handle == 0)
        {
            return;
        }

        KernelPthreadExtendedCompatExports.ReleaseThreadState(handle);
        KernelPthreadState.ReleaseThreadHandle(handle);
        _ = context.TryWriteUInt64(ThreadOutputAddress, 0);
    }

    private sealed class FailingScheduler : IGuestThreadScheduler
    {
        public bool SupportsGuestContextTransfer => true;

        public ulong RequestedThreadHandle { get; private set; }

        public void RegisterGuestThreadContext(ulong threadHandle, CpuContext context)
        {
        }

        public bool TryStartThread(
            CpuContext creatorContext,
            GuestThreadStartRequest request,
            out string? error)
        {
            RequestedThreadHandle = request.ThreadHandle;
            error = "synthetic scheduler failure";
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
