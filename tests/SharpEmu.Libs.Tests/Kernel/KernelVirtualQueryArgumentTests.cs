// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using SharpEmu.Libs.Kernel;
using Xunit;

namespace SharpEmu.Libs.Tests.Kernel;

[Collection(KernelMemoryCompatStateCollection.Name)]
public sealed class KernelVirtualQueryArgumentTests
{
    private const ulong MemoryBase = 0x1_0000_0000;
    private const ulong OutputAddress = MemoryBase + 0x100;

    [Theory]
    [InlineData(2, 72)]
    [InlineData(0, 71)]
    [InlineData(0, 73)]
    public void VirtualQueryRejectsUnknownFlagsAndNonExactInfoSizes(
        int flags,
        ulong infoSize)
    {
        var memory = new FakeCpuMemory(MemoryBase, 0x1000);
        var output = new byte[72];
        Array.Fill(output, (byte)0xCC);
        Assert.True(memory.TryWrite(OutputAddress, output));
        var context = new CpuContext(memory, Generation.Gen5);
        context[CpuRegister.Rdi] = 0x2000;
        context[CpuRegister.Rsi] = unchecked((ulong)flags);
        context[CpuRegister.Rdx] = OutputAddress;
        context[CpuRegister.Rcx] = infoSize;

        var result = KernelMemoryCompatExports.KernelVirtualQuery(context);

        Assert.Equal(
            (int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT,
            result);
        Assert.True(memory.TryRead(OutputAddress, output));
        Assert.All(output, value => Assert.Equal(0xCC, value));
    }
}
