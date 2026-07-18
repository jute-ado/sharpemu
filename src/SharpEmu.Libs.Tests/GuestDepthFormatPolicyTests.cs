// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.VideoOut;
using Silk.NET.Vulkan;
using Xunit;

namespace SharpEmu.Libs.Tests;

public sealed class GuestDepthFormatPolicyTests
{
    [Theory]
    [InlineData(1u, 2u, (int)Format.R16Unorm, (int)Format.D16Unorm)]
    [InlineData(3u, 4u, (int)Format.R32Sfloat, (int)Format.D32Sfloat)]
    public void SupportedDepthFormatsHaveOneNativePolicy(
        uint guestFormat,
        uint bytesPerElement,
        int textureFormat,
        int attachmentFormat)
    {
        Assert.True(GuestDepthFormatPolicy.TryResolve(
            guestFormat,
            out var policy));
        Assert.Equal(guestFormat, policy.GuestFormat);
        Assert.Equal(bytesPerElement, policy.BytesPerElement);
        Assert.Equal((Format)textureFormat, policy.TextureFormat);
        Assert.Equal((Format)attachmentFormat, policy.AttachmentFormat);
    }

    [Theory]
    [InlineData(0u)]
    [InlineData(2u)]
    [InlineData(4u)]
    [InlineData(uint.MaxValue)]
    public void UnknownDepthFormatsHaveNoFallbackPolicy(uint guestFormat)
    {
        Assert.False(GuestDepthFormatPolicy.TryResolve(
            guestFormat,
            out var policy));
        Assert.Equal(default, policy);
    }
}
