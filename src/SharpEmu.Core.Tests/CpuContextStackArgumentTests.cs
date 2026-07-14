// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Core.Loader;
using SharpEmu.Core.Memory;
using SharpEmu.HLE;
using Xunit;

namespace SharpEmu.Core.Tests;

public sealed class CpuContextStackArgumentTests
{
    private const ulong StackAddress = 0x7000;

    [Fact]
    public void ReadsStackArgumentsAfterReturnAddress()
    {
        var memory = new VirtualMemory();
        memory.Map(
            StackAddress,
            0x1000,
            fileOffset: 0,
            fileData: ReadOnlySpan<byte>.Empty,
            ProgramHeaderFlags.Read | ProgramHeaderFlags.Write);
        var context = new CpuContext(memory, Generation.Gen5);
        context[CpuRegister.Rsp] = StackAddress + 0x100;

        Assert.True(context.TryWriteUInt64(context[CpuRegister.Rsp], 0xDEAD_BEEF));
        Assert.True(context.TryWriteUInt64(context[CpuRegister.Rsp] + 8, 0x1122_3344_5566_7788));
        Assert.True(context.TryWriteUInt64(context[CpuRegister.Rsp] + 16, 0xAABB_CCDD_EEFF_0011));

        Assert.True(context.TryReadStackArgumentUInt64(0, out var first));
        Assert.Equal(0x1122_3344_5566_7788UL, first);
        Assert.True(context.TryReadStackArgumentUInt64(1, out var second));
        Assert.Equal(0xAABB_CCDD_EEFF_0011UL, second);
        Assert.True(context.TryReadStackArgumentUInt32(1, out var secondLow));
        Assert.Equal(0xEEFF_0011U, secondLow);
    }

    [Fact]
    public void RejectsInvalidOrOverflowingStackArgumentAddresses()
    {
        var memory = new VirtualMemory();
        var context = new CpuContext(memory, Generation.Gen5);

        Assert.False(context.TryReadStackArgumentUInt64(-1, out var negative));
        Assert.Equal(0UL, negative);

        context[CpuRegister.Rsp] = ulong.MaxValue - 4;
        Assert.False(context.TryReadStackArgumentUInt64(0, out var overflowing));
        Assert.Equal(0UL, overflowing);
    }
}
