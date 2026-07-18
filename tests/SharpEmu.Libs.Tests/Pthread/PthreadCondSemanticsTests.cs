// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using SharpEmu.HLE;
using SharpEmu.Libs.Kernel;
using Xunit;

namespace SharpEmu.Libs.Tests.Pthread;

// POSIX condition variables are edges, not semaphore credits. A signal with no waiter
// must have no effect. This was violated by the previous implementation which persisted
// signals via PendingSignals, causing lock inversions and predicate bypasses.
// See issue #113.
public sealed class PthreadCondSemanticsTests
{
    [Fact]
    public void PthreadCondSignal_WithNoWaiter_DoesNotPersist()
    {
        const ulong condAddress = 0x1_0000;
        const ulong mutexAddress = 0x2_0000;
        const ulong deadlineAddress = 0x3_0000;
        var memory = new FakeGuestMemory();
        memory.AddRegion(condAddress, new byte[sizeof(ulong)]);
        memory.AddRegion(mutexAddress, new byte[sizeof(ulong)]);
        var deadline = new byte[2 * sizeof(ulong)];
        BinaryPrimitives.WriteInt64LittleEndian(deadline, -1);
        memory.AddRegion(deadlineAddress, deadline);
        var context = new CpuContext(memory, Generation.Gen5);

        context[CpuRegister.Rdi] = condAddress;
        context[CpuRegister.Rsi] = 0;
        Assert.Equal(
            (int)OrbisGen2Result.ORBIS_GEN2_OK,
            KernelPthreadCompatExports.PosixPthreadCondInit(context));
        context[CpuRegister.Rdi] = mutexAddress;
        context[CpuRegister.Rsi] = 0;
        Assert.Equal(
            (int)OrbisGen2Result.ORBIS_GEN2_OK,
            KernelPthreadCompatExports.PosixPthreadMutexInit(context));

        try
        {
            context[CpuRegister.Rdi] = condAddress;
            Assert.Equal(
                (int)OrbisGen2Result.ORBIS_GEN2_OK,
                KernelPthreadCompatExports.PosixPthreadCondSignal(
                    context));

            context[CpuRegister.Rdi] = mutexAddress;
            Assert.Equal(
                (int)OrbisGen2Result.ORBIS_GEN2_OK,
                KernelPthreadCompatExports.PosixPthreadMutexLock(
                    context));
            context[CpuRegister.Rdi] = condAddress;
            context[CpuRegister.Rsi] = mutexAddress;
            context[CpuRegister.Rdx] = deadlineAddress;
            Assert.Equal(
                60,
                KernelPthreadCompatExports.PosixPthreadCondTimedwait(
                    context));
            context[CpuRegister.Rdi] = mutexAddress;
            Assert.Equal(
                (int)OrbisGen2Result.ORBIS_GEN2_OK,
                KernelPthreadCompatExports.PosixPthreadMutexUnlock(
                    context));
        }
        finally
        {
            context[CpuRegister.Rdi] = condAddress;
            _ = KernelPthreadCompatExports.PthreadCondDestroy(context);
            context[CpuRegister.Rdi] = mutexAddress;
            _ = KernelPthreadCompatExports.PosixPthreadMutexDestroy(
                context);
        }
    }
}
