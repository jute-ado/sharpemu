// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Diagnostics;

namespace SharpEmu.Libs.VideoOut;

internal sealed class GuestWorkCompletionTracker
{
    private readonly object _gate = new();
    private long _enqueuedSequence;
    private long _completedSequence;

    public long EnqueuedSequence
    {
        get
        {
            lock (_gate)
            {
                return _enqueuedSequence;
            }
        }
    }

    public long CompletedSequence
    {
        get
        {
            lock (_gate)
            {
                return _completedSequence;
            }
        }
    }

    public long MarkEnqueued()
    {
        lock (_gate)
        {
            return ++_enqueuedSequence;
        }
    }

    public void MarkCompleted()
    {
        lock (_gate)
        {
            if (_completedSequence >= _enqueuedSequence)
            {
                throw new InvalidOperationException(
                    "Guest work completion exceeded the enqueued sequence.");
            }

            _completedSequence++;
            Monitor.PulseAll(_gate);
        }
    }

    public bool WaitUntilCompleted(long targetSequence, TimeSpan timeout)
    {
        if (targetSequence < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(targetSequence));
        }
        if (timeout < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout));
        }

        lock (_gate)
        {
            if (targetSequence > _enqueuedSequence)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(targetSequence),
                    "Cannot wait for guest work that was not enqueued.");
            }
            if (_completedSequence >= targetSequence)
            {
                return true;
            }
            if (timeout == TimeSpan.Zero)
            {
                return false;
            }

            var started = Stopwatch.GetTimestamp();
            var remaining = timeout;
            while (_completedSequence < targetSequence)
            {
                Monitor.Wait(_gate, remaining);
                if (_completedSequence >= targetSequence)
                {
                    return true;
                }

                remaining = timeout - Stopwatch.GetElapsedTime(started);
                if (remaining <= TimeSpan.Zero)
                {
                    return false;
                }
            }

            return true;
        }
    }
}
