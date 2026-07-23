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

    [Theory]
    [InlineData(512ul, 512ul, 16u, 1u, false)]
    [InlineData(8192ul, 512ul, 16u, 1u, true)]
    [InlineData(2048ul, 512ul, 1u, 4u, true)]
    [InlineData(0ul, 0ul, 1u, 1u, false)]
    [InlineData(ulong.MaxValue, ulong.MaxValue, 2u, 1u, false)]
    public void TextureUploadMustCoverEveryDepthSliceAndLayer(
        ulong byteCount,
        ulong bytesPerSlice,
        uint depth,
        uint layers,
        bool expected) =>
        Assert.Equal(
            expected,
            VulkanVideoPresenter.IsCompleteTextureUpload(
                byteCount,
                bytesPerSlice,
                depth,
                layers));

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

    [Theory]
    [InlineData(false, 1u, false, true)]
    [InlineData(true, 1u, false, true)]
    [InlineData(true, 2u, false, false)]
    [InlineData(false, 1u, true, false)]
    public void GuestImageAliasRequiresSingleLayerNonCubeDescriptor(
        bool arrayedView,
        uint arrayLayers,
        bool cubeView,
        bool expected) =>
        Assert.Equal(
            expected,
            VulkanVideoPresenter.CanUseSingleLayerGuestImageAlias(
                arrayedView,
                arrayLayers,
                cubeView));

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
