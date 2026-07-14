// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using SharpEmu.HLE;
using SharpEmu.Libs.Kernel;
using Xunit;

namespace SharpEmu.Libs.Tests;

public sealed class PthreadConditionCompatibilityTests
{
    private const int PosixTimedOut = 60;
    private const ulong CondAddress = 0x1000;
    private const ulong MutexAddress = 0x2000;
    private const ulong DeadlineAddress = 0x3000;

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
                (int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT,
                KernelPthreadCompatExports.PosixPthreadCondTimedwait(context));

            UnlockMutex(context);
        }
        finally
        {
            DestroyConditionAndMutex(context);
        }
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

    private static CpuContext CreateContext(
        long deadlineSeconds = 0,
        long deadlineNanoseconds = 0)
    {
        var memory = new FakeGuestMemory();
        memory.AddRegion(CondAddress, new byte[sizeof(ulong)]);
        memory.AddRegion(MutexAddress, new byte[sizeof(ulong)]);
        var deadline = new byte[2 * sizeof(ulong)];
        BinaryPrimitives.WriteInt64LittleEndian(deadline, deadlineSeconds);
        BinaryPrimitives.WriteInt64LittleEndian(deadline.AsSpan(sizeof(ulong)), deadlineNanoseconds);
        memory.AddRegion(DeadlineAddress, deadline);
        return new CpuContext(memory, Generation.Gen5);
    }

    private static void InitializeConditionAndMutex(CpuContext context)
    {
        context[CpuRegister.Rdi] = CondAddress;
        context[CpuRegister.Rsi] = 0;
        Assert.Equal(
            (int)OrbisGen2Result.ORBIS_GEN2_OK,
            KernelPthreadCompatExports.PosixPthreadCondInit(context));

        context[CpuRegister.Rdi] = MutexAddress;
        context[CpuRegister.Rsi] = 0;
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
    }
}
