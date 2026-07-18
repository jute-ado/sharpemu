// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Diagnostics;
using SharpEmu.HLE;
using Xunit;

namespace SharpEmu.Core.Tests;

public sealed class GuestThreadExecutionTests : IDisposable
{
    public GuestThreadExecutionTests() => ResetAmbientState();

    public void Dispose() => ResetAmbientState();

    [Fact]
    public void EntryExitAndContextTransferAreOneShotSignals()
    {
        _ = GuestThreadExecution.EnterGuestThread(0x5678);
        GuestThreadExecution.RequestCurrentEntryExit("exit", -7);

        Assert.True(GuestThreadExecution.TryConsumeCurrentEntryExit(out var value, out var reason));
        Assert.Equal(unchecked((ulong)-7L), value);
        Assert.Equal("exit", reason);
        Assert.False(GuestThreadExecution.TryConsumeCurrentEntryExit(out value, out reason));
        Assert.Equal(0UL, value);
        Assert.Empty(reason);

        var target = default(GuestCpuContinuation) with
        {
            Rip = 0x6000,
            Rsp = 0x7000,
            Rax = 0x88,
        };
        GuestThreadExecution.RequestCurrentContextTransfer(target);
        Assert.True(GuestThreadExecution.TryConsumeCurrentContextTransfer(out var consumed));
        Assert.Equal(target, consumed);
        Assert.False(GuestThreadExecution.TryConsumeCurrentContextTransfer(out consumed));
        Assert.Equal(default, consumed);
    }

    [Fact]
    public void FiberAndImportFramesSupportNestedRestoration()
    {
        var previousFiber = GuestThreadExecution.EnterFiber(0x1000);
        Assert.Equal(0UL, previousFiber);
        Assert.Equal(0x1000UL, GuestThreadExecution.CurrentFiberAddress);
        var nestedFiber = GuestThreadExecution.EnterFiber(0x2000);
        Assert.Equal(0x1000UL, nestedFiber);
        GuestThreadExecution.RestoreFiber(nestedFiber);
        Assert.Equal(0x1000UL, GuestThreadExecution.CurrentFiberAddress);
        GuestThreadExecution.RestoreFiber(previousFiber);
        Assert.Equal(0UL, GuestThreadExecution.CurrentFiberAddress);

        Assert.False(GuestThreadExecution.TryGetCurrentImportCallFrame(out _));
        var outer = GuestThreadExecution.EnterImportCallFrame(1, 2, 3);
        Assert.False(outer.IsValid);
        var inner = GuestThreadExecution.EnterImportCallFrame(4, 5, 6);
        Assert.True(inner.IsValid);
        Assert.Equal(new GuestImportCallFrame(true, 1, 2, 3), inner);
        Assert.True(GuestThreadExecution.TryGetCurrentImportCallFrame(out var current));
        Assert.Equal(new GuestImportCallFrame(true, 4, 5, 6), current);
        GuestThreadExecution.RestoreImportCallFrame(inner);
        Assert.True(GuestThreadExecution.TryGetCurrentImportCallFrame(out current));
        Assert.Equal(new GuestImportCallFrame(true, 1, 2, 3), current);
        GuestThreadExecution.RestoreImportCallFrame(outer);
        Assert.False(GuestThreadExecution.TryGetCurrentImportCallFrame(out _));
    }

    [Fact]
    public void EnteringGuestThreadResetsPendingSignals()
    {
        _ = GuestThreadExecution.EnterGuestThread(0x6789);
        GuestThreadExecution.RequestCurrentEntryExit("old-exit", 1UL);
        GuestThreadExecution.RequestCurrentContextTransfer(
            default(GuestCpuContinuation) with { Rip = 0x1234 });
        _ = GuestThreadExecution.EnterImportCallFrame(1, 2, 3);

        var previous = GuestThreadExecution.EnterGuestThread(0x789A);

        Assert.Equal(0x6789UL, previous);
        Assert.Equal(0x789AUL, GuestThreadExecution.CurrentGuestThreadHandle);
        Assert.False(GuestThreadExecution.TryConsumeCurrentEntryExit(out _, out _));
        Assert.False(GuestThreadExecution.TryConsumeCurrentContextTransfer(out _));
        Assert.False(GuestThreadExecution.TryGetCurrentImportCallFrame(out _));
    }

    [Fact]
    public void DeadlineCalculationHandlesImmediateFiniteAndSaturatedTimeouts()
    {
        var beforeImmediate = Stopwatch.GetTimestamp();
        var immediate = GuestThreadExecution.ComputeDeadlineTimestamp(TimeSpan.Zero);
        var afterImmediate = Stopwatch.GetTimestamp();
        Assert.InRange(immediate, beforeImmediate, afterImmediate);

        var beforeFinite = Stopwatch.GetTimestamp();
        var finite = GuestThreadExecution.ComputeDeadlineTimestamp(TimeSpan.FromMilliseconds(1));
        Assert.True(finite > beforeFinite);
        Assert.Equal(long.MaxValue, GuestThreadExecution.ComputeDeadlineTimestamp(TimeSpan.MaxValue));
    }

    private static void ResetAmbientState()
    {
        GuestThreadExecution.RestoreGuestThread(0);
        GuestThreadExecution.RestoreFiber(0);
        GuestThreadExecution.Scheduler = null;
    }
}
