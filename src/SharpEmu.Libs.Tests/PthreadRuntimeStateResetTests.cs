// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using SharpEmu.Libs.Kernel;
using Xunit;

namespace SharpEmu.Libs.Tests;

[CollectionDefinition(
    PthreadRuntimeStateCollection.Name,
    DisableParallelization = true)]
public sealed class PthreadRuntimeStateCollection
{
    public const string Name = "Pthread runtime state";
}

[Collection(PthreadRuntimeStateCollection.Name)]
public sealed class PthreadRuntimeStateResetTests
{
    [Fact]
    public void ResetRuntimeStateDiscardsProcessStateAndRestartsKeyAllocation()
    {
        const ulong firstKeyAddress = 0x1000;
        const ulong secondKeyAddress = 0x2000;
        const ulong attrAddress = 0x3000;
        const ulong detachStateAddress = 0x4000;
        const ulong storedValue = 0x1234_5678_9ABC_DEF0;
        var memory = new FakeGuestMemory();
        memory.AddRegion(firstKeyAddress, new byte[sizeof(int)]);
        memory.AddRegion(secondKeyAddress, new byte[sizeof(int)]);
        memory.AddRegion(attrAddress, new byte[sizeof(ulong)]);
        memory.AddRegion(detachStateAddress, new byte[sizeof(int)]);
        var context = new CpuContext(memory, Generation.Gen5);

        KernelPthreadLifecycle.ResetRuntimeState();
        try
        {
            context[CpuRegister.Rdi] = firstKeyAddress;
            Assert.Equal(
                (int)OrbisGen2Result.ORBIS_GEN2_OK,
                KernelPthreadExtendedCompatExports.PosixPthreadKeyCreate(context));
            Assert.True(context.TryReadInt32(firstKeyAddress, out var firstKey));
            Assert.Equal(1, firstKey);

            context[CpuRegister.Rdi] = unchecked((ulong)firstKey);
            context[CpuRegister.Rsi] = storedValue;
            Assert.Equal(
                (int)OrbisGen2Result.ORBIS_GEN2_OK,
                KernelPthreadExtendedCompatExports.PosixPthreadSetspecific(context));

            context[CpuRegister.Rdi] = attrAddress;
            Assert.Equal(
                (int)OrbisGen2Result.ORBIS_GEN2_OK,
                KernelPthreadExtendedCompatExports.PthreadAttrInit(context));
            context[CpuRegister.Rsi] = 1;
            Assert.Equal(
                (int)OrbisGen2Result.ORBIS_GEN2_OK,
                KernelPthreadExtendedCompatExports.PthreadAttrSetdetachstate(context));

            KernelPthreadLifecycle.ResetRuntimeState();

            context[CpuRegister.Rdi] = unchecked((ulong)firstKey);
            Assert.Equal(
                (int)OrbisGen2Result.ORBIS_GEN2_OK,
                KernelPthreadExtendedCompatExports.PosixPthreadGetspecific(context));
            Assert.Equal(0UL, context[CpuRegister.Rax]);
            Assert.Equal(
                (int)OrbisGen2Result.ORBIS_GEN2_ERROR_NOT_FOUND,
                KernelPthreadExtendedCompatExports.PosixPthreadKeyDelete(context));

            context[CpuRegister.Rdi] = attrAddress;
            context[CpuRegister.Rsi] = detachStateAddress;
            Assert.Equal(
                (int)OrbisGen2Result.ORBIS_GEN2_OK,
                KernelPthreadExtendedCompatExports.PthreadAttrGetdetachstate(context));
            Assert.True(context.TryReadInt32(detachStateAddress, out var detachState));
            Assert.Equal(0, detachState);

            context[CpuRegister.Rdi] = secondKeyAddress;
            context[CpuRegister.Rsi] = 0;
            Assert.Equal(
                (int)OrbisGen2Result.ORBIS_GEN2_OK,
                KernelPthreadExtendedCompatExports.PosixPthreadKeyCreate(context));
            Assert.True(context.TryReadInt32(secondKeyAddress, out var secondKey));
            Assert.Equal(1, secondKey);
        }
        finally
        {
            KernelPthreadLifecycle.ResetRuntimeState();
        }
    }
}
