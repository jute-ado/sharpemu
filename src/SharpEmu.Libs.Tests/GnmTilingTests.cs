// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.Agc;
using Xunit;

namespace SharpEmu.Libs.Tests;

public sealed class GnmTilingTests
{
    [Theory]
    [InlineData(1u, 1, 1, 1, 256ul)]
    [InlineData(5u, 1, 1, 4, 4096ul)]
    [InlineData(9u, 8, 8, 8, 65536ul)]
    [InlineData(9u, 257, 256, 1, 131072ul)]
    public void TryGetTiledByteCount_AllocatesWholeSwizzleBlocks(
        uint swizzleMode,
        int elementsWide,
        int elementsHigh,
        int bytesPerElement,
        ulong expected)
    {
        Assert.True(GnmTiling.TryGetTiledByteCount(
            swizzleMode,
            elementsWide,
            elementsHigh,
            bytesPerElement,
            out var actual));
        Assert.Equal(expected, actual);
    }

    [Fact]
    public void TryDetile_RejectsTruncatedSwizzleBlockWithoutChangingOutput()
    {
        var tiled = new byte[255];
        var linear = Enumerable.Repeat((byte)0xA5, 16 * 16).ToArray();
        var original = linear.ToArray();

        Assert.False(GnmTiling.TryDetile(tiled, linear, 1, 16, 16, 1));
        Assert.Equal(original, linear);
    }

    [Fact]
    public void TryDetile_Standard256ByteMode_ProducesRowMajorElements()
    {
        var tiled = Enumerable.Range(0, 256).Select(value => (byte)value).ToArray();
        var linear = new byte[16 * 16];

        Assert.True(GnmTiling.TryDetile(tiled, linear, 1, 16, 16, 1));

        Assert.Equal(new byte[] { 0, 1, 4, 5 }, linear.AsSpan(0, 4).ToArray());
        Assert.Equal(new byte[] { 2, 3, 6, 7 }, linear.AsSpan(16, 4).ToArray());
    }

    [Fact]
    public void TryDetile_ZOrder4KMode_UsesYAsTheLowMortonBit()
    {
        var tiled = new byte[4096];
        tiled[0] = 0x10;
        tiled[1] = 0x11;
        tiled[2] = 0x12;
        tiled[3] = 0x13;
        var linear = new byte[64 * 64];

        Assert.True(GnmTiling.TryDetile(tiled, linear, 4, 64, 64, 1));

        Assert.Equal(0x10, linear[0]);
        Assert.Equal(0x12, linear[1]);
        Assert.Equal(0x11, linear[64]);
        Assert.Equal(0x13, linear[65]);
    }

    [Theory]
    [InlineData(0u)]
    [InlineData(12u)]
    [InlineData(28u)]
    public void TryDetile_UnsupportedOrLinearModesLeaveOutputUntouched(uint swizzleMode)
    {
        var tiled = new byte[65536];
        var linear = Enumerable.Repeat((byte)0x5A, 16).ToArray();
        var original = linear.ToArray();

        Assert.False(GnmTiling.TryDetile(tiled, linear, swizzleMode, 4, 4, 1));
        Assert.Equal(original, linear);
    }
}
