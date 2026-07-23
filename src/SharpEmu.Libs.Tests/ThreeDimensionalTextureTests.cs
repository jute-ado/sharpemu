// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.Gpu;
using SharpEmu.Libs.VideoOut;
using SharpEmu.ShaderCompiler.Vulkan;
using Silk.NET.Vulkan;
using Xunit;

namespace SharpEmu.Libs.Tests;

public sealed class ThreeDimensionalTextureTests
{
    [Theory]
    [InlineData(0u, SpirvImageDim.Dim1D)]
    [InlineData(1u, SpirvImageDim.Dim2D)]
    [InlineData(2u, SpirvImageDim.Dim3D)]
    [InlineData(3u, SpirvImageDim.Cube)]
    [InlineData(4u, SpirvImageDim.Dim1D)]
    [InlineData(5u, SpirvImageDim.Dim2D)]
    [InlineData(6u, SpirvImageDim.Dim2D)]
    [InlineData(7u, SpirvImageDim.Dim2D)]
    public void ShaderImageDimensionMapsToSpirvDimension(
        uint guestDimension,
        SpirvImageDim expected)
    {
        Assert.Equal(
            expected,
            Gen5SpirvTranslator.GetImageDimension(guestDimension));
    }

    [Theory]
    [InlineData(1u, false, 2u)]
    [InlineData(2u, false, 3u)]
    [InlineData(5u, true, 3u)]
    public void ShaderCoordinatesPreserveEverySpatialComponent(
        uint guestDimension,
        bool arrayed,
        uint expectedComponents)
    {
        Assert.Equal(
            expectedComponents,
            Gen5SpirvTranslator.GetImageCoordinateComponentCount(
                guestDimension,
                arrayed));
    }

    [Fact]
    public void VolumeTextureUsesDepthInsteadOfArrayLayers()
    {
        var texture = Texture() with
        {
            Depth = 4,
            ThreeDimensionalView = true,
        };

        var shape = VulkanVideoPresenter.ResolveTextureImageShape(texture);

        Assert.True(shape.IsThreeDimensional);
        Assert.Equal(4u, shape.Depth);
        Assert.Equal(1u, shape.ArrayLayers);
    }

    [Fact]
    public void ArrayTextureKeepsLayersAndUnitDepth()
    {
        var texture = Texture() with
        {
            ArrayedView = true,
            ArrayLayers = 4,
        };

        var shape = VulkanVideoPresenter.ResolveTextureImageShape(texture);

        Assert.False(shape.IsThreeDimensional);
        Assert.Equal(1u, shape.Depth);
        Assert.Equal(4u, shape.ArrayLayers);
    }

    [Fact]
    public void CubeTextureUsesSixArrayLayersAndCubeShape()
    {
        var texture = Texture() with { CubeView = true };

        var shape = VulkanVideoPresenter.ResolveTextureImageShape(texture);

        Assert.True(shape.IsCube);
        Assert.False(shape.IsThreeDimensional);
        Assert.Equal(1u, shape.Depth);
        Assert.Equal(6u, shape.ArrayLayers);
        Assert.Equal(
            ImageCreateFlags.CreateCubeCompatibleBit,
            VulkanVideoPresenter.ResolveTextureImageCreateFlags(
                supportsAttachmentUsage: false,
                shape));
        Assert.Equal(
            ImageViewType.TypeCube,
            VulkanVideoPresenter.ResolveTextureImageViewType(
                shape,
                arrayedView: false));
    }

    private static GuestDrawTexture Texture() => new(
        Address: 0x1000,
        Width: 4,
        Height: 4,
        Format: 10,
        NumberType: 9,
        RgbaPixels: new byte[4 * 4 * 4],
        IsFallback: false,
        IsStorage: false);
}
