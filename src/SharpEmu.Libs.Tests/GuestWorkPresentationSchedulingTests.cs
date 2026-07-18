// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.Gpu;
using Xunit;

namespace SharpEmu.Libs.Tests;

public sealed class GuestWorkPresentationSchedulingTests
{
    [Fact]
    public void ReadyPresentationStopsNewerGuestWorkAtFrameBoundary()
    {
        Assert.False(
            GuestWorkPresentationPolicy.ShouldProcessGuestWork(
                hasReadyPresentation: true,
                completedWork: 0,
                maxGuestWork: 128));
        Assert.False(
            GuestWorkPresentationPolicy.ShouldProcessGuestWork(
                hasReadyPresentation: true,
                completedWork: 64,
                maxGuestWork: 128));
    }

    [Fact]
    public void GuestWorkContinuesUntilPresentationIsReadyOrBatchIsFull()
    {
        Assert.True(
            GuestWorkPresentationPolicy.ShouldProcessGuestWork(
                hasReadyPresentation: false,
                completedWork: 0,
                maxGuestWork: 128));
        Assert.True(
            GuestWorkPresentationPolicy.ShouldProcessGuestWork(
                hasReadyPresentation: false,
                completedWork: 127,
                maxGuestWork: 128));
        Assert.False(
            GuestWorkPresentationPolicy.ShouldProcessGuestWork(
                hasReadyPresentation: false,
                completedWork: 128,
                maxGuestWork: 128));
    }
}
