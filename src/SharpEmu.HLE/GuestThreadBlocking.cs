// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Collections.Concurrent;
using System.Diagnostics;

namespace SharpEmu.HLE;

/// <summary>
/// Coordinates HLE synchronization primitives that park a guest thread's
/// dedicated host thread inside the HLE call. Monitor owns the predicate-check
/// and park transition, eliminating the lost-wakeup window created by
/// continuation capture and wake-key rescheduling.
/// </summary>
public static class GuestThreadBlocking
{
    /// <summary>
    /// Maximum duration of one host wait. Slicing lets teardown and guest
    /// exception delivery interrupt an otherwise indefinite wait promptly.
    /// </summary>
    public const int WaitSliceMilliseconds = 50;

    private static volatile bool _shutdownRequested;
    private static readonly ConcurrentDictionary<ulong, string> _blockDescriptions = new();
    private static readonly ConcurrentDictionary<ulong, byte> _interrupted = new();

    /// <summary>True after execution teardown has requested parked threads to unwind.</summary>
    public static bool ShutdownRequested => _shutdownRequested;

    /// <summary>
    /// Starts a fresh execution lifetime. This is separate from Execute because
    /// one backend executes multiple module initializers before running a title.
    /// </summary>
    public static void BeginExecution()
    {
        _shutdownRequested = false;
        _blockDescriptions.Clear();
        _interrupted.Clear();
    }

    /// <summary>Requests all sliced waits to unwind during backend teardown.</summary>
    public static void RequestShutdown() => _shutdownRequested = true;

    /// <summary>Records what a guest thread is about to wait on.</summary>
    public static void NoteBlocked(ulong guestThreadHandle, string description)
    {
        if (guestThreadHandle != 0)
        {
            _blockDescriptions[guestThreadHandle] = description;
        }
    }

    /// <summary>Clears a thread's wait diagnostic.</summary>
    public static void NoteUnblocked(ulong guestThreadHandle)
    {
        if (guestThreadHandle != 0)
        {
            _blockDescriptions.TryRemove(guestThreadHandle, out _);
        }
    }

    /// <summary>Returns the current in-place wait description, if any.</summary>
    public static string? DescribeBlock(ulong guestThreadHandle) =>
        _blockDescriptions.TryGetValue(guestThreadHandle, out var description)
            ? description
            : null;

    /// <summary>Snapshots current in-place waits for stall diagnostics.</summary>
    public static KeyValuePair<ulong, string>[] SnapshotBlockDescriptions() =>
        [.. _blockDescriptions];

    /// <summary>
    /// Set by the active backend to deliver an exception queued for the current
    /// guest thread on that thread's own host thread.
    /// </summary>
    public static Action? DeliverInterruptForCurrentThread { get; set; }

    /// <summary>Flags a parked thread to deliver its queued exception.</summary>
    public static void RequestInterrupt(ulong guestThreadHandle)
    {
        if (guestThreadHandle != 0)
        {
            _interrupted[guestThreadHandle] = 0;
        }
    }

    /// <summary>
    /// Clears an interrupt that was delivered at a normal import safe point
    /// before the target entered an HLE wait.
    /// </summary>
    public static void AcknowledgeInterrupt(ulong guestThreadHandle)
    {
        if (guestThreadHandle != 0)
        {
            _interrupted.TryRemove(guestThreadHandle, out _);
        }
    }

    /// <summary>
    /// Delivers a pending guest exception outside an HLE synchronization gate,
    /// then reacquires the gate so the caller can re-check its wait predicate.
    /// The caller must hold <paramref name="gate"/>.
    /// </summary>
    public static void Checkpoint(ulong guestThreadHandle, object gate)
    {
        if (_interrupted.IsEmpty ||
            guestThreadHandle == 0 ||
            !_interrupted.TryRemove(guestThreadHandle, out _))
        {
            return;
        }

        var deliver = DeliverInterruptForCurrentThread;
        if (deliver is null)
        {
            return;
        }

        Monitor.Exit(gate);
        try
        {
            deliver();
        }
        finally
        {
            Monitor.Enter(gate);
        }
    }

    /// <summary>
    /// Returns a positive, bounded host wait for a Stopwatch deadline, or zero
    /// when the deadline has elapsed.
    /// </summary>
    public static int GetWaitMilliseconds(long deadlineTimestamp)
    {
        var remainingTicks = deadlineTimestamp - Stopwatch.GetTimestamp();
        if (remainingTicks <= 0)
        {
            return 0;
        }

        var remainingMilliseconds =
            remainingTicks * 1000d / Stopwatch.Frequency;
        return (int)Math.Max(
            1d,
            Math.Min(WaitSliceMilliseconds, Math.Ceiling(remainingMilliseconds)));
    }
}
