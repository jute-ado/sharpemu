// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.Bink;
using Xunit;

namespace SharpEmu.Libs.Tests.Bink;

public sealed class BinkFramePlaybackTests
{
    [Fact]
    public void FramesAdvanceAccordingToMovieClock()
    {
        var clock = new ManualTimeProvider();
        using var playback = new BinkFramePlayback(
            new SequenceDecoder(1, 2, 3),
            clock);

        Assert.Equal(1, WaitForAdvancedFrame(playback)[0]);
        Assert.True(playback.TryGetFrame(true, out var heldFrame, out var advanced));
        Assert.False(advanced);
        Assert.Equal(1, heldFrame[0]);

        clock.Advance(TimeSpan.FromMilliseconds(50));
        Assert.Equal(2, WaitForAdvancedFrame(playback)[0]);
        clock.Advance(TimeSpan.FromMilliseconds(50));
        Assert.Equal(3, WaitForAdvancedFrame(playback)[0]);
    }

    private static byte[] WaitForAdvancedFrame(BinkFramePlayback playback)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(2);
        while (DateTime.UtcNow < deadline)
        {
            if (playback.TryGetFrame(true, out var frame, out var advanced) && advanced)
            {
                return frame;
            }

            Thread.Sleep(1);
        }

        throw new TimeoutException("The decoder did not produce a frame.");
    }

    [Fact]
    public void FirstFrameWaitsUntilPresentationStarts()
    {
        var clock = new ManualTimeProvider();
        using var playback = new BinkFramePlayback(
            new SequenceDecoder(1, 2),
            clock);

        var first = WaitForFrame(playback, advanceClock: false);
        Assert.Equal(1, first[0]);
        clock.Advance(TimeSpan.FromSeconds(10));

        Assert.True(playback.TryGetFrame(false, out var held, out var advanced));
        Assert.False(advanced);
        Assert.Equal(1, held[0]);

        Assert.True(playback.TryGetFrame(true, out held, out advanced));
        Assert.False(advanced);
        Assert.Equal(1, held[0]);
        clock.Advance(TimeSpan.FromMilliseconds(50));
        Assert.Equal(2, WaitForAdvancedFrame(playback)[0]);
    }

    [Fact]
    public void LatePresentationReturnsFinalFrameBeforeCompleting()
    {
        var clock = new ManualTimeProvider();
        using var playback = new BinkFramePlayback(
            new SequenceDecoder(1, 2),
            clock);

        Assert.Equal(1, WaitForFrame(playback, advanceClock: false)[0]);
        Assert.True(playback.TryGetFrame(true, out var held, out var advanced));
        Assert.False(advanced);
        Assert.Equal(1, held[0]);

        clock.Advance(TimeSpan.FromMilliseconds(150));

        Assert.True(playback.TryGetFrame(true, out var final, out advanced));
        Assert.True(advanced);
        Assert.Equal(2, final[0]);
        Assert.False(playback.IsFinished);

        Assert.False(playback.TryGetFrame(true, out _, out _));
        Assert.True(playback.IsFinished);
    }

    private static byte[] WaitForFrame(
        BinkFramePlayback playback,
        bool advanceClock)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(2);
        while (DateTime.UtcNow < deadline)
        {
            if (playback.TryGetFrame(advanceClock, out var frame, out _))
            {
                return frame;
            }

            Thread.Sleep(1);
        }

        throw new TimeoutException("The decoder did not produce a frame.");
    }

    private sealed class SequenceDecoder(params byte[] values) : IBinkFrameDecoder
    {
        private int _index;

        public uint Width => 1;

        public uint Height => 1;

        public uint FramesPerSecondNumerator => 20;

        public uint FramesPerSecondDenominator => 1;

        public bool TryDecodeNextFrame(Span<byte> destination)
        {
            if (_index >= values.Length)
            {
                return false;
            }

            destination.Fill(values[_index++]);
            return true;
        }

        public void Dispose()
        {
        }
    }

    private sealed class ManualTimeProvider : TimeProvider
    {
        private long _timestamp;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public override long GetTimestamp() =>
            Interlocked.Read(ref _timestamp);

        public void Advance(TimeSpan elapsed) =>
            Interlocked.Add(ref _timestamp, elapsed.Ticks);
    }
}
