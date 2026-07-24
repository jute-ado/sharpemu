// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.VideoOut;
using Xunit;

namespace SharpEmu.Libs.Tests;

public sealed class GuestWorkCompletionTrackerTests
{
    [Fact]
    public async Task WaitsForSnapshottedGuestWorkWithoutIncludingLaterSubmissions()
    {
        var tracker = new GuestWorkCompletionTracker();
        tracker.MarkEnqueued();
        var target = tracker.EnqueuedSequence;

        var waiter = Task.Run(
            () => tracker.WaitUntilCompleted(
                target,
                TimeSpan.FromSeconds(1)));
        tracker.MarkEnqueued();
        tracker.MarkCompleted();

        Assert.True(await waiter);
        Assert.Equal(2, tracker.EnqueuedSequence);
        Assert.Equal(1, tracker.CompletedSequence);
    }

    [Fact]
    public void WaitHasBoundedTimeoutAndCompletedTargetsReturnImmediately()
    {
        var tracker = new GuestWorkCompletionTracker();
        tracker.MarkEnqueued();

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        Assert.False(
            tracker.WaitUntilCompleted(
                tracker.EnqueuedSequence,
                TimeSpan.FromMilliseconds(20)));
        stopwatch.Stop();

        Assert.InRange(
            stopwatch.Elapsed,
            TimeSpan.FromMilliseconds(10),
            // Loaded hosted runners can deschedule the test process well after
            // Monitor.Wait has observed its 20 ms deadline.
            TimeSpan.FromSeconds(5));
        tracker.MarkCompleted();
        Assert.True(
            tracker.WaitUntilCompleted(
                tracker.EnqueuedSequence,
                TimeSpan.Zero));
    }
}
