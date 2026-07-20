// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using SharpEmu.HLE;
using SharpEmu.Libs.Kernel;
using Xunit;

namespace SharpEmu.Libs.Tests;

public sealed class PthreadConditionCompatibilityTests : IDisposable
{
    private const int PosixMemoryFault = 14;
    private const int PosixInvalidArgument = 22;
    private const int PosixTimedOut = 60;
    private const ulong CondAddress = 0x1000;
    private const ulong MutexAddress = 0x2000;
    private const ulong DeadlineAddress = 0x3000;
    private const ulong MutexAttrAddress = 0x4000;

    public PthreadConditionCompatibilityTests() =>
        GuestThreadBlocking.BeginExecution();

    public void Dispose()
    {
        GuestThreadBlocking.RequestShutdown();
        GuestThreadBlocking.BeginExecution();
    }

    [Fact]
    public void DestroyReleasesAllocatedConditionMutexAndAttributeObjects()
    {
        var memory = new FakeGuestMemory();
        var condHandle = new byte[sizeof(ulong)];
        var mutexHandle = new byte[sizeof(ulong)];
        var attrHandle = new byte[sizeof(ulong)];
        memory.AddRegion(CondAddress, condHandle);
        memory.AddRegion(MutexAddress, mutexHandle);
        memory.AddRegion(MutexAttrAddress, attrHandle);
        var context = new CpuContext(memory, Generation.Gen5);

        context[CpuRegister.Rdi] = CondAddress;
        Assert.Equal(
            (int)OrbisGen2Result.ORBIS_GEN2_OK,
            KernelPthreadCompatExports.PosixPthreadCondInit(context));
        context[CpuRegister.Rdi] = MutexAddress;
        context[CpuRegister.Rsi] = 0;
        Assert.Equal(
            (int)OrbisGen2Result.ORBIS_GEN2_OK,
            KernelPthreadCompatExports.PosixPthreadMutexInit(context));
        context[CpuRegister.Rdi] = MutexAttrAddress;
        Assert.Equal(
            (int)OrbisGen2Result.ORBIS_GEN2_OK,
            KernelPthreadCompatExports.PosixPthreadMutexattrInit(context));
        Assert.Equal(3, memory.GuestAllocationCount);

        context[CpuRegister.Rdi] = CondAddress;
        Assert.Equal(
            (int)OrbisGen2Result.ORBIS_GEN2_OK,
            KernelPthreadCompatExports.PthreadCondDestroy(context));
        context[CpuRegister.Rdi] = MutexAddress;
        Assert.Equal(
            (int)OrbisGen2Result.ORBIS_GEN2_OK,
            KernelPthreadCompatExports.PosixPthreadMutexDestroy(context));
        context[CpuRegister.Rdi] = MutexAttrAddress;
        Assert.Equal(
            (int)OrbisGen2Result.ORBIS_GEN2_OK,
            KernelPthreadCompatExports.PosixPthreadMutexattrDestroy(context));

        Assert.Equal(0, memory.GuestAllocationCount);
        Assert.Equal(0UL, BinaryPrimitives.ReadUInt64LittleEndian(condHandle));
        Assert.Equal(0UL, BinaryPrimitives.ReadUInt64LittleEndian(mutexHandle));
        Assert.Equal(0UL, BinaryPrimitives.ReadUInt64LittleEndian(attrHandle));
    }

    [Fact]
    public void FailedMutexInitializationReleasesAllocatedOpaqueObject()
    {
        var memory = new FakeGuestMemory();
        var context = new CpuContext(memory, Generation.Gen5);
        context[CpuRegister.Rdi] = MutexAddress;
        context[CpuRegister.Rsi] = 0;

        Assert.Equal(
            (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT,
            KernelPthreadCompatExports.PosixPthreadMutexInit(context));
        Assert.Equal(0, memory.GuestAllocationCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PosixSignalWithoutWaitersIsNotConsumedByFutureWait(bool broadcast)
    {
        var context = CreateContext();
        InitializeConditionAndMutex(context);

        try
        {
            context[CpuRegister.Rdi] = CondAddress;
            Assert.Equal(
                (int)OrbisGen2Result.ORBIS_GEN2_OK,
                broadcast
                    ? KernelPthreadCompatExports.PosixPthreadCondBroadcast(context)
                    : KernelPthreadCompatExports.PosixPthreadCondSignal(context));

            LockMutex(context);
            context[CpuRegister.Rdi] = CondAddress;
            context[CpuRegister.Rsi] = MutexAddress;
            context[CpuRegister.Rdx] = DeadlineAddress;

            Assert.Equal(
                PosixTimedOut,
                KernelPthreadCompatExports.PosixPthreadCondTimedwait(context));

            UnlockMutex(context);
        }
        finally
        {
            DestroyConditionAndMutex(context);
        }
    }

    [Fact]
    public void SceSignalWithoutWaitersRetainsCompatibilityLatch()
    {
        var context = CreateContext();
        InitializeConditionAndMutex(context);

        try
        {
            context[CpuRegister.Rdi] = CondAddress;
            Assert.Equal(
                (int)OrbisGen2Result.ORBIS_GEN2_OK,
                KernelPthreadCompatExports.PthreadCondSignal(context));

            LockMutex(context);
            context[CpuRegister.Rdi] = CondAddress;
            context[CpuRegister.Rsi] = MutexAddress;
            context[CpuRegister.Rdx] = 1;

            Assert.Equal(
                (int)OrbisGen2Result.ORBIS_GEN2_OK,
                KernelPthreadCompatExports.PthreadCondTimedwait(context));

            UnlockMutex(context);
        }
        finally
        {
            DestroyConditionAndMutex(context);
        }
    }

    [Theory]
    [InlineData(-1L)]
    [InlineData(1_000_000_000L)]
    public void PosixTimedwaitRejectsInvalidNanoseconds(long nanoseconds)
    {
        var context = CreateContext(deadlineNanoseconds: nanoseconds);
        InitializeConditionAndMutex(context);

        try
        {
            LockMutex(context);
            context[CpuRegister.Rdi] = CondAddress;
            context[CpuRegister.Rsi] = MutexAddress;
            context[CpuRegister.Rdx] = DeadlineAddress;

            Assert.Equal(
                PosixInvalidArgument,
                KernelPthreadCompatExports.PosixPthreadCondTimedwait(context));

            UnlockMutex(context);
        }
        finally
        {
            DestroyConditionAndMutex(context);
        }
    }

    [Fact]
    public void PosixTimedwaitRejectsNullDeadlineWithEinval()
    {
        var context = CreateContext();
        context[CpuRegister.Rdi] = CondAddress;
        context[CpuRegister.Rsi] = MutexAddress;
        context[CpuRegister.Rdx] = 0;

        Assert.Equal(
            PosixInvalidArgument,
            KernelPthreadCompatExports.PosixPthreadCondTimedwait(context));
    }

    [Fact]
    public void PosixTimedwaitRejectsUnmappedDeadlineWithEfault()
    {
        var context = CreateContext();
        context[CpuRegister.Rdi] = CondAddress;
        context[CpuRegister.Rsi] = MutexAddress;
        context[CpuRegister.Rdx] = 0xDEAD_0000;

        Assert.Equal(
            PosixMemoryFault,
            KernelPthreadCompatExports.PosixPthreadCondTimedwait(context));
    }

    [Fact]
    public void PosixTimedwaitTreatsNegativeSecondsAsExpired()
    {
        var context = CreateContext(deadlineSeconds: -1);
        InitializeConditionAndMutex(context);

        try
        {
            LockMutex(context);
            context[CpuRegister.Rdi] = CondAddress;
            context[CpuRegister.Rsi] = MutexAddress;
            context[CpuRegister.Rdx] = DeadlineAddress;

            Assert.Equal(
                PosixTimedOut,
                KernelPthreadCompatExports.PosixPthreadCondTimedwait(context));

            UnlockMutex(context);
        }
        finally
        {
            DestroyConditionAndMutex(context);
        }
    }

    [Fact]
    public void SignaledWaiterReacquiresMutexBeforeReturning()
    {
        var context = CreateContext();
        InitializeConditionAndMutex(context);
        using var waiterStarted = new ManualResetEventSlim();
        using var waiterReturned = new ManualResetEventSlim();
        var waitResult = int.MinValue;
        var unlockResult = int.MinValue;
        var waiterThread = new Thread(
            () =>
            {
                var waiterContext = new CpuContext(
                    context.Memory,
                    Generation.Gen5);
                waiterContext[CpuRegister.Rdi] = MutexAddress;
                var lockResult =
                    KernelPthreadCompatExports.PosixPthreadMutexLock(
                        waiterContext);
                if (lockResult != (int)OrbisGen2Result.ORBIS_GEN2_OK)
                {
                    waitResult = lockResult;
                    waiterReturned.Set();
                    return;
                }

                waiterStarted.Set();
                waiterContext[CpuRegister.Rdi] = CondAddress;
                waiterContext[CpuRegister.Rsi] = MutexAddress;
                waitResult =
                    KernelPthreadCompatExports.PosixPthreadCondWait(
                        waiterContext);
                waiterReturned.Set();

                waiterContext[CpuRegister.Rdi] = MutexAddress;
                unlockResult =
                    KernelPthreadCompatExports.PosixPthreadMutexUnlock(
                        waiterContext);
            })
        {
            IsBackground = true,
        };

        try
        {
            waiterThread.Start();
            Assert.True(waiterStarted.Wait(TimeSpan.FromSeconds(5)));
            Assert.True(
                SpinWait.SpinUntil(
                    () =>
                        KernelPthreadCompatExports
                            .TryGetConditionStateSnapshot(
                                CondAddress,
                                out var waiterCount) &&
                        waiterCount == 1,
                    TimeSpan.FromSeconds(5)));

            context[CpuRegister.Rdi] = CondAddress;
            Assert.Equal(
                (int)OrbisGen2Result.ORBIS_GEN2_ERROR_BUSY,
                KernelPthreadCompatExports.PthreadCondDestroy(context));
            context[CpuRegister.Rdi] = MutexAddress;
            Assert.Equal(
                (int)OrbisGen2Result.ORBIS_GEN2_ERROR_BUSY,
                KernelPthreadCompatExports.PosixPthreadMutexDestroy(
                    context));

            LockMutex(context);
            context[CpuRegister.Rdi] = CondAddress;
            Assert.Equal(
                (int)OrbisGen2Result.ORBIS_GEN2_OK,
                KernelPthreadCompatExports.PosixPthreadCondSignal(
                    context));

            Assert.False(
                waiterReturned.Wait(TimeSpan.FromMilliseconds(100)));
            UnlockMutex(context);

            Assert.True(waiterThread.Join(TimeSpan.FromSeconds(5)));
            Assert.Equal(
                (int)OrbisGen2Result.ORBIS_GEN2_OK,
                waitResult);
            Assert.Equal(
                (int)OrbisGen2Result.ORBIS_GEN2_OK,
                unlockResult);
        }
        finally
        {
            DestroyConditionAndMutex(context);
        }
    }

    [Fact]
    public void ConditionWaitRestoresRecursiveMutexDepth()
    {
        var context = CreateContext();
        InitializeConditionAndMutex(context, mutexType: 2);

        try
        {
            context[CpuRegister.Rdi] = CondAddress;
            Assert.Equal(
                (int)OrbisGen2Result.ORBIS_GEN2_OK,
                KernelPthreadCompatExports.PthreadCondSignal(context));

            LockMutex(context);
            LockMutex(context);

            context[CpuRegister.Rdi] = CondAddress;
            context[CpuRegister.Rsi] = MutexAddress;
            context[CpuRegister.Rdx] = 1;
            Assert.Equal(
                (int)OrbisGen2Result.ORBIS_GEN2_OK,
                KernelPthreadCompatExports.PthreadCondTimedwait(context));

            UnlockMutex(context);
            UnlockMutex(context);
            context[CpuRegister.Rdi] = MutexAddress;
            Assert.Equal(
                (int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT,
                KernelPthreadCompatExports.PosixPthreadMutexUnlock(context));
        }
        finally
        {
            DestroyConditionAndMutex(context);
        }
    }

    private static CpuContext CreateContext(
        long deadlineSeconds = 0,
        long deadlineNanoseconds = 0)
    {
        var memory = new FakeGuestMemory();
        memory.AddRegion(CondAddress, new byte[sizeof(ulong)]);
        memory.AddRegion(MutexAddress, new byte[sizeof(ulong)]);
        memory.AddRegion(MutexAttrAddress, new byte[sizeof(ulong)]);
        var deadline = new byte[2 * sizeof(ulong)];
        BinaryPrimitives.WriteInt64LittleEndian(deadline, deadlineSeconds);
        BinaryPrimitives.WriteInt64LittleEndian(deadline.AsSpan(sizeof(ulong)), deadlineNanoseconds);
        memory.AddRegion(DeadlineAddress, deadline);
        return new CpuContext(memory, Generation.Gen5);
    }

    private static void InitializeConditionAndMutex(
        CpuContext context,
        int? mutexType = null)
    {
        context[CpuRegister.Rdi] = CondAddress;
        context[CpuRegister.Rsi] = 0;
        Assert.Equal(
            (int)OrbisGen2Result.ORBIS_GEN2_OK,
            KernelPthreadCompatExports.PosixPthreadCondInit(context));

        var attrAddress = 0UL;
        if (mutexType.HasValue)
        {
            context[CpuRegister.Rdi] = MutexAttrAddress;
            Assert.Equal(
                (int)OrbisGen2Result.ORBIS_GEN2_OK,
                KernelPthreadCompatExports.PosixPthreadMutexattrInit(
                    context));
            context[CpuRegister.Rdi] = MutexAttrAddress;
            context[CpuRegister.Rsi] = unchecked((ulong)mutexType.Value);
            Assert.Equal(
                (int)OrbisGen2Result.ORBIS_GEN2_OK,
                KernelPthreadCompatExports.PosixPthreadMutexattrSettype(
                    context));
            attrAddress = MutexAttrAddress;
        }

        context[CpuRegister.Rdi] = MutexAddress;
        context[CpuRegister.Rsi] = attrAddress;
        Assert.Equal(
            (int)OrbisGen2Result.ORBIS_GEN2_OK,
            KernelPthreadCompatExports.PosixPthreadMutexInit(context));
    }

    private static void LockMutex(CpuContext context)
    {
        context[CpuRegister.Rdi] = MutexAddress;
        Assert.Equal(
            (int)OrbisGen2Result.ORBIS_GEN2_OK,
            KernelPthreadCompatExports.PosixPthreadMutexLock(context));
    }

    private static void UnlockMutex(CpuContext context)
    {
        context[CpuRegister.Rdi] = MutexAddress;
        Assert.Equal(
            (int)OrbisGen2Result.ORBIS_GEN2_OK,
            KernelPthreadCompatExports.PosixPthreadMutexUnlock(context));
    }

    private static void DestroyConditionAndMutex(CpuContext context)
    {
        context[CpuRegister.Rdi] = CondAddress;
        _ = KernelPthreadCompatExports.PthreadCondDestroy(context);
        context[CpuRegister.Rdi] = MutexAddress;
        _ = KernelPthreadCompatExports.PosixPthreadMutexDestroy(context);
        context[CpuRegister.Rdi] = MutexAttrAddress;
        _ = KernelPthreadCompatExports.PosixPthreadMutexattrDestroy(context);
    }
}
