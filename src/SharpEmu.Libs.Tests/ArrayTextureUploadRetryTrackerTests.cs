// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.Agc;
using Xunit;

namespace SharpEmu.Libs.Tests;

public sealed class ArrayTextureUploadRetryTrackerTests
{
    [Fact]
    public void FailedAllocationIsSkippedWithoutPoisoningOtherAddresses()
    {
        var tracker = new ArrayTextureUploadRetryTracker();
        const ulong failedAddress = 0x1000;

        Assert.True(tracker.ShouldAttempt(failedAddress));

        tracker.MarkUnsupported(failedAddress);

        Assert.False(tracker.ShouldAttempt(failedAddress));
        Assert.True(tracker.ShouldAttempt(0x2000));
    }
}
