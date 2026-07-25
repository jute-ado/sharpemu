// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.Agc;
using SharpEmu.Libs.Gpu.Metal;
using SharpEmu.Libs.Gpu.Vulkan;
using Xunit;

namespace SharpEmu.Libs.Tests.Agc;

public sealed class AgcGpuDetilePolicyTests
{
    [Theory]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("0", false)]
    [InlineData("true", false)]
    [InlineData("01", false)]
    [InlineData("1", true)]
    public void IsEnabledRequiresExactOptIn(string? value, bool expected)
    {
        Assert.Equal(expected, AgcGpuDetilePolicy.IsEnabled(value));
    }

    [Fact]
    public void OnlyVulkanAdvertisesTiledTextureUploads()
    {
        Assert.True(new VulkanGuestGpuBackend().SupportsTiledTextureUploads);
        Assert.False(new MetalGuestGpuBackend().SupportsTiledTextureUploads);
    }

    [Theory]
    [InlineData(4, 65536)]
    [InlineData(8, 131072)]
    [InlineData(16, 262144)]
    public void TryCreateSingleLayerParametersAcceptsSupportedElementSizes(
        int bytesPerElement,
        int tiledByteCount)
    {
        var accepted = AgcGpuDetilePolicy.TryCreateSingleLayerParameters(
            enabled: true,
            backendSupportsTiledUploads: true,
            hasElementLayout: true,
            baseMipInTail: false,
            isStorage: false,
            isArrayed: false,
            isThreeDimensional: false,
            isCube: false,
            tileMode: 27,
            bytesPerElement: bytesPerElement,
            elementsWide: 128,
            elementsHigh: 128,
            tiledByteCount: tiledByteCount,
            out var parameters);

        Assert.True(accepted);
        Assert.NotEqual(DetileEquation.None, parameters.Equation);
        Assert.Equal(bytesPerElement, parameters.BytesPerElement);
        Assert.Equal(128, parameters.ElementsWide);
        Assert.Equal(128, parameters.ElementsHigh);
    }

    [Fact]
    public void TryCreateSingleLayerParametersAcceptsBlockTableEquation()
    {
        Assert.True(GnmTiling.TryGetTiledByteCount(
            swizzleMode: 8,
            elementsWide: 128,
            elementsHigh: 128,
            bytesPerElement: 4,
            out var tiledByteCount));

        var accepted = AgcGpuDetilePolicy.TryCreateSingleLayerParameters(
            enabled: true,
            backendSupportsTiledUploads: true,
            hasElementLayout: true,
            baseMipInTail: false,
            isStorage: false,
            isArrayed: false,
            isThreeDimensional: false,
            isCube: false,
            tileMode: 8,
            bytesPerElement: 4,
            elementsWide: 128,
            elementsHigh: 128,
            tiledByteCount: checked((int)tiledByteCount),
            out var parameters);

        Assert.True(accepted);
        Assert.Equal(DetileEquation.BlockTable, parameters.Equation);
    }

    [Fact]
    public void TryCreateSingleLayerParametersRejectsLinearTexture()
    {
        var accepted = AgcGpuDetilePolicy.TryCreateSingleLayerParameters(
            enabled: true,
            backendSupportsTiledUploads: true,
            hasElementLayout: true,
            baseMipInTail: false,
            isStorage: false,
            isArrayed: false,
            isThreeDimensional: false,
            isCube: false,
            tileMode: 0,
            bytesPerElement: 4,
            elementsWide: 128,
            elementsHigh: 128,
            tiledByteCount: 65536,
            out var parameters);

        Assert.False(accepted);
        Assert.Equal(default, parameters);
    }

    [Theory]
    [InlineData(false, true, true, false, false, false, false, false, 4, 65536)]
    [InlineData(true, false, true, false, false, false, false, false, 4, 65536)]
    [InlineData(true, true, false, false, false, false, false, false, 4, 65536)]
    [InlineData(true, true, true, true, false, false, false, false, 4, 65536)]
    [InlineData(true, true, true, false, true, false, false, false, 4, 65536)]
    [InlineData(true, true, true, false, false, true, false, false, 4, 65536)]
    [InlineData(true, true, true, false, false, false, true, false, 4, 65536)]
    [InlineData(true, true, true, false, false, false, false, true, 4, 65536)]
    [InlineData(true, true, true, false, false, false, false, false, 1, 65536)]
    [InlineData(true, true, true, false, false, false, false, false, 2, 65536)]
    [InlineData(true, true, true, false, false, false, false, false, 32, 65536)]
    [InlineData(true, true, true, false, false, false, false, false, 4, 65535)]
    public void TryCreateSingleLayerParametersRejectsUnsafeCandidate(
        bool enabled,
        bool backendSupportsTiledUploads,
        bool hasElementLayout,
        bool baseMipInTail,
        bool isStorage,
        bool isArrayed,
        bool isThreeDimensional,
        bool isCube,
        int bytesPerElement,
        int tiledByteCount)
    {
        var accepted = AgcGpuDetilePolicy.TryCreateSingleLayerParameters(
            enabled,
            backendSupportsTiledUploads,
            hasElementLayout,
            baseMipInTail,
            isStorage,
            isArrayed,
            isThreeDimensional,
            isCube,
            tileMode: 27,
            bytesPerElement: bytesPerElement,
            elementsWide: 128,
            elementsHigh: 128,
            tiledByteCount: tiledByteCount,
            out var parameters);

        Assert.False(accepted);
        Assert.Equal(default, parameters);
    }

    [Fact]
    public void TryCreateArrayLayerParametersAcceptsCompleteLayerSet()
    {
        var accepted = AgcGpuDetilePolicy.TryCreateArrayLayerParameters(
            enabled: true,
            backendSupportsTiledUploads: true,
            hasElementLayout: true,
            baseMipInTail: false,
            isStorage: false,
            isArrayed: true,
            isThreeDimensional: false,
            isCube: false,
            layers: 3,
            tileMode: 27,
            bytesPerElement: 4,
            elementsWide: 128,
            elementsHigh: 128,
            tiledBytesPerLayer: 65536,
            tiledSourceByteCount: 196608,
            out var parameters);

        Assert.True(accepted);
        Assert.Equal(DetileEquation.ExactXor, parameters.Equation);
    }

    [Theory]
    [InlineData(false, true, true, false, false, true, false, false, 3, 196608)]
    [InlineData(true, false, true, false, false, true, false, false, 3, 196608)]
    [InlineData(true, true, false, false, false, true, false, false, 3, 196608)]
    [InlineData(true, true, true, true, false, true, false, false, 3, 196608)]
    [InlineData(true, true, true, false, true, true, false, false, 3, 196608)]
    [InlineData(true, true, true, false, false, false, false, false, 3, 196608)]
    [InlineData(true, true, true, false, false, true, true, false, 3, 196608)]
    [InlineData(true, true, true, false, false, true, false, true, 3, 196608)]
    [InlineData(true, true, true, false, false, true, false, false, 1, 65536)]
    [InlineData(true, true, true, false, false, true, false, false, 3, 196607)]
    public void TryCreateArrayLayerParametersRejectsIncompleteOrUnsafeSet(
        bool enabled,
        bool backendSupportsTiledUploads,
        bool hasElementLayout,
        bool baseMipInTail,
        bool isStorage,
        bool isArrayed,
        bool isThreeDimensional,
        bool isCube,
        uint layers,
        int tiledSourceByteCount)
    {
        var accepted = AgcGpuDetilePolicy.TryCreateArrayLayerParameters(
            enabled,
            backendSupportsTiledUploads,
            hasElementLayout,
            baseMipInTail,
            isStorage,
            isArrayed,
            isThreeDimensional,
            isCube,
            layers,
            tileMode: 27,
            bytesPerElement: 4,
            elementsWide: 128,
            elementsHigh: 128,
            tiledBytesPerLayer: 65536,
            tiledSourceByteCount,
            out var parameters);

        Assert.False(accepted);
        Assert.Equal(default, parameters);
    }
}
