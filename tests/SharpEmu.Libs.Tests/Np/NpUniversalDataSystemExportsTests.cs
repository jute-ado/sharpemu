// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using SharpEmu.Libs.Np;
using Xunit;

namespace SharpEmu.Libs.Tests.Np;

public sealed class NpUniversalDataSystemExportsTests
{
    private const int InvalidArgument = unchecked((int)0x80553102);
    private const ulong MemoryBase = 0x1_0000_0000;
    private const ulong OutputAddress = MemoryBase + 0x100;

    [Fact]
    public void CreateHandleWritesToOutputArgument()
    {
        var memory = new FakeCpuMemory(MemoryBase, 0x1000);
        var context = new CpuContext(memory, Generation.Gen5);
        context[CpuRegister.Rdi] = OutputAddress;

        var result = NpUniversalDataSystemExports.NpUniversalDataSystemCreateHandle(context);

        Assert.Equal(0, result);
        Assert.True(context.TryReadInt32(OutputAddress, out var handle));
        Assert.True(handle > 0);
    }

    [Fact]
    public void CreateHandleRejectsNullOutputArgument()
    {
        var memory = new FakeCpuMemory(MemoryBase, 0x1000);
        var context = new CpuContext(memory, Generation.Gen5);
        Assert.True(context.TryWriteInt32(OutputAddress, unchecked((int)0xCCCCCCCC)));
        context[CpuRegister.Rdi] = 0;
        context[CpuRegister.Rsi] = OutputAddress;

        var result = NpUniversalDataSystemExports.NpUniversalDataSystemCreateHandle(context);

        Assert.Equal(InvalidArgument, result);
        Assert.True(context.TryReadInt32(OutputAddress, out var value));
        Assert.Equal(unchecked((int)0xCCCCCCCC), value);
    }

    [Fact]
    public void CreatedHandleCanBeDestroyedOnlyOnce()
    {
        var memory = new FakeCpuMemory(MemoryBase, 0x1000);
        var context = new CpuContext(memory, Generation.Gen5);
        context[CpuRegister.Rdi] = OutputAddress;
        Assert.Equal(
            0,
            NpUniversalDataSystemExports.NpUniversalDataSystemCreateHandle(context));
        Assert.True(context.TryReadInt32(OutputAddress, out var handle));

        context[CpuRegister.Rdi] = unchecked((uint)handle);
        Assert.Equal(
            0,
            NpUniversalDataSystemExports.NpUniversalDataSystemDestroyHandle(context));
        Assert.Equal(
            InvalidArgument,
            NpUniversalDataSystemExports.NpUniversalDataSystemDestroyHandle(context));
    }
}
