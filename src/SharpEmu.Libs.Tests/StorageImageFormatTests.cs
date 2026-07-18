// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.ShaderCompiler.Vulkan;
using Xunit;

namespace SharpEmu.Libs.Tests;

public sealed class StorageImageFormatTests
{
    [Theory]
    [InlineData(1u, 0u, SpirvImageFormat.R8)]
    [InlineData(1u, 1u, SpirvImageFormat.R8Snorm)]
    [InlineData(1u, 4u, SpirvImageFormat.R8ui)]
    [InlineData(4u, 7u, SpirvImageFormat.R32f)]
    [InlineData(10u, 5u, SpirvImageFormat.Rgba8i)]
    [InlineData(12u, 7u, SpirvImageFormat.Rgba16f)]
    [InlineData(20u, 0u, SpirvImageFormat.R32ui)]
    public void ExactGuestNumberTypeSelectsStorageFormat(
        uint dataFormat,
        uint numberType,
        SpirvImageFormat expected)
    {
        Assert.Equal(
            expected,
            Gen5SpirvTranslator.DecodeStorageImageFormat(
                dataFormat,
                numberType));
    }

    [Fact]
    public void UnsupportedGuestCombinationRemainsUnknown()
    {
        Assert.Equal(
            SpirvImageFormat.Unknown,
            Gen5SpirvTranslator.DecodeStorageImageFormat(
                dataFormat: 4,
                numberType: 0));
    }
}
