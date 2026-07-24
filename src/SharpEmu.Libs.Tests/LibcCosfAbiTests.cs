// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using SharpEmu.Libs.LibcMath;
using Xunit;

namespace SharpEmu.Libs.Tests;

public sealed class LibcCosfAbiTests
{
    [Theory]
    [InlineData(0.0F)]
    [InlineData(1.0471976F)]
    [InlineData(-3.1415927F)]
    public void Cosf_ReadsAndReturnsScalarFloatInXmm0(float input)
    {
        const ulong preservedLow = 0xA5A5A5A500000000UL;
        const ulong preservedHigh = 0x0123456789ABCDEFUL;
        var context = new CpuContext(new FakeGuestMemory(), Generation.Gen5);
        context[CpuRegister.Rdi] = BitConverter.SingleToUInt32Bits(input + 0.5F);
        context.SetXmmRegister(
            0,
            preservedLow | BitConverter.SingleToUInt32Bits(input),
            preservedHigh);

        Assert.Equal(
            (int)OrbisGen2Result.ORBIS_GEN2_OK,
            LibcMathExports.Cosf(context));

        context.GetXmmRegister(0, out var resultLow, out var resultHigh);
        Assert.Equal(
            BitConverter.SingleToUInt32Bits(MathF.Cos(input)),
            unchecked((uint)resultLow));
        Assert.Equal(preservedLow, resultLow & 0xFFFFFFFF00000000UL);
        Assert.Equal(preservedHigh, resultHigh);
    }

    [Fact]
    public void Cosf_ReturnsNanForInfiniteInput()
    {
        var context = new CpuContext(new FakeGuestMemory(), Generation.Gen4);
        context.SetXmmRegister(0, BitConverter.SingleToUInt32Bits(float.PositiveInfinity), 0);

        Assert.Equal(
            (int)OrbisGen2Result.ORBIS_GEN2_OK,
            LibcMathExports.Cosf(context));

        context.GetXmmRegister(0, out var resultLow, out _);
        Assert.True(float.IsNaN(BitConverter.UInt32BitsToSingle(unchecked((uint)resultLow))));
    }
}
