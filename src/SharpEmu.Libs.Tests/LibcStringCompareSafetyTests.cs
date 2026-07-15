// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using SharpEmu.Libs.Kernel;
using Xunit;

namespace SharpEmu.Libs.Tests;

public sealed class LibcStringCompareSafetyTests
{
    private const ulong RightAddress = 0x1000;

    [Fact]
    public void StrcmpRejectsAddressWrapInsteadOfContinuingAtZero()
    {
        var memory = new FakeGuestMemory();
        memory.AddRegion(ulong.MaxValue, [(byte)'A']);
        memory.AddRegion(0, [0]);
        memory.AddRegion(RightAddress, [(byte)'A', 0]);
        var context = CreateContext(memory, ulong.MaxValue, RightAddress);

        Assert.Equal(
            (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT,
            KernelMemoryCompatExports.Strcmp(context));
    }

    [Fact]
    public void StrncmpAllowsFinalAddressSpaceByteWhenLimitIsOne()
    {
        var memory = new FakeGuestMemory();
        memory.AddRegion(ulong.MaxValue, [(byte)'A']);
        memory.AddRegion(RightAddress, [(byte)'A']);
        var context = CreateContext(memory, ulong.MaxValue, RightAddress, limit: 1);

        Assert.Equal(0, KernelMemoryCompatExports.Strncmp(context));
        Assert.Equal(0uL, context[CpuRegister.Rax]);
    }

    [Fact]
    public void StrncmpWithZeroLimitDoesNotDereferenceNullPointers()
    {
        var context = CreateContext(new FakeGuestMemory(), 0, 0, limit: 0);

        Assert.Equal(0, KernelMemoryCompatExports.Strncmp(context));
        Assert.Equal(0uL, context[CpuRegister.Rax]);
    }

    [Fact]
    public void StrcasecmpRejectsAddressWrapInsteadOfContinuingAtZero()
    {
        var memory = new FakeGuestMemory();
        memory.AddRegion(ulong.MaxValue, [(byte)'A']);
        memory.AddRegion(0, [0]);
        memory.AddRegion(RightAddress, [(byte)'a', 0]);
        var context = CreateContext(memory, ulong.MaxValue, RightAddress);

        Assert.Equal(
            (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT,
            KernelMemoryCompatExports.Strcasecmp(context));
        Assert.Equal(1uL, context[CpuRegister.Rax]);
    }

    [Fact]
    public void WcscmpRejectsAddressWrapInsteadOfContinuingAtZero()
    {
        var memory = new FakeGuestMemory();
        memory.AddRegion(ulong.MaxValue - 1, [(byte)'A', 0]);
        memory.AddRegion(0, [0, 0]);
        memory.AddRegion(RightAddress, [(byte)'A', 0, 0, 0]);
        var context = CreateContext(memory, ulong.MaxValue - 1, RightAddress);

        Assert.Equal(
            (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT,
            KernelMemoryCompatExports.Wcscmp(context));
    }

    [Fact]
    public void WcsncmpAllowsFinalWideElementWhenLimitIsOne()
    {
        var memory = new FakeGuestMemory();
        memory.AddRegion(ulong.MaxValue - 1, [(byte)'A', 0]);
        memory.AddRegion(RightAddress, [(byte)'A', 0]);
        var context = CreateContext(memory, ulong.MaxValue - 1, RightAddress, limit: 1);

        Assert.Equal(0, KernelMemoryCompatExports.Wcsncmp(context));
        Assert.Equal(0uL, context[CpuRegister.Rax]);
    }

    [Fact]
    public void WcsncmpWithZeroLimitDoesNotDereferenceNullPointers()
    {
        var context = CreateContext(new FakeGuestMemory(), 0, 0, limit: 0);

        Assert.Equal(0, KernelMemoryCompatExports.Wcsncmp(context));
        Assert.Equal(0uL, context[CpuRegister.Rax]);
    }

    private static CpuContext CreateContext(
        FakeGuestMemory memory,
        ulong left,
        ulong right,
        ulong limit = 0)
    {
        var context = new CpuContext(memory, Generation.Gen5);
        context[CpuRegister.Rdi] = left;
        context[CpuRegister.Rsi] = right;
        context[CpuRegister.Rdx] = limit;
        return context;
    }
}
