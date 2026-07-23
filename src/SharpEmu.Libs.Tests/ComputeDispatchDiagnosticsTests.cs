// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.VideoOut;
using SharpEmu.Libs.Gpu.Vulkan;
using SharpEmu.ShaderCompiler.Vulkan;
using Xunit;

namespace SharpEmu.Libs.Tests;

public sealed class ComputeDispatchDiagnosticsTests
{
    [Theory]
    [InlineData(true, true, true, true)]
    [InlineData(false, true, true, false)]
    [InlineData(true, false, true, true)]
    [InlineData(false, false, false, false)]
    public void PaddedWave64LanesStayInProgramForUniformBarriers(
        bool invocationInBounds,
        bool emulateWave64,
        bool expectedProgramActive,
        bool expectedExecActive)
    {
        var state = Gen5SpirvTranslator.ResolveComputeInvocationState(
            invocationInBounds,
            emulateWave64);

        Assert.Equal(expectedProgramActive, state.ProgramActive);
        Assert.Equal(expectedExecActive, state.ExecActive);
    }

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

    [Theory]
    [InlineData(null, null, true)]
    [InlineData("", "", true)]
    [InlineData("9f64a747e1b97f13", "9F64A747E1B97F13", true)]
    [InlineData("DEADBEEF; 9F64A747E1B97F13", "9f64a747e1b97f13", true)]
    [InlineData("*", "9F64A747E1B97F13", true)]
    [InlineData("DEADBEEF", "9F64A747E1B97F13", false)]
    [InlineData("9F64A747E1B97F13", null, false)]
    public void GuestImageTraceMatchesOptionalShaderSignatureFilter(
        string? configuredSignatures,
        string? shaderSignature,
        bool expected)
    {
        Assert.Equal(
            expected,
            VulkanVideoPresenter.GuestImageShaderSignatureFilterMatches(
                configuredSignatures,
                shaderSignature));
    }

    [Theory]
    [InlineData(false, false, null, null, false)]
    [InlineData(true, false, null, null, true)]
    [InlineData(false, true, null, null, true)]
    [InlineData(false, false, "9F64A747E1B97F13", "9f64a747e1b97f13", true)]
    [InlineData(false, false, "DEADBEEF", "9F64A747E1B97F13", false)]
    public void GuestImageTraceTreatsMatchingShaderSignatureAsExplicitSelector(
        bool addressMatched,
        bool shaderAddressMatched,
        string? configuredSignatures,
        string? shaderSignature,
        bool expected)
    {
        Assert.Equal(
            expected,
            VulkanVideoPresenter.GuestImageTraceSelectorMatches(
                addressMatched,
                shaderAddressMatched,
                configuredSignatures,
                shaderSignature));
    }

    [Theory]
    [InlineData(true, false, false, false, true)]
    [InlineData(false, true, false, false, true)]
    [InlineData(false, false, true, false, true)]
    [InlineData(false, false, false, true, true)]
    [InlineData(false, false, false, false, false)]
    public void GuestImageTraceIncludesSampledImagesForSignatureDiagnostics(
        bool isStorage,
        bool addressFilterEnabled,
        bool shaderAddressFilterEnabled,
        bool signatureFilterEnabled,
        bool expected)
    {
        Assert.Equal(
            expected,
            VulkanVideoPresenter.ShouldCollectGuestImageTraceCandidate(
                isStorage,
                addressFilterEnabled,
                shaderAddressFilterEnabled,
                signatureFilterEnabled));
    }

    [Theory]
    [InlineData(true, false, false, false, false, true)]
    [InlineData(false, true, false, false, false, true)]
    [InlineData(false, false, true, false, false, true)]
    [InlineData(false, false, false, true, false, true)]
    [InlineData(false, false, false, false, true, true)]
    [InlineData(false, false, false, false, false, false)]
    public void GuestImageTraceSelectionRunsForEverySupportedSelector(
        bool broadTraceEnabled,
        bool addressFilterEnabled,
        bool shaderAddressFilterEnabled,
        bool signatureFilterEnabled,
        bool intervalEnabled,
        bool expected)
    {
        Assert.Equal(
            expected,
            VulkanVideoPresenter.ShouldRunGuestImageTraceSelection(
                broadTraceEnabled,
                addressFilterEnabled,
                shaderAddressFilterEnabled,
                signatureFilterEnabled,
                intervalEnabled));
    }
}
