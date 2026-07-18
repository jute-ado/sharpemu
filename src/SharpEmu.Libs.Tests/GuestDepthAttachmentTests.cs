// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.Gpu;
using SharpEmu.Libs.VideoOut;
using Xunit;

namespace SharpEmu.Libs.Tests;

public sealed class GuestDepthAttachmentTests
{
    private static readonly GuestDepthTarget Target = new(
        ReadAddress: 0x1000,
        WriteAddress: 0x1000,
        Width: 1920,
        Height: 1080,
        GuestFormat: 1,
        SwizzleMode: 0,
        ClearDepth: 1f,
        ReadOnly: false);

    [Theory]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(false, false, true)]
    public void DepthAttachmentIsRequiredForAnyDepthOperation(
        bool test,
        bool write,
        bool clear)
    {
        Assert.True(VulkanVideoPresenter.ShouldAttachGuestDepth(
            Target,
            new GuestDepthState(test, write, CompareOp: 3, clear)));
    }

    [Fact]
    public void DepthAttachmentRequiresTargetAndDepthWork()
    {
        Assert.False(VulkanVideoPresenter.ShouldAttachGuestDepth(
            Target,
            GuestDepthState.Default));
        Assert.False(VulkanVideoPresenter.ShouldAttachGuestDepth(
            null,
            new GuestDepthState(true, false, CompareOp: 3)));
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void DepthAttachmentIsRequiredForStencilWork(
        bool test,
        bool clear)
    {
        var state = GuestDepthState.Default with
        {
            Stencil = GuestStencilState.Default with
            {
                TestEnable = test,
                ClearEnable = clear,
            },
        };

        Assert.True(VulkanVideoPresenter.ShouldAttachGuestDepth(Target, state));
    }

    [Fact]
    public void StaleOneByOneExtentExpandsToColorTarget()
    {
        var stale = Target with { Width = 1, Height = 1 };

        var resolved = GuestDepthExtentResolver.Resolve(
            stale,
            colorWidth: 1280,
            colorHeight: 720,
            textures: []);

        Assert.True(resolved.IsUsable);
        Assert.Equal(GuestDepthExtentResolutionKind.StaleOneByOne, resolved.Kind);
        Assert.Equal(1280u, resolved.Width);
        Assert.Equal(720u, resolved.Height);
    }

    [Fact]
    public void MatchingTextureCanResolveUndersizedDepthMetadata()
    {
        var undersized = Target with { Width = 640, Height = 360 };
        var texture = new GuestDrawTexture(
            Target.Address,
            Width: 1920,
            Height: 1080,
            Format: 56,
            NumberType: 0,
            RgbaPixels: [],
            IsFallback: false,
            IsStorage: false);

        var resolved = GuestDepthExtentResolver.Resolve(
            undersized,
            colorWidth: 1920,
            colorHeight: 1080,
            [texture]);

        Assert.Equal(GuestDepthExtentResolutionKind.TextureAlias, resolved.Kind);
        Assert.Equal(1920u, resolved.Width);
        Assert.Equal(1080u, resolved.Height);
    }
}
