// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using SharpEmu.Libs.Kernel;
using Xunit;

namespace SharpEmu.Libs.Tests;

[CollectionDefinition(
    KernelMemorySessionStateCollection.Name,
    DisableParallelization = true)]
public sealed class KernelMemorySessionStateCollection
{
    public const string Name = "Kernel memory session state";
}

[Collection(KernelMemorySessionStateCollection.Name)]
public sealed class KernelMemorySessionResetTests
{
    private const ulong OutputAddress = 0x1000;
    private const ulong AllocationStart = 0x2800_0000;
    private const ulong AllocationLength = 0x4000;

    [Fact]
    public void ResetRuntimeStateReleasesLibcHeapAndRestartsDirectMemoryState()
    {
        var memory = new FakeGuestMemory();
        memory.AddRegion(OutputAddress, new byte[24]);
        var context = new CpuContext(memory, Generation.Gen5);

        KernelMemoryLifecycle.ResetRuntimeState();
        try
        {
            context[CpuRegister.Rdi] = 32;
            Assert.Equal(
                (int)OrbisGen2Result.ORBIS_GEN2_OK,
                KernelMemoryCompatExports.Malloc(context));
            var heapAddress = context[CpuRegister.Rax];
            Assert.NotEqual(0UL, heapAddress);
            Assert.True(KernelMemoryCompatExports.TryReadTrackedLibcHeap(
                heapAddress,
                new byte[32]));

            Assert.Equal(AllocationStart, AllocateDirectMemory(context));
            Assert.Equal(
                (int)OrbisGen2Result.ORBIS_GEN2_OK,
                QueryDirectMemory(context, AllocationStart));

            KernelMemoryLifecycle.ResetRuntimeState();

            Assert.False(KernelMemoryCompatExports.TryReadTrackedLibcHeap(
                heapAddress,
                new byte[1]));
            Assert.Equal(
                (int)OrbisGen2Result.ORBIS_GEN2_ERROR_DELETED,
                QueryDirectMemory(context, AllocationStart));
            Assert.Equal(AllocationStart, AllocateDirectMemory(context));
        }
        finally
        {
            KernelMemoryLifecycle.ResetRuntimeState();
        }
    }

    private static ulong AllocateDirectMemory(CpuContext context)
    {
        context[CpuRegister.Rdi] = AllocationStart;
        context[CpuRegister.Rsi] = AllocationStart + AllocationLength;
        context[CpuRegister.Rdx] = AllocationLength;
        context[CpuRegister.Rcx] = 0x4000;
        context[CpuRegister.R8] = 7;
        context[CpuRegister.R9] = OutputAddress;

        Assert.Equal(
            (int)OrbisGen2Result.ORBIS_GEN2_OK,
            KernelMemoryCompatExports.KernelAllocateDirectMemory(context));
        Assert.True(context.TryReadUInt64(OutputAddress, out var address));
        return address;
    }

    private static int QueryDirectMemory(CpuContext context, ulong address)
    {
        context[CpuRegister.Rdi] = address;
        context[CpuRegister.Rsi] = 0;
        context[CpuRegister.Rdx] = OutputAddress;
        context[CpuRegister.Rcx] = 24;
        return KernelMemoryCompatExports.KernelDirectMemoryQuery(context);
    }
}
