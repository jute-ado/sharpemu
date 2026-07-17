// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.VideoOut;
using Xunit;

namespace SharpEmu.Libs.Tests;

public sealed class VideoOutFlipQueueTests
{
    [Fact]
    public void FullQueueRejectsSubmissionsBeyondHardwareDepth()
    {
        var queue = new VideoOutFlipQueue();

        for (var index = 0;
             index < VideoOutFlipQueue.MaxPendingFlips;
             index++)
        {
            Assert.True(queue.TryEnqueue(
                bufferIndex: index % 2,
                flipArgument: index));
        }

        Assert.False(queue.TryEnqueue(
            bufferIndex: 0,
            flipArgument: 999));
        Assert.Equal(
            VideoOutFlipQueue.MaxPendingFlips,
            queue.PendingCount);
        Assert.Equal(0UL, queue.CompletedCount);
        Assert.Equal(-1, queue.CurrentBuffer);
    }

    [Fact]
    public void CompletionAdvancesStatusInSubmissionOrder()
    {
        var queue = new VideoOutFlipQueue();
        Assert.True(queue.TryEnqueue(
            bufferIndex: 1,
            flipArgument: 0x1111));
        Assert.True(queue.TryEnqueue(
            bufferIndex: 0,
            flipArgument: 0x2222));

        Assert.True(queue.TryComplete(out var first));
        Assert.Equal(1, first.BufferIndex);
        Assert.Equal(0x1111, first.FlipArgument);
        Assert.Equal(1UL, queue.CompletedCount);
        Assert.Equal(1, queue.CurrentBuffer);
        Assert.Equal(0x1111, queue.LastFlipArgument);
        Assert.Equal(1, queue.PendingCount);

        Assert.True(queue.TryComplete(out var second));
        Assert.Equal(0, second.BufferIndex);
        Assert.Equal(0x2222, second.FlipArgument);
        Assert.Equal(2UL, queue.CompletedCount);
        Assert.Equal(0, queue.CurrentBuffer);
        Assert.Equal(0x2222, queue.LastFlipArgument);
        Assert.Equal(0, queue.PendingCount);
        Assert.False(queue.TryComplete(out _));
    }
}
