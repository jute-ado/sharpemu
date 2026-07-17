// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.VideoOut;
using Xunit;

namespace SharpEmu.Libs.Tests;

public sealed class GuestWorkPresentationSchedulingTests
{
    [Fact]
    public void ReadyPresentationStopsNewerGuestWorkAtFrameBoundary()
    {
        Assert.False(
            VulkanVideoPresenter.ShouldProcessGuestWorkBeforePresentation(
                hasReadyPresentation: true,
                completedWork: 0));
        Assert.False(
            VulkanVideoPresenter.ShouldProcessGuestWorkBeforePresentation(
                hasReadyPresentation: true,
                completedWork: 64));
    }

    [Fact]
    public void GuestWorkContinuesUntilPresentationIsReadyOrBatchIsFull()
    {
        Assert.True(
            VulkanVideoPresenter.ShouldProcessGuestWorkBeforePresentation(
                hasReadyPresentation: false,
                completedWork: 0));
        Assert.True(
            VulkanVideoPresenter.ShouldProcessGuestWorkBeforePresentation(
                hasReadyPresentation: false,
                completedWork: 127));
        Assert.False(
            VulkanVideoPresenter.ShouldProcessGuestWorkBeforePresentation(
                hasReadyPresentation: false,
                completedWork: 128));
    }
}
