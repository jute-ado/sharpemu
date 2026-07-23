// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.VideoOut;
using Silk.NET.Vulkan;
using Xunit;

namespace SharpEmu.Libs.Tests;

public sealed class VulkanImageViewUsageTests
{
    [Fact]
    public void ArrayedAliasNeverReusesNonArrayBaseView()
    {
        Assert.False(VulkanVideoPresenter.CanReuseGuestImageBaseView(
            Format.R8G8B8A8Unorm,
            0xFAC,
            Format.R8G8B8A8Unorm,
            0xFAC,
            arrayedView: true));
        Assert.True(VulkanVideoPresenter.CanReuseGuestImageBaseView(
            Format.R8G8B8A8Unorm,
            0xFAC,
            Format.R8G8B8A8Unorm,
            0xFAC,
            arrayedView: false));
    }

    [Fact]
    public void SrgbCompatibleViewDoesNotInheritUnsupportedStorageUsage()
    {
        var usage = VulkanVideoPresenter.ResolveGuestImageViewUsage(
            supportsColorAttachment: true,
            supportsStorageImage: false);

        Assert.True((usage & ImageUsageFlags.SampledBit) != 0);
        Assert.True((usage & ImageUsageFlags.ColorAttachmentBit) != 0);
        Assert.False((usage & ImageUsageFlags.StorageBit) != 0);
    }

    [Fact]
    public void LinearStorageViewRetainsStorageUsage()
    {
        var usage = VulkanVideoPresenter.ResolveGuestImageViewUsage(
            supportsColorAttachment: true,
            supportsStorageImage: true);

        Assert.True((usage & ImageUsageFlags.StorageBit) != 0);
    }

    [Fact]
    public void AliasViewCannotAddStorageUsageMissingFromBackingImage()
    {
        var usage = VulkanVideoPresenter.ResolveGuestImageViewUsage(
            supportsColorAttachment: true,
            supportsStorageImage: true,
            backingImageSupportsStorage: false);

        Assert.False((usage & ImageUsageFlags.StorageBit) != 0);
    }
}
