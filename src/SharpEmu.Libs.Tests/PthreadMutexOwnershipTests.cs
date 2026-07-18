// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using SharpEmu.Libs.Kernel;
using Xunit;

namespace SharpEmu.Libs.Tests;

public sealed class PthreadMutexOwnershipTests
{
    private const ulong MutexAddress = 0x71_0000;
    private const ulong AttrAddress = 0x72_0000;

    [Theory]
    [InlineData(3)]
    [InlineData(4)]
    public void NormalAndAdaptiveMutexesRejectUnlockWhenNotLocked(
        int mutexType)
    {
        var context = CreateInitializedMutex(mutexType);
        try
        {
            context[CpuRegister.Rdi] = MutexAddress;

            Assert.Equal(
                (int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT,
                KernelPthreadCompatExports.PosixPthreadMutexUnlock(context));
        }
        finally
        {
            DestroyMutex(context);
        }
    }

    [Theory]
    [InlineData(3)]
    [InlineData(4)]
    public void NormalAndAdaptiveMutexesRejectNonOwnerUnlock(
        int mutexType)
    {
        var context = CreateInitializedMutex(mutexType);
        try
        {
            using var ownerReady = new ManualResetEventSlim();
            using var allowOwnerUnlock = new ManualResetEventSlim();
            var ownerLockResult = int.MinValue;
            var ownerUnlockResult = int.MinValue;
            var ownerThread = new Thread(
                () =>
                {
                    var ownerContext = new CpuContext(
                        context.Memory,
                        Generation.Gen5);
                    ownerContext[CpuRegister.Rdi] = MutexAddress;
                    ownerLockResult =
                        KernelPthreadCompatExports.PosixPthreadMutexLock(
                            ownerContext);
                    ownerReady.Set();
                    allowOwnerUnlock.Wait();
                    ownerContext[CpuRegister.Rdi] = MutexAddress;
                    ownerUnlockResult =
                        KernelPthreadCompatExports.PosixPthreadMutexUnlock(
                            ownerContext);
                });
            ownerThread.Start();
            var foreignResult = int.MinValue;
            try
            {
                Assert.True(ownerReady.Wait(TimeSpan.FromSeconds(5)));
                context[CpuRegister.Rdi] = MutexAddress;
                foreignResult =
                    KernelPthreadCompatExports.PosixPthreadMutexUnlock(
                        context);
            }
            finally
            {
                allowOwnerUnlock.Set();
                Assert.True(ownerThread.Join(TimeSpan.FromSeconds(5)));
            }

            Assert.Equal(
                (int)OrbisGen2Result.ORBIS_GEN2_OK,
                ownerLockResult);
            Assert.Equal(
                (int)OrbisGen2Result.ORBIS_GEN2_ERROR_PERMISSION_DENIED,
                foreignResult);
            Assert.Equal(
                (int)OrbisGen2Result.ORBIS_GEN2_OK,
                ownerUnlockResult);
        }
        finally
        {
            DestroyMutex(context);
        }
    }

    [Fact]
    public void DestroyRejectsLockedMutexAndPreservesItForOwnerUnlock()
    {
        var context = CreateInitializedMutex(mutexType: 3);
        try
        {
            context[CpuRegister.Rdi] = MutexAddress;
            Assert.Equal(
                (int)OrbisGen2Result.ORBIS_GEN2_OK,
                KernelPthreadCompatExports.PosixPthreadMutexLock(context));

            context[CpuRegister.Rdi] = MutexAddress;
            Assert.Equal(
                (int)OrbisGen2Result.ORBIS_GEN2_ERROR_BUSY,
                KernelPthreadCompatExports.PosixPthreadMutexDestroy(context));

            context[CpuRegister.Rdi] = MutexAddress;
            Assert.Equal(
                (int)OrbisGen2Result.ORBIS_GEN2_OK,
                KernelPthreadCompatExports.PosixPthreadMutexUnlock(context));

            context[CpuRegister.Rdi] = MutexAddress;
            Assert.Equal(
                (int)OrbisGen2Result.ORBIS_GEN2_OK,
                KernelPthreadCompatExports.PosixPthreadMutexDestroy(context));
        }
        finally
        {
            DestroyMutex(context);
        }
    }

    [Fact]
    public void ContendedMutexGrantsHostWaitersInFifoOrder()
    {
        var context = CreateInitializedMutex(mutexType: 3);
        var acquisitionOrder = new List<int>();
        var firstResult = int.MinValue;
        var secondResult = int.MinValue;

        try
        {
            context[CpuRegister.Rdi] = MutexAddress;
            Assert.Equal(
                (int)OrbisGen2Result.ORBIS_GEN2_OK,
                KernelPthreadCompatExports.PosixPthreadMutexLock(context));

            var first = CreateQueuedLocker(
                context.Memory,
                marker: 1,
                acquisitionOrder,
                result => firstResult = result);
            first.Start();
            Assert.True(WaitForQueuedWaiters(expected: 1));

            var second = CreateQueuedLocker(
                context.Memory,
                marker: 2,
                acquisitionOrder,
                result => secondResult = result);
            second.Start();
            Assert.True(WaitForQueuedWaiters(expected: 2));

            context[CpuRegister.Rdi] = MutexAddress;
            Assert.Equal(
                (int)OrbisGen2Result.ORBIS_GEN2_ERROR_BUSY,
                KernelPthreadCompatExports.PosixPthreadMutexDestroy(context));

            context[CpuRegister.Rdi] = MutexAddress;
            Assert.Equal(
                (int)OrbisGen2Result.ORBIS_GEN2_OK,
                KernelPthreadCompatExports.PosixPthreadMutexUnlock(context));

            Assert.True(first.Join(TimeSpan.FromSeconds(5)));
            Assert.True(second.Join(TimeSpan.FromSeconds(5)));
            Assert.Equal(
                (int)OrbisGen2Result.ORBIS_GEN2_OK,
                firstResult);
            Assert.Equal(
                (int)OrbisGen2Result.ORBIS_GEN2_OK,
                secondResult);
            Assert.Equal([1, 2], acquisitionOrder);
        }
        finally
        {
            DestroyMutex(context);
        }
    }

    private static Thread CreateQueuedLocker(
        ICpuMemory memory,
        int marker,
        List<int> acquisitionOrder,
        Action<int> setResult)
    {
        return new Thread(
            () =>
            {
                var waiterContext = new CpuContext(memory, Generation.Gen5);
                waiterContext[CpuRegister.Rdi] = MutexAddress;
                var result =
                    KernelPthreadCompatExports.PosixPthreadMutexLock(
                        waiterContext);
                if (result != (int)OrbisGen2Result.ORBIS_GEN2_OK)
                {
                    setResult(result);
                    return;
                }

                acquisitionOrder.Add(marker);
                waiterContext[CpuRegister.Rdi] = MutexAddress;
                setResult(
                    KernelPthreadCompatExports.PosixPthreadMutexUnlock(
                        waiterContext));
            })
        {
            IsBackground = true,
        };
    }

    private static bool WaitForQueuedWaiters(int expected)
    {
        return SpinWait.SpinUntil(
            () =>
                KernelPthreadCompatExports.TryGetMutexStateSnapshot(
                    MutexAddress,
                    out _,
                    out _,
                    out var waiterCount) &&
                waiterCount == expected,
            TimeSpan.FromSeconds(5));
    }

    private static CpuContext CreateInitializedMutex(int mutexType)
    {
        var memory = new FakeGuestMemory();
        memory.AddRegion(MutexAddress, new byte[sizeof(ulong)]);
        memory.AddRegion(AttrAddress, new byte[sizeof(ulong)]);
        var context = new CpuContext(memory, Generation.Gen5);

        context[CpuRegister.Rdi] = AttrAddress;
        Assert.Equal(
            (int)OrbisGen2Result.ORBIS_GEN2_OK,
            KernelPthreadCompatExports.PosixPthreadMutexattrInit(context));

        context[CpuRegister.Rdi] = AttrAddress;
        context[CpuRegister.Rsi] = unchecked((ulong)mutexType);
        Assert.Equal(
            (int)OrbisGen2Result.ORBIS_GEN2_OK,
            KernelPthreadCompatExports.PosixPthreadMutexattrSettype(context));

        context[CpuRegister.Rdi] = MutexAddress;
        context[CpuRegister.Rsi] = AttrAddress;
        Assert.Equal(
            (int)OrbisGen2Result.ORBIS_GEN2_OK,
            KernelPthreadCompatExports.PosixPthreadMutexInit(context));
        return context;
    }

    private static void DestroyMutex(CpuContext context)
    {
        context[CpuRegister.Rdi] = MutexAddress;
        _ = KernelPthreadCompatExports.PosixPthreadMutexDestroy(context);
        context[CpuRegister.Rdi] = AttrAddress;
        _ = KernelPthreadCompatExports.PosixPthreadMutexattrDestroy(context);
    }
}
