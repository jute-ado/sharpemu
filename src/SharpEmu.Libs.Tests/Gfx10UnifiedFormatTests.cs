// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.ShaderCompiler;
using Xunit;

namespace SharpEmu.Libs.Tests;

public sealed class Gfx10UnifiedFormatTests
{
    [Theory]
    [InlineData(0u, 0u, 0u)]
    [InlineData(13u, 2u, 7u)]
    [InlineData(29u, 5u, 7u)]
    [InlineData(36u, 6u, 7u)]
    [InlineData(43u, 7u, 7u)]
    [InlineData(44u, 8u, 0u)]
    [InlineData(60u, 10u, 4u)]
    [InlineData(61u, 10u, 5u)]
    [InlineData(77u, 14u, 7u)]
    [InlineData(128u, 1u, 9u)]
    [InlineData(132u, 34u, 7u)]
    [InlineData(182u, 182u, 9u)]
    public void DecodesDefinedFormats(
        uint unifiedFormat,
        uint expectedDataFormat,
        uint expectedNumberFormat)
    {
        Assert.True(
            Gfx10UnifiedFormat.TryDecode(
                unifiedFormat,
                out var dataFormat,
                out var numberFormat));
        Assert.Equal(expectedDataFormat, dataFormat);
        Assert.Equal(expectedNumberFormat, numberFormat);
    }

    [Theory]
    [InlineData(30u)]
    [InlineData(35u)]
    [InlineData(37u)]
    [InlineData(42u)]
    [InlineData(46u)]
    [InlineData(47u)]
    [InlineData(78u)]
    [InlineData(127u)]
    [InlineData(155u)]
    [InlineData(168u)]
    [InlineData(183u)]
    [InlineData(uint.MaxValue)]
    public void RejectsReservedFormats(uint unifiedFormat)
    {
        Assert.False(
            Gfx10UnifiedFormat.TryDecode(
                unifiedFormat,
                out var dataFormat,
                out var numberFormat));
        Assert.Equal(0u, dataFormat);
        Assert.Equal(0u, numberFormat);
    }
}
