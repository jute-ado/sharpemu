// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using SharpEmu.Libs.Kernel;
using Xunit;

namespace SharpEmu.Libs.Tests.Pthread;

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class PthreadThreadExitCollection
{
    public const string Name = "PthreadThreadExit";
}

[Collection(PthreadThreadExitCollection.Name)]
public sealed class PthreadThreadExitSemanticsTests
{
    private const ulong MutexAddress = 0x1_0000;
    private const ulong CondAddress = 0x2_0000;
    private const ulong RwlockAddress = 0x3_0000;

    [Fact]
    public void GuestThreadExit_ReleasesOwnedMutex()
    {
        var fixture = CreateMutexFixture();
        var ownerThread = KernelPthreadState.CreateThreadHandle("mutex-owner");
        var successorThread = KernelPthreadState.CreateThreadHandle("mutex-successor");

        try
        {
            Assert.Equal(0, LockAs(fixture, ownerThread));

            GuestThreadExecution.NotifyGuestThreadExited(ownerThread);

            Assert.Equal(0, TryLockAs(fixture, successorThread));
            Assert.Equal(0, UnlockAs(fixture, successorThread));
        }
        finally
        {
            DestroyMutex(fixture);
        }
    }

    [Fact]
    public async Task GuestThreadExit_WakesQueuedMutexSuccessor()
    {
        GuestThreadBlocking.BeginExecution();
        var fixture = CreateMutexFixture();
        var ownerThread = KernelPthreadState.CreateThreadHandle("mutex-owner");
        var successorThread = KernelPthreadState.CreateThreadHandle("mutex-successor");

        try
        {
            Assert.Equal(0, LockAs(fixture, ownerThread));
            var successor = Task.Run(() =>
            {
                var lockResult = LockAs(fixture, successorThread);
                if (lockResult != 0)
                {
                    return lockResult;
                }

                return UnlockAs(fixture, successorThread);
            });
            AssertBlocked(successorThread, "pthread_mutex_lock");

            GuestThreadExecution.NotifyGuestThreadExited(ownerThread);

            Assert.Equal(0, await successor.WaitAsync(TimeSpan.FromSeconds(5)));
        }
        finally
        {
            DestroyMutex(fixture);
            GuestThreadBlocking.BeginExecution();
        }
    }

    [Fact]
    public async Task ConditionSignal_WakesWaiterAndRelocksFreeMutex()
    {
        GuestThreadBlocking.BeginExecution();
        var fixture = CreateMutexFixture();
        var waiterThread = KernelPthreadState.CreateThreadHandle("cond-waiter");
        InitializeCond(fixture);

        try
        {
            var waiter = Task.Run(() =>
            {
                var previous = GuestThreadExecution.EnterGuestThread(waiterThread);
                try
                {
                    var context = CreateContext(fixture.Memory);
                    context[CpuRegister.Rdi] = MutexAddress;
                    var result = KernelPthreadCompatExports.PosixPthreadMutexLock(context);
                    if (result != 0)
                    {
                        return result;
                    }

                    context[CpuRegister.Rdi] = CondAddress;
                    context[CpuRegister.Rsi] = MutexAddress;
                    result = KernelPthreadCompatExports.PosixPthreadCondWait(context);
                    if (result != 0)
                    {
                        return result;
                    }

                    context[CpuRegister.Rdi] = MutexAddress;
                    return KernelPthreadCompatExports.PosixPthreadMutexUnlock(context);
                }
                finally
                {
                    GuestThreadExecution.RestoreGuestThread(previous);
                }
            });
            AssertBlocked(waiterThread, "pthread_cond_wait");

            var signalContext = CreateContext(fixture.Memory);
            signalContext[CpuRegister.Rdi] = CondAddress;
            Assert.Equal(
                0,
                KernelPthreadCompatExports.PosixPthreadCondSignal(signalContext));

            Assert.Equal(0, await waiter.WaitAsync(TimeSpan.FromSeconds(5)));
        }
        finally
        {
            var context = CreateContext(fixture.Memory);
            context[CpuRegister.Rdi] = CondAddress;
            _ = KernelPthreadCompatExports.PthreadCondDestroy(context);
            DestroyMutex(fixture);
            GuestThreadBlocking.BeginExecution();
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void GuestThreadExit_ReleasesAllOwnedRwlockDepth(bool write)
    {
        var fixture = CreateRwlockFixture();
        var ownerThread = KernelPthreadState.CreateThreadHandle("rwlock-owner");

        Assert.Equal(0, LockRwlockAs(fixture, ownerThread, write));
        Assert.Equal(0, LockRwlockAs(fixture, ownerThread, write));

        GuestThreadExecution.NotifyGuestThreadExited(ownerThread);

        Assert.Equal(0, DestroyRwlock(fixture));
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task GuestThreadExit_WakesQueuedRwlockSuccessor(
        bool ownerWrites,
        bool successorWrites)
    {
        GuestThreadBlocking.BeginExecution();
        var fixture = CreateRwlockFixture();
        var ownerThread = KernelPthreadState.CreateThreadHandle("rwlock-owner");
        var successorThread =
            KernelPthreadState.CreateThreadHandle("rwlock-successor");
        Task<int>? successor = null;

        try
        {
            Assert.Equal(
                0,
                LockRwlockAs(fixture, ownerThread, ownerWrites));
            successor = Task.Run(() =>
            {
                var lockResult =
                    LockRwlockAs(
                        fixture,
                        successorThread,
                        successorWrites);
                if (lockResult != 0)
                {
                    return lockResult;
                }

                return UnlockRwlockAs(fixture, successorThread);
            });
            AssertBlocked(
                successorThread,
                successorWrites
                    ? "pthread_rwlock_wrlock"
                    : "pthread_rwlock_rdlock");

            GuestThreadExecution.NotifyGuestThreadExited(ownerThread);

            Assert.Equal(0, await successor.WaitAsync(TimeSpan.FromSeconds(5)));
        }
        finally
        {
            GuestThreadBlocking.BeginExecution();
            if (successor is not null)
            {
                _ = await successor.WaitAsync(TimeSpan.FromSeconds(5));
            }

            _ = DestroyRwlock(fixture);
            GuestThreadBlocking.BeginExecution();
        }
    }

    private static MutexFixture CreateMutexFixture()
    {
        var memory = new FakeGuestMemory();
        memory.AddRegion(MutexAddress, new byte[sizeof(ulong)]);
        var context = CreateContext(memory);
        context[CpuRegister.Rdi] = MutexAddress;
        context[CpuRegister.Rsi] = 0;
        Assert.Equal(0, KernelPthreadCompatExports.PosixPthreadMutexInit(context));
        return new MutexFixture(memory);
    }

    private static void InitializeCond(MutexFixture fixture)
    {
        fixture.Memory.AddRegion(CondAddress, new byte[sizeof(ulong)]);
        var context = CreateContext(fixture.Memory);
        context[CpuRegister.Rdi] = CondAddress;
        context[CpuRegister.Rsi] = 0;
        Assert.Equal(0, KernelPthreadCompatExports.PosixPthreadCondInit(context));
    }

    private static RwlockFixture CreateRwlockFixture()
    {
        var memory = new FakeGuestMemory();
        memory.AddRegion(RwlockAddress, new byte[sizeof(ulong)]);
        var context = CreateContext(memory);
        context[CpuRegister.Rdi] = RwlockAddress;
        context[CpuRegister.Rsi] = 0;
        Assert.Equal(
            0,
            KernelPthreadExtendedCompatExports.PosixPthreadRwlockInit(context));
        return new RwlockFixture(memory);
    }

    private static int LockAs(MutexFixture fixture, ulong threadHandle) =>
        InvokeAs(
            fixture,
            threadHandle,
            KernelPthreadCompatExports.PosixPthreadMutexLock);

    private static int TryLockAs(MutexFixture fixture, ulong threadHandle) =>
        InvokeAs(
            fixture,
            threadHandle,
            KernelPthreadCompatExports.PosixPthreadMutexTrylock);

    private static int UnlockAs(MutexFixture fixture, ulong threadHandle) =>
        InvokeAs(
            fixture,
            threadHandle,
            KernelPthreadCompatExports.PosixPthreadMutexUnlock);

    private static int LockRwlockAs(
        RwlockFixture fixture,
        ulong threadHandle,
        bool write) =>
        InvokeAs(
            fixture.Memory,
            RwlockAddress,
            threadHandle,
            write
                ? KernelPthreadExtendedCompatExports.PosixPthreadRwlockWrlock
                : KernelPthreadExtendedCompatExports.PosixPthreadRwlockRdlock);

    private static int UnlockRwlockAs(
        RwlockFixture fixture,
        ulong threadHandle) =>
        InvokeAs(
            fixture.Memory,
            RwlockAddress,
            threadHandle,
            KernelPthreadExtendedCompatExports.PosixPthreadRwlockUnlock);

    private static int InvokeAs(
        MutexFixture fixture,
        ulong threadHandle,
        Func<CpuContext, int> operation) =>
        InvokeAs(
            fixture.Memory,
            MutexAddress,
            threadHandle,
            operation);

    private static int InvokeAs(
        FakeGuestMemory memory,
        ulong address,
        ulong threadHandle,
        Func<CpuContext, int> operation)
    {
        var previous = GuestThreadExecution.EnterGuestThread(threadHandle);
        try
        {
            var context = CreateContext(memory);
            context[CpuRegister.Rdi] = address;
            return operation(context);
        }
        finally
        {
            GuestThreadExecution.RestoreGuestThread(previous);
        }
    }

    private static int DestroyRwlock(RwlockFixture fixture)
    {
        var context = CreateContext(fixture.Memory);
        context[CpuRegister.Rdi] = RwlockAddress;
        return KernelPthreadExtendedCompatExports.PosixPthreadRwlockDestroy(
            context);
    }

    private static void AssertBlocked(ulong threadHandle, string reason)
    {
        Assert.True(
            SpinWait.SpinUntil(
                () => GuestThreadBlocking.DescribeBlock(threadHandle) == reason,
                TimeSpan.FromSeconds(2)),
            $"Guest thread 0x{threadHandle:X} did not block in {reason}.");
    }

    private static void DestroyMutex(MutexFixture fixture)
    {
        var context = CreateContext(fixture.Memory);
        context[CpuRegister.Rdi] = MutexAddress;
        _ = KernelPthreadCompatExports.PosixPthreadMutexDestroy(context);
    }

    private static CpuContext CreateContext(FakeGuestMemory memory) =>
        new(memory, Generation.Gen5);

    private sealed record MutexFixture(FakeGuestMemory Memory);

    private sealed record RwlockFixture(FakeGuestMemory Memory);
}
