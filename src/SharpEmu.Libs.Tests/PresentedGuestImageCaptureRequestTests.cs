// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.VideoOut;
using Xunit;

namespace SharpEmu.Libs.Tests;

public sealed class PresentedGuestImageCaptureRequestTests
{
    [Fact]
    public void ParsesOneExplicitPositiveFrameMilestone()
    {
        Assert.True(
            PresentedGuestImageCaptureRequest.TryParse(
                " 30 ",
                out var request));
        Assert.True(request.IsEnabled);
        Assert.True(request.ShouldCapture(30));
        Assert.False(request.ShouldCapture(1));
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("1,30")]
    [InlineData("not-a-frame")]
    public void RejectsInvalidOrMultipleFrameMilestones(string value)
    {
        Assert.False(
            PresentedGuestImageCaptureRequest.TryParse(
                value,
                out var request));
        Assert.False(request.IsEnabled);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public void EmptyValueCreatesDisabledRequest(string? value)
    {
        Assert.True(
            PresentedGuestImageCaptureRequest.TryParse(
                value,
                out var request));
        Assert.False(request.IsEnabled);
    }
}
