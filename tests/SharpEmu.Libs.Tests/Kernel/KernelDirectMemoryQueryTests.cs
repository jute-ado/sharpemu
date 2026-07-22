// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using SharpEmu.Libs.Kernel;
using Xunit;

namespace SharpEmu.Libs.Tests.Kernel;

public sealed class KernelDirectMemoryQueryTests
{
    private const ulong MemoryBase = 0x1_0000_0000;
    private const ulong OutputAddress = MemoryBase + 0x100;

    [Theory]
    [InlineData(-1L, 0UL, 24UL)]
    [InlineData(0L, 2UL, 24UL)]
    [InlineData(0L, 0UL, 23UL)]
    [InlineData(0L, 0UL, 25UL)]
    public void QueryRejectsInvalidOffsetFlagsAndOutputSize(long offset, ulong flags, ulong infoSize)
    {
        var memory = new FakeCpuMemory(MemoryBase, 0x1000);
        var output = new byte[24];
        Array.Fill(output, (byte)0xCC);
        Assert.True(memory.TryWrite(OutputAddress, output));
        var context = new CpuContext(memory, Generation.Gen5);
        context[CpuRegister.Rdi] = unchecked((ulong)offset);
        context[CpuRegister.Rsi] = flags;
        context[CpuRegister.Rdx] = OutputAddress;
        context[CpuRegister.Rcx] = infoSize;

        var result = KernelMemoryCompatExports.KernelDirectMemoryQuery(context);

        Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT, result);
        Assert.True(memory.TryRead(OutputAddress, output));
        Assert.All(output, value => Assert.Equal(0xCC, value));
    }
}
