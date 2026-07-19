// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.VideoOut;
using Silk.NET.Vulkan;
using Xunit;

namespace SharpEmu.Libs.Tests.VideoOut;

public sealed class VulkanImageViewSwizzleTests
{
    [Fact]
    public void CanonicalGuestChannelSelectionUsesVulkanIdentitySwizzles()
    {
        var mapping = VulkanVideoPresenter.ToVkComponentMapping(0xFAC);

        Assert.Equal(ComponentSwizzle.Identity, mapping.R);
        Assert.Equal(ComponentSwizzle.Identity, mapping.G);
        Assert.Equal(ComponentSwizzle.Identity, mapping.B);
        Assert.Equal(ComponentSwizzle.Identity, mapping.A);
    }

    [Fact]
    public void NonCanonicalGuestChannelSelectionRemainsExplicitForSampling()
    {
        const uint SelectRedGreenZeroOne =
            4u |
            (5u << 3) |
            (0u << 6) |
            (1u << 9);

        var mapping =
            VulkanVideoPresenter.ToVkComponentMapping(SelectRedGreenZeroOne);

        Assert.Equal(ComponentSwizzle.R, mapping.R);
        Assert.Equal(ComponentSwizzle.G, mapping.G);
        Assert.Equal(ComponentSwizzle.Zero, mapping.B);
        Assert.Equal(ComponentSwizzle.One, mapping.A);
    }

    [Theory]
    [InlineData(0xFACu, 0xFACu, true)]
    [InlineData(0xFACu, 0xA0Cu, false)]
    [InlineData(0xA0Cu, 0xA0Cu, true)]
    [InlineData(0xA0Cu, 0xFACu, false)]
    public void BaseViewIsReusedOnlyForItsOwnChannelSelection(
        uint baseViewDstSelect,
        uint requestedDstSelect,
        bool expected)
    {
        var canReuse = VulkanVideoPresenter.CanReuseGuestImageBaseView(
            Format.R8G8B8A8Unorm,
            baseViewDstSelect,
            Format.R8G8B8A8Unorm,
            requestedDstSelect);

        Assert.Equal(expected, canReuse);
    }

    [Fact]
    public void BaseViewIsNotReusedForAFormatAlias()
    {
        var canReuse = VulkanVideoPresenter.CanReuseGuestImageBaseView(
            Format.R8G8B8A8Unorm,
            0xFAC,
            Format.R8G8B8A8Srgb,
            0xFAC);

        Assert.False(canReuse);
    }
}
