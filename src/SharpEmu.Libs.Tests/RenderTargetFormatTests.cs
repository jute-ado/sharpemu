// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.Gpu.Metal;
using SharpEmu.Libs.VideoOut;
using SharpEmu.ShaderCompiler;
using Silk.NET.Vulkan;
using Xunit;

namespace SharpEmu.Libs.Tests;

public sealed class RenderTargetFormatTests
{
    [Theory]
    [InlineData(2u, 4u, Format.R16Uint, Gen5PixelOutputKind.Uint)]
    [InlineData(2u, 5u, Format.R16Sint, Gen5PixelOutputKind.Sint)]
    public void VulkanDecodesR16IntegerTargets(
        uint dataFormat,
        uint numberType,
        Format expectedFormat,
        Gen5PixelOutputKind expectedOutputKind)
    {
        Assert.True(VulkanVideoPresenter.TryDecodeRenderTargetFormat(
            dataFormat,
            numberType,
            out var result));
        Assert.Equal(expectedFormat, result.Format);
        Assert.Equal(expectedOutputKind, result.OutputKind);
    }

    [Theory]
    [InlineData(2u, 4u, (int)MtlPixelFormat.R16Uint, Gen5PixelOutputKind.Uint)]
    [InlineData(2u, 5u, (int)MtlPixelFormat.R16Sint, Gen5PixelOutputKind.Sint)]
    public void MetalDecodesR16IntegerTargets(
        uint dataFormat,
        uint numberType,
        int expectedFormat,
        Gen5PixelOutputKind expectedOutputKind)
    {
        Assert.True(MetalGuestFormats.TryDecodeRenderTargetFormat(
            dataFormat,
            numberType,
            out var result));
        Assert.Equal((MtlPixelFormat)expectedFormat, result.Format);
        Assert.Equal(expectedOutputKind, result.OutputKind);
    }

    [Theory]
    [InlineData(13u, 4u, Format.R32G32B32A32Uint, Gen5PixelOutputKind.Uint)]
    [InlineData(14u, 4u, Format.R32G32B32A32Uint, Gen5PixelOutputKind.Uint)]
    [InlineData(13u, 5u, Format.R32G32B32A32Sint, Gen5PixelOutputKind.Sint)]
    [InlineData(14u, 5u, Format.R32G32B32A32Sint, Gen5PixelOutputKind.Sint)]
    public void VulkanDecodesRgba32IntegerTargets(
        uint dataFormat,
        uint numberType,
        Format expectedFormat,
        Gen5PixelOutputKind expectedOutputKind)
    {
        Assert.True(VulkanVideoPresenter.TryDecodeRenderTargetFormat(
            dataFormat,
            numberType,
            out var result));
        Assert.Equal(expectedFormat, result.Format);
        Assert.Equal(expectedOutputKind, result.OutputKind);
    }

    [Theory]
    [InlineData(13u, 4u, (int)MtlPixelFormat.Rgba32Uint, Gen5PixelOutputKind.Uint)]
    [InlineData(14u, 4u, (int)MtlPixelFormat.Rgba32Uint, Gen5PixelOutputKind.Uint)]
    [InlineData(13u, 5u, (int)MtlPixelFormat.Rgba32Sint, Gen5PixelOutputKind.Sint)]
    [InlineData(14u, 5u, (int)MtlPixelFormat.Rgba32Sint, Gen5PixelOutputKind.Sint)]
    public void MetalDecodesRgba32IntegerTargets(
        uint dataFormat,
        uint numberType,
        int expectedFormat,
        Gen5PixelOutputKind expectedOutputKind)
    {
        Assert.True(MetalGuestFormats.TryDecodeRenderTargetFormat(
            dataFormat,
            numberType,
            out var result));
        Assert.Equal((MtlPixelFormat)expectedFormat, result.Format);
        Assert.Equal(expectedOutputKind, result.OutputKind);
    }
}
