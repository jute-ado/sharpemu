// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.Agc;
using Xunit;

namespace SharpEmu.Libs.Tests;

public sealed class AgcColorMetadataOperationTests
{
    private const uint CbColorControl = 0x202;

    [Theory]
    [InlineData(2u)]
    [InlineData(5u)]
    [InlineData(6u)]
    public void RecognizesOnlyMetadataColorModes(uint mode)
    {
        var registers = new Dictionary<uint, uint>
        {
            [CbColorControl] = mode << 4,
        };

        Assert.True(
            AgcExports.TryGetCbMetadataColorMode(registers, out var decodedMode));
        Assert.Equal(mode, decodedMode);
    }

    [Theory]
    [InlineData(0u)]
    [InlineData(1u)]
    [InlineData(3u)]
    [InlineData(4u)]
    [InlineData(7u)]
    public void PreservesNonMetadataColorModes(uint mode)
    {
        var registers = new Dictionary<uint, uint>
        {
            [CbColorControl] = mode << 4,
        };

        Assert.False(
            AgcExports.TryGetCbMetadataColorMode(registers, out var decodedMode));
        Assert.Equal(mode, decodedMode);
    }

    [Fact]
    public void IgnoresUnrelatedColorControlBits()
    {
        var registers = new Dictionary<uint, uint>
        {
            [CbColorControl] = (5u << 4) | (0xCCu << 16) | 0xFu,
        };

        Assert.True(
            AgcExports.TryGetCbMetadataColorMode(registers, out var decodedMode));
        Assert.Equal(5u, decodedMode);
    }

    [Fact]
    public void MissingColorControlDoesNotSuppressDraw()
    {
        Assert.False(
            AgcExports.TryGetCbMetadataColorMode(
                new Dictionary<uint, uint>(),
                out var decodedMode));
        Assert.Equal(0u, decodedMode);
    }
}
