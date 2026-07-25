// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.Agc;
using SharpEmu.Libs.VideoOut;
using Xunit;

namespace SharpEmu.Libs.Tests.VideoOut;

public sealed class VulkanDetilePassTests
{
    [Theory]
    [InlineData(1, false)]
    [InlineData(2, false)]
    [InlineData(4, true)]
    [InlineData(8, true)]
    [InlineData(16, true)]
    public void SupportsOnlyWholeWordElementWidths(
        int bytesPerElement,
        bool expected)
    {
        var parameters = GnmTiling.GetDetileParams(
            swizzleMode: 27,
            bytesPerElement,
            elementsWide: 257,
            elementsHigh: 129);

        Assert.Equal(expected, VulkanDetilePass.Supports(parameters));
    }

    [Fact]
    public void TryCreateDispatchBuildsExactXorArrayPlan()
    {
        const int width = 257;
        const int height = 129;
        const int bytesPerElement = 8;
        const uint layers = 2;
        var parameters = GnmTiling.GetDetileParams(
            swizzleMode: 27,
            bytesPerElement,
            width,
            height);
        var tiledBytesPerLayer = GetTiledBytesPerLayer(parameters);

        Assert.True(
            VulkanDetilePass.TryCreateDispatch(
                checked(tiledBytesPerLayer * (int)layers),
                texelWidth: width,
                texelHeight: height,
                layers,
                parameters,
                out var dispatch));

        Assert.Equal((uint)width, dispatch.ElementsWide);
        Assert.Equal((uint)height, dispatch.ElementsHigh);
        Assert.Equal(65u, dispatch.GroupCountX);
        Assert.Equal(17u, dispatch.GroupCountY);
        Assert.Equal(layers, dispatch.GroupCountZ);
        Assert.Equal(
            (uint)(tiledBytesPerLayer / bytesPerElement),
            dispatch.SourceSliceElements);
        Assert.Equal(
            (ulong)width * height * bytesPerElement * layers,
            dispatch.OutputBytes);
        Assert.Equal(0u, dispatch.Equation);
        Assert.Equal(2u, dispatch.UintsPerElement);
    }

    [Fact]
    public void TryCreateDispatchBuildsBlockTablePlan()
    {
        var parameters = GnmTiling.GetDetileParams(
            swizzleMode: 8,
            bytesPerElement: 16,
            elementsWide: 64,
            elementsHigh: 33);
        var tiledBytes = GetTiledBytesPerLayer(parameters);

        Assert.True(
            VulkanDetilePass.TryCreateDispatch(
                tiledBytes,
                texelWidth: 64,
                texelHeight: 33,
                layers: 1,
                parameters,
                out var dispatch));

        Assert.Equal(1u, dispatch.Equation);
        Assert.Equal(4u, dispatch.UintsPerElement);
        Assert.Equal(32u, dispatch.GroupCountX);
        Assert.Equal(5u, dispatch.GroupCountY);
        Assert.Equal(1u, dispatch.GroupCountZ);
    }

    [Fact]
    public void TryCreateDispatchRequiresExactWholeBlockSourceExtent()
    {
        var parameters = GnmTiling.GetDetileParams(
            swizzleMode: 27,
            bytesPerElement: 4,
            elementsWide: 257,
            elementsHigh: 129);
        var exactBytes = GetTiledBytesPerLayer(parameters);

        Assert.False(
            VulkanDetilePass.TryCreateDispatch(
                exactBytes - 1,
                257,
                129,
                1,
                parameters,
                out _));
        Assert.False(
            VulkanDetilePass.TryCreateDispatch(
                exactBytes + 1,
                257,
                129,
                1,
                parameters,
                out _));
    }

    [Fact]
    public void TryCreateDispatchRejectsMalformedAddressTables()
    {
        var exactXor = GnmTiling.GetDetileParams(
            swizzleMode: 27,
            bytesPerElement: 4,
            elementsWide: 64,
            elementsHigh: 64);
        var exactBytes = GetTiledBytesPerLayer(exactXor);
        var missingXTerm = exactXor with { XByteTerm = [] };

        Assert.False(
            VulkanDetilePass.TryCreateDispatch(
                exactBytes,
                64,
                64,
                1,
                missingXTerm,
                out _));

        var blockTable = GnmTiling.GetDetileParams(
            swizzleMode: 8,
            bytesPerElement: 4,
            elementsWide: 64,
            elementsHigh: 64);
        var blockBytes = GetTiledBytesPerLayer(blockTable);
        var outOfRangeTable = blockTable with
        {
            BlockTable =
            [
                .. blockTable.BlockTable[..^1],
                blockTable.BlockElements,
            ],
        };

        Assert.False(
            VulkanDetilePass.TryCreateDispatch(
                blockBytes,
                64,
                64,
                1,
                outOfRangeTable,
                out _));
    }

    private static int GetTiledBytesPerLayer(DetileParams parameters)
    {
        var blocksHigh =
            (parameters.ElementsHigh + parameters.BlockHeight - 1) /
            parameters.BlockHeight;
        return checked(parameters.BlocksPerRow * blocksHigh * parameters.BlockBytes);
    }
}
