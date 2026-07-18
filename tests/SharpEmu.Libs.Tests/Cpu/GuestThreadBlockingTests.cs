// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Diagnostics;
using SharpEmu.Core.Cpu.Native;
using SharpEmu.HLE;
using Xunit;

namespace SharpEmu.Libs.Tests.Cpu;

public sealed class GuestThreadBlockingTests
{
    [Fact]
    public void CooperativeBlockingSurfaceIsRemoved()
    {
        Assert.Null(typeof(SharpEmu.HLE.GuestThreadExecution).GetMethod(
            "RequestCurrentThreadBlock"));
        Assert.Null(typeof(SharpEmu.HLE.GuestThreadExecution).GetMethod(
            "TryConsumeCurrentThreadBlock"));
        Assert.Null(typeof(IGuestThreadScheduler).GetMethod(
            "WakeBlockedThreads"));
        Assert.Null(typeof(DirectExecutionBackend).GetNestedType(
            "GuestThreadState",
            System.Reflection.BindingFlags.NonPublic)!
            .GetProperty("BlockWaiter"));
    }

    [Fact]
    public void BeginExecutionClearsShutdownAndDiagnostics()
    {
        GuestThreadBlocking.BeginExecution();
        GuestThreadBlocking.NoteBlocked(0x1234, "test-wait");
        GuestThreadBlocking.RequestInterrupt(0x1234);
        GuestThreadBlocking.RequestShutdown();

        GuestThreadBlocking.BeginExecution();

        Assert.False(GuestThreadBlocking.ShutdownRequested);
        Assert.Null(GuestThreadBlocking.DescribeBlock(0x1234));
        Assert.Empty(GuestThreadBlocking.SnapshotBlockDescriptions());
    }

    [Fact]
    public void CheckpointRunsInterruptOutsideGateAndReacquiresIt()
    {
        GuestThreadBlocking.BeginExecution();
        var gate = new object();
        var deliveredWithoutGate = false;
        GuestThreadBlocking.DeliverInterruptForCurrentThread = () =>
            deliveredWithoutGate = !Monitor.IsEntered(gate);
        GuestThreadBlocking.RequestInterrupt(0x42);

        lock (gate)
        {
            GuestThreadBlocking.Checkpoint(0x42, gate);
            Assert.True(Monitor.IsEntered(gate));
        }

        Assert.True(deliveredWithoutGate);
        GuestThreadBlocking.DeliverInterruptForCurrentThread = null;
    }

    [Fact]
    public void SafePointAcknowledgementPreventsStaleWaitInterrupt()
    {
        GuestThreadBlocking.BeginExecution();
        var gate = new object();
        var deliveries = 0;
        GuestThreadBlocking.DeliverInterruptForCurrentThread =
            () => deliveries++;
        GuestThreadBlocking.RequestInterrupt(0x43);

        GuestThreadBlocking.AcknowledgeInterrupt(0x43);
        lock (gate)
        {
            GuestThreadBlocking.Checkpoint(0x43, gate);
        }

        Assert.Equal(0, deliveries);
        GuestThreadBlocking.DeliverInterruptForCurrentThread = null;
    }

    [Fact]
    public void WaitSliceIsPositiveBoundedAndExpires()
    {
        var deadline = GuestThreadExecution.ComputeDeadlineTimestamp(
            TimeSpan.FromSeconds(1));

        Assert.InRange(
            GuestThreadBlocking.GetWaitMilliseconds(deadline),
            1,
            GuestThreadBlocking.WaitSliceMilliseconds);
        Assert.Equal(
            0,
            GuestThreadBlocking.GetWaitMilliseconds(
                Stopwatch.GetTimestamp() - 1));
    }
}
