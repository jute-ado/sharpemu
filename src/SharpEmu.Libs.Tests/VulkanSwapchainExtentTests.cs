// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.VideoOut;
using Xunit;

namespace SharpEmu.Libs.Tests;

public sealed class VulkanSwapchainExtentTests
{
    [Theory]
    [InlineData(0u, 1280u, 1u, 0u, 1280u)]
    [InlineData(1u, 720u, 1u, 0u, 720u)]
    [InlineData(0u, 1280u, 64u, 1920u, 1280u)]
    [InlineData(2560u, 1280u, 64u, 1920u, 1920u)]
    public void ResolveSwapchainExtentComponentUsesUsableFallback(
        uint value,
        uint fallback,
        uint minimum,
        uint maximum,
        uint expected)
    {
        Assert.Equal(
            expected,
            VulkanVideoPresenter.ResolveSwapchainExtentComponent(
                value,
                fallback,
                minimum,
                maximum));
    }

    [Theory]
    [InlineData(0u, 1080u, true)]
    [InlineData(1920u, 0u, true)]
    [InlineData(1u, 1080u, true)]
    [InlineData(1920u, 1u, true)]
    [InlineData(2u, 2u, false)]
    public void SwapchainRecreationUsesFallbackForUnusableSurfaceExtent(
        uint width,
        uint height,
        bool expected)
    {
        Assert.Equal(
            expected,
            VulkanVideoPresenter.ShouldUseSwapchainFallbackExtent(width, height));
    }
}
