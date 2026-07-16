// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.Libs.VideoOut;

/// <summary>
/// Retains the first frame waiting on guest work while coalescing all newer
/// frames to the latest one. This bounds storage without allowing a producer
/// that continually moves the work-queue tail to starve presentation.
/// </summary>
internal sealed class CoalescingGuestFrameMailbox<T>
{
    internal readonly record struct Entry(
        T Value,
        long Sequence,
        long RequiredWorkSequence);

    private Entry? _pending;

    public Entry? Latest { get; private set; }

    public void Publish(
        T value,
        long sequence,
        long requiredWorkSequence,
        bool replacePending = false)
    {
        var entry = new Entry(value, sequence, requiredWorkSequence);
        Latest = entry;
        if (replacePending || _pending is null)
        {
            _pending = entry;
        }
    }

    public bool HasReady(
        long presentedSequence,
        long completedWorkSequence)
    {
        var candidate = GetCandidate(presentedSequence);
        return candidate is { } entry &&
            entry.RequiredWorkSequence <= completedWorkSequence;
    }

    public bool TryTake(
        long presentedSequence,
        long completedWorkSequence,
        out T value)
    {
        if (_pending is { } stale &&
            stale.Sequence <= presentedSequence)
        {
            _pending = null;
        }

        var candidate = GetCandidate(presentedSequence);
        if (candidate is not { } entry ||
            entry.RequiredWorkSequence > completedWorkSequence)
        {
            value = default!;
            return false;
        }

        value = entry.Value;
        if (_pending is { } pending &&
            pending.Sequence == entry.Sequence)
        {
            _pending = null;
        }

        return true;
    }

    private Entry? GetCandidate(long presentedSequence)
    {
        if (_pending is { } pending &&
            pending.Sequence > presentedSequence)
        {
            return pending;
        }

        if (Latest is { } latest &&
            latest.Sequence > presentedSequence)
        {
            return latest;
        }

        return null;
    }
}
