// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.Libs.VideoOut;

/// <summary>
/// Tracks guest-visible VideoOut submissions separately from completed flips.
/// The hardware queue is bounded; completion advances in FIFO order when the
/// VideoOut timing source consumes one request.
/// </summary>
internal sealed class VideoOutFlipQueue
{
    internal const int MaxPendingFlips = 17;

    internal readonly record struct Entry(
        int BufferIndex,
        long FlipArgument);

    private readonly Queue<Entry> _pending = new();

    public int PendingCount => _pending.Count;

    public bool CanEnqueue => _pending.Count < MaxPendingFlips;

    public ulong CompletedCount { get; private set; }

    public int CurrentBuffer { get; private set; } = -1;

    public long LastFlipArgument { get; private set; }

    public bool TryEnqueue(
        int bufferIndex,
        long flipArgument)
    {
        if (!CanEnqueue)
        {
            return false;
        }

        _pending.Enqueue(new Entry(bufferIndex, flipArgument));
        return true;
    }

    public bool TryComplete(out Entry completed)
    {
        if (!_pending.TryDequeue(out completed))
        {
            return false;
        }

        CompletedCount++;
        CurrentBuffer = completed.BufferIndex;
        LastFlipArgument = completed.FlipArgument;
        return true;
    }
}
