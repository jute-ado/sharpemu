// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.Gpu;
using SharpEmu.Libs.VideoOut;
using Xunit;

namespace SharpEmu.Libs.Tests;

public sealed class IntegerAttachmentBlendTests
{
    [Fact]
    public void IntegerAttachmentDisablesBlendWithoutChangingOtherState()
    {
        var enabled = GuestBlendState.Default with
        {
            Enable = true,
            WriteMask = 0x5,
        };

        var normalized = VulkanVideoPresenter.NormalizeIntegerAttachmentBlends(
            [enabled, enabled],
            [true, false],
            out var normalizedCount);

        Assert.Equal(1, normalizedCount);
        Assert.False(normalized[0].Enable);
        Assert.Equal(0x5u, normalized[0].WriteMask);
        Assert.Equal(enabled, normalized[1]);
    }

    [Fact]
    public void AttachmentAndBlendCountsMustMatch()
    {
        Assert.Throws<ArgumentException>(() =>
            VulkanVideoPresenter.NormalizeIntegerAttachmentBlends(
                [GuestBlendState.Default],
                [],
                out _));
    }
}
