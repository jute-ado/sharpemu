// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.VideoOut;
using Silk.NET.Vulkan;
using Xunit;

namespace SharpEmu.Libs.Tests;

public sealed class GuestStencilVulkanPolicyTests
{
    [Theory]
    [InlineData(0u, (int)StencilOp.Keep)]
    [InlineData(1u, (int)StencilOp.Zero)]
    [InlineData(3u, (int)StencilOp.Replace)]
    [InlineData(4u, (int)StencilOp.Replace)]
    [InlineData(5u, (int)StencilOp.IncrementAndClamp)]
    [InlineData(6u, (int)StencilOp.DecrementAndClamp)]
    [InlineData(7u, (int)StencilOp.Invert)]
    [InlineData(8u, (int)StencilOp.IncrementAndWrap)]
    [InlineData(9u, (int)StencilOp.DecrementAndWrap)]
    public void SupportedGuestOperationsHaveExactVulkanMappings(
        uint guestOp,
        int expected)
    {
        Assert.True(VulkanVideoPresenter.TryGetStencilOp(
            guestOp,
            out var stencilOp));
        Assert.Equal((StencilOp)expected, stencilOp);
    }

    [Theory]
    [InlineData(2u)]
    [InlineData(10u)]
    [InlineData(15u)]
    [InlineData(uint.MaxValue)]
    public void UnsupportedGuestOperationsHaveNoFallback(uint guestOp)
    {
        Assert.False(VulkanVideoPresenter.TryGetStencilOp(
            guestOp,
            out _));
    }
}
