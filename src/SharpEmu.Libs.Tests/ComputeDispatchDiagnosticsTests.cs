// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.VideoOut;
using SharpEmu.Libs.Gpu.Vulkan;
using Xunit;

namespace SharpEmu.Libs.Tests;

public sealed class ComputeDispatchDiagnosticsTests
{
    [Fact]
    public void ComputeShaderSignatureIsStableAndContentSensitive()
    {
        var first = VulkanVideoPresenter.ComputeShaderSignature([1, 2, 3, 4]);
        var sameContent = VulkanVideoPresenter.ComputeShaderSignature([1, 2, 3, 4]);
        var differentContent = VulkanVideoPresenter.ComputeShaderSignature([1, 2, 3, 5]);

        Assert.Equal("9F64A747E1B97F13", first);
        Assert.Equal(first, sameContent);
        Assert.NotEqual(first, differentContent);
    }

    [Theory]
    [InlineData(32u, 8u, 6u, 1u, 32u)]
    [InlineData(64u, 8u, 6u, 1u, 32u)]
    [InlineData(64u, 8u, 8u, 1u, 64u)]
    [InlineData(64u, 16u, 8u, 1u, 64u)]
    public void VulkanTranslationUsesNativeSubgroupsForPartialWave64(
        uint guestWaveLaneCount,
        uint localSizeX,
        uint localSizeY,
        uint localSizeZ,
        uint expected)
    {
        Assert.Equal(
            expected,
            VulkanGuestGpuBackend.ResolveComputeTranslationWaveLaneCount(
                guestWaveLaneCount,
                localSizeX,
                localSizeY,
                localSizeZ));
    }

    [Theory]
    [InlineData("9f64a747e1b97f13", true)]
    [InlineData("DEADBEEF; 9F64A747E1B97F13, CAFEBABE", true)]
    [InlineData("DEADBEEF", false)]
    [InlineData("", false)]
    public void ComputeShaderSignatureListMatchesWholeCaseInsensitiveTokens(
        string configuredSignatures,
        bool expected)
    {
        Assert.Equal(
            expected,
            VulkanVideoPresenter.ComputeShaderSignatureListContains(
                configuredSignatures,
                [1, 2, 3, 4]));
    }
}
