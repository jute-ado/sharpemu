// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.VideoOut;
using Xunit;

namespace SharpEmu.Libs.Tests;

public sealed class VulkanLogicalExtentTests
{
    [Theory]
    [InlineData(1920u, 1080u, 0u, 0u, 1920u, 1080u)]
    [InlineData(2560u, 1440u, 1920u, 1080u, 1920u, 1080u)]
    [InlineData(2560u, 1440u, 1920u, 0u, 1920u, 1440u)]
    public void LogicalExtentPreservesGuestSizeAndFallsBackToPhysicalSize(
        uint physicalWidth,
        uint physicalHeight,
        uint logicalWidth,
        uint logicalHeight,
        uint expectedWidth,
        uint expectedHeight)
    {
        var actual = VulkanVideoPresenter.ResolveLogicalExtent(
            physicalWidth,
            physicalHeight,
            logicalWidth,
            logicalHeight);

        Assert.Equal(expectedWidth, actual.Width);
        Assert.Equal(expectedHeight, actual.Height);
    }
}
