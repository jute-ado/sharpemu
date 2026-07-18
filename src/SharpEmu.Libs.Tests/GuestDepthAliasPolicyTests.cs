// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.Gpu;
using SharpEmu.Libs.VideoOut;
using Silk.NET.Vulkan;
using Xunit;

namespace SharpEmu.Libs.Tests;

public sealed class GuestDepthAliasPolicyTests
{
    private static readonly GuestDepthAliasSurface Depth = new(
        Address: 0x1000,
        Width: 1920,
        Height: 1080,
        GuestFormat: 3);

    [Fact]
    public void ExactD32SampleReusesCanonicalDepthImage()
    {
        Assert.Equal(
            GuestDepthAliasKind.ExactSample,
            GuestDepthAliasPolicy.Classify(
                Depth,
                Texture(),
                Format.R32Sfloat,
                depthIsAttached: false));
    }

    [Fact]
    public void ExactD16SampleReusesCanonicalDepthImage()
    {
        Assert.Equal(
            GuestDepthAliasKind.ExactSample,
            GuestDepthAliasPolicy.Classify(
                Depth with { GuestFormat = 1 },
                Texture() with { Format = 2, NumberType = 0 },
                Format.R16Unorm,
                depthIsAttached: false));
    }

    [Fact]
    public void SameDrawDepthFeedbackIsExplicitlyUnsupported()
    {
        Assert.Equal(
            GuestDepthAliasKind.AttachmentFeedback,
            GuestDepthAliasPolicy.Classify(
                Depth,
                Texture(),
                Format.R32Sfloat,
                depthIsAttached: true));
    }

    [Theory]
    [InlineData(1u, 1920u, 1080u, false, 0u, 1u, (int)Format.R32Sfloat)]
    [InlineData(3u, 1280u, 1080u, false, 0u, 1u, (int)Format.R32Sfloat)]
    [InlineData(3u, 1920u, 1080u, true, 0u, 1u, (int)Format.R32Sfloat)]
    [InlineData(3u, 1920u, 1080u, false, 1u, 1u, (int)Format.R32Sfloat)]
    [InlineData(3u, 1920u, 1080u, false, 0u, 2u, (int)Format.R32Sfloat)]
    [InlineData(3u, 1920u, 1080u, false, 0u, 1u, (int)Format.R32Uint)]
    public void OverlappingButUnprovenLayoutsAreNotAliased(
        uint guestFormat,
        uint width,
        uint height,
        bool storage,
        uint mipLevel,
        uint mipLevels,
        int textureFormat)
    {
        Assert.Equal(
            GuestDepthAliasKind.Incompatible,
            GuestDepthAliasPolicy.Classify(
                Depth with { GuestFormat = guestFormat },
                Texture() with
                {
                    Width = width,
                    Height = height,
                    IsStorage = storage,
                    MipLevel = mipLevel,
                    MipLevels = mipLevels,
                },
                (Format)textureFormat,
                depthIsAttached: false));
    }

    [Fact]
    public void DefaultZeroComponentMappingUsesTheCanonicalDepthView()
    {
        Assert.Equal(
            GuestDepthAliasKind.ExactSample,
            GuestDepthAliasPolicy.Classify(
                Depth,
                Texture() with { DstSelect = 0 },
                Format.R32Sfloat,
                depthIsAttached: false));
    }

    [Fact]
    public void ComponentRemappingRequiresAProvenDepthView()
    {
        Assert.Equal(
            GuestDepthAliasKind.Incompatible,
            GuestDepthAliasPolicy.Classify(
                Depth,
                Texture() with { DstSelect = 0x111 },
                Format.R32Sfloat,
                depthIsAttached: false));
    }

    [Fact]
    public void DisjointImagesDoNotEnterDepthAliasHandling()
    {
        Assert.Equal(
            GuestDepthAliasKind.None,
            GuestDepthAliasPolicy.Classify(
                Depth,
                Texture() with { Address = 0x2000 },
                Format.R32Sfloat,
                depthIsAttached: false));
    }

    private static GuestDrawTexture Texture() => new(
        Address: Depth.Address,
        Width: Depth.Width,
        Height: Depth.Height,
        Format: 4,
        NumberType: 7,
        RgbaPixels: [],
        IsFallback: false,
        IsStorage: false);
}
