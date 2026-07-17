// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.VideoOut;
using Xunit;

namespace SharpEmu.Libs.Tests;

public sealed class CoalescingGuestFrameMailboxTests
{
    [Fact]
    public void ReadyPendingFrameIsNotStarvedByNewerGuestWork()
    {
        var mailbox = new CoalescingGuestFrameMailbox<string>();

        mailbox.Publish("first", sequence: 1, requiredWorkSequence: 1);
        for (var sequence = 2; sequence <= 100; sequence++)
        {
            mailbox.Publish(
                $"frame-{sequence}",
                sequence,
                requiredWorkSequence: sequence);
        }

        Assert.True(
            mailbox.HasReady(
                presentedSequence: 0,
                completedWorkSequence: 1));
        Assert.True(
            mailbox.TryTake(
                presentedSequence: 0,
                completedWorkSequence: 1,
                out var first));
        Assert.Equal("first", first);
    }

    [Fact]
    public void NewerFramesCoalesceAfterPendingFrameIsTaken()
    {
        var mailbox = new CoalescingGuestFrameMailbox<string>();
        mailbox.Publish("first", sequence: 1, requiredWorkSequence: 1);
        mailbox.Publish("second", sequence: 2, requiredWorkSequence: 2);
        mailbox.Publish("latest", sequence: 3, requiredWorkSequence: 3);

        Assert.True(
            mailbox.TryTake(
                presentedSequence: 0,
                completedWorkSequence: 1,
                out _));
        Assert.False(
            mailbox.TryTake(
                presentedSequence: 1,
                completedWorkSequence: 2,
                out _));
        Assert.True(
            mailbox.TryTake(
                presentedSequence: 1,
                completedWorkSequence: 3,
                out var latest));
        Assert.Equal("latest", latest);
    }

    [Fact]
    public void ExplicitReplacementDiscardsPendingSplashFrame()
    {
        var mailbox = new CoalescingGuestFrameMailbox<string>();
        mailbox.Publish("splash", sequence: 1, requiredWorkSequence: 0);
        mailbox.Publish(
            "hidden",
            sequence: 2,
            requiredWorkSequence: 0,
            replacePending: true);

        Assert.True(
            mailbox.TryTake(
                presentedSequence: 0,
                completedWorkSequence: 0,
                out var frame));
        Assert.Equal("hidden", frame);
    }
}
