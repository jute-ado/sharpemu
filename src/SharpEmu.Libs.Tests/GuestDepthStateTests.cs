// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.Agc;
using Xunit;

namespace SharpEmu.Libs.Tests;

public sealed class GuestDepthStateTests
{
    [Theory]
    [InlineData(0x41u, true)]
    [InlineData(0x40u, false)]
    public void DecodeDepthStateReadsControlAndClearBits(
        uint renderControl,
        bool clearEnable)
    {
        var registers = new Dictionary<uint, uint>
        {
            [0x000] = renderControl,
            [0x200] = 0x76,
        };

        var state = AgcExports.DecodeDepthState(registers);

        Assert.True(state.TestEnable);
        Assert.True(state.WriteEnable);
        Assert.Equal(7u, state.CompareOp);
        Assert.Equal(clearEnable, state.ClearEnable);
    }

    [Fact]
    public void DecodeDepthTargetPreservesAddressesExtentAndClearValue()
    {
        var registers = new Dictionary<uint, uint>
        {
            [0x000] = 1,
            [0x007] = 1919u | (1079u << 16),
            [0x00B] = BitConverter.SingleToUInt32Bits(0.25f),
            [0x010] = 1u | (4u << 4),
            [0x012] = 0x1234,
            [0x014] = 0x5678,
            [0x01A] = 2,
            [0x01C] = 3,
            [0x200] = 0x76,
        };

        var target = Assert.IsType<SharpEmu.Libs.Gpu.GuestDepthTarget>(
            AgcExports.DecodeDepthTarget(registers));

        Assert.Equal(1920u, target.Width);
        Assert.Equal(1080u, target.Height);
        Assert.Equal((2UL << 40) | (0x1234UL << 8), target.ReadAddress);
        Assert.Equal((3UL << 40) | (0x5678UL << 8), target.WriteAddress);
        Assert.Equal(4u, target.SwizzleMode);
        Assert.Equal(0.25f, target.ClearDepth);
        Assert.False(target.ReadOnly);
    }
}
