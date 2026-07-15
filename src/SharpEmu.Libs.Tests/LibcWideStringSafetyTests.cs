// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using SharpEmu.Libs.Kernel;
using Xunit;

namespace SharpEmu.Libs.Tests;

public sealed class LibcWideStringSafetyTests
{
    private const ulong DestinationAddress = 0x1000;
    private const byte Canary = 0xA5;

    [Fact]
    public void WcslenRejectsAddressWrapInsteadOfContinuingAtZero()
    {
        var memory = CreateWrappingWideStringMemory();
        var context = new CpuContext(memory, Generation.Gen5);
        context[CpuRegister.Rdi] = ulong.MaxValue - 1;

        Assert.Equal(
            (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT,
            KernelMemoryCompatExports.Wcslen(context));
    }

    [Fact]
    public void WcscpyRejectsWrappedSourceWithoutChangingDestination()
    {
        var memory = CreateWrappingWideStringMemory();
        var destination = Enumerable.Repeat(Canary, 4).ToArray();
        memory.AddRegion(DestinationAddress, destination);
        var context = new CpuContext(memory, Generation.Gen5);
        context[CpuRegister.Rdi] = DestinationAddress;
        context[CpuRegister.Rsi] = ulong.MaxValue - 1;

        Assert.Equal(
            (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT,
            KernelMemoryCompatExports.WcscpyFm5(context));
        Assert.All(destination, value => Assert.Equal(Canary, value));
    }

    [Fact]
    public void WcscpySRejectsWrappedSourceAndZerosDestination()
    {
        var memory = CreateWrappingWideStringMemory();
        var destination = Enumerable.Repeat(Canary, 4).ToArray();
        memory.AddRegion(DestinationAddress, destination);
        var context = new CpuContext(memory, Generation.Gen5);
        context[CpuRegister.Rdi] = DestinationAddress;
        context[CpuRegister.Rsi] = 2;
        context[CpuRegister.Rdx] = ulong.MaxValue - 1;

        Assert.Equal(
            (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT,
            KernelMemoryCompatExports.WcscpyS(context));
        Assert.Equal([0, 0, Canary, Canary], destination);
    }

    [Fact]
    public void WcschrRejectsAddressWrapInsteadOfSearchingAtZero()
    {
        var memory = new FakeGuestMemory();
        memory.AddRegion(ulong.MaxValue - 1, [(byte)'A', 0]);
        memory.AddRegion(0, [(byte)'B', 0]);
        var context = new CpuContext(memory, Generation.Gen5);
        context[CpuRegister.Rdi] = ulong.MaxValue - 1;
        context[CpuRegister.Rsi] = 'B';

        Assert.Equal(
            (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT,
            KernelMemoryCompatExports.Wcschr(context));
    }

    [Fact]
    public void WcschrCanMatchFinalWideElementInAddressSpace()
    {
        var memory = new FakeGuestMemory();
        memory.AddRegion(ulong.MaxValue - 1, [(byte)'A', 0]);
        var context = new CpuContext(memory, Generation.Gen5);
        context[CpuRegister.Rdi] = ulong.MaxValue - 1;
        context[CpuRegister.Rsi] = 'A';

        Assert.Equal(0, KernelMemoryCompatExports.Wcschr(context));
        Assert.Equal(ulong.MaxValue - 1, context[CpuRegister.Rax]);
    }

    private static FakeGuestMemory CreateWrappingWideStringMemory()
    {
        var memory = new FakeGuestMemory();
        memory.AddRegion(ulong.MaxValue - 1, [(byte)'A', 0]);
        memory.AddRegion(0, [0, 0]);
        return memory;
    }
}
