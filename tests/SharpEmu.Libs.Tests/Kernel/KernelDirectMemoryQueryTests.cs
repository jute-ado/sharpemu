// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using SharpEmu.Libs.Kernel;
using Xunit;

namespace SharpEmu.Libs.Tests.Kernel;

[Collection(KernelMemoryCompatStateCollection.Name)]
public sealed class KernelDirectMemoryQueryTests
{
    private const ulong MemoryBase = 0x1_0000_0000;
    private const ulong OutputAddress = MemoryBase + 0x100;
    private const ulong DirectMemorySize = 16UL * 1024 * 1024 * 1024;

    [Fact]
    public void FindNextAfterLastAllocationReturnsTerminalRange()
    {
        var context = new CpuContext(new FakeCpuMemory(MemoryBase, 0x1000), Generation.Gen5);
        context[CpuRegister.Rdi] = DirectMemorySize - 1;
        context[CpuRegister.Rsi] = 1;
        context[CpuRegister.Rdx] = OutputAddress;
        context[CpuRegister.Rcx] = 24;

        var result = KernelMemoryCompatExports.KernelDirectMemoryQuery(context);

        Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_OK, result);
        Assert.True(context.TryReadUInt64(OutputAddress, out var start));
        Assert.True(context.TryReadUInt64(OutputAddress + sizeof(ulong), out var end));
        Assert.True(context.TryReadInt32(OutputAddress + (2 * sizeof(ulong)), out var memoryType));
        Assert.Equal(DirectMemorySize, start);
        Assert.Equal(DirectMemorySize, end);
        Assert.Equal(0, memoryType);
    }
}
