// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Diagnostics;
using SharpEmu.Core.Memory;
using SharpEmu.HLE;
using Xunit;

namespace SharpEmu.Core.Tests;

public sealed class GuestThreadExecutionTests : IDisposable
{
    public GuestThreadExecutionTests() => ResetAmbientState();

    public void Dispose() => ResetAmbientState();

    [Fact]
    public void BlockRequestsRequireGuestThreadAndConsumeOnce()
    {
        Assert.False(GuestThreadExecution.RequestCurrentThreadBlock("outside"));

        _ = GuestThreadExecution.EnterGuestThread(0x1234);
        var waiter = new RecordingWaiter();
        Assert.True(GuestThreadExecution.RequestCurrentThreadBlock(
            context: null,
            reason: " ",
            wakeKey: "",
            waiter,
            blockDeadlineTimestamp: 12345));

        Assert.True(GuestThreadExecution.TryConsumeCurrentThreadBlock(
            out var reason,
            out var continuation,
            out var hasContinuation,
            out var wakeKey,
            out IGuestThreadBlockWaiter? consumedWaiter,
            out var deadline));
        Assert.Equal("guest_thread_blocked", reason);
        Assert.Equal(reason, wakeKey);
        Assert.False(hasContinuation);
        Assert.Equal(default, continuation);
        Assert.Same(waiter, consumedWaiter);
        Assert.Equal(12345, deadline);

        Assert.False(GuestThreadExecution.TryConsumeCurrentThreadBlock(
            out reason,
            out continuation,
            out hasContinuation,
            out wakeKey,
            out consumedWaiter,
            out deadline));
        Assert.Empty(reason);
        Assert.Empty(wakeKey);
        Assert.False(hasContinuation);
        Assert.Null(consumedWaiter);
        Assert.Equal(0, deadline);
    }

    [Fact]
    public void BlockContinuationCapturesImportReturnAndCpuState()
    {
        _ = GuestThreadExecution.EnterGuestThread(0x2345);
        var context = CreateContext();
        var previousFrame = GuestThreadExecution.EnterImportCallFrame(
            returnRip: 0x1_2345,
            resumeRsp: 0x2_0000,
            returnSlotAddress: 0x1_FFF8);
        try
        {
            Assert.True(GuestThreadExecution.RequestCurrentThreadBlock(context, "wait"));
            Assert.True(GuestThreadExecution.TryConsumeCurrentThreadBlock(
                out var reason,
                out var continuation,
                out var hasContinuation));

            Assert.Equal("wait", reason);
            Assert.True(hasContinuation);
            Assert.Equal(0x1_2345UL, continuation.Rip);
            Assert.Equal(0x2_0000UL, continuation.Rsp);
            Assert.Equal(0x1_FFF8UL, continuation.ReturnSlotAddress);
            Assert.Equal(context.Rflags, continuation.Rflags);
            Assert.Equal(context.FsBase, continuation.FsBase);
            Assert.Equal(context.GsBase, continuation.GsBase);
            Assert.Equal(0UL, continuation.Rax);
            Assert.Equal(context[CpuRegister.Rcx], continuation.Rcx);
            Assert.Equal(context[CpuRegister.R15], continuation.R15);
            Assert.Equal(context.FpuControlWord, continuation.FpuControlWord);
            Assert.Equal(context.Mxcsr, continuation.Mxcsr);
            Assert.False(continuation.RestoreFullFpuState);
        }
        finally
        {
            GuestThreadExecution.RestoreImportCallFrame(previousFrame);
        }
    }

    [Theory]
    [InlineData(0xFFFFUL, 0x20000UL, 0x1FFF8UL)]
    [InlineData(0x10000UL, 0UL, 0x1FFF8UL)]
    [InlineData(0x10000UL, 0x20000UL, 0UL)]
    public void InvalidImportFramesDoNotProduceBlockContinuation(
        ulong returnRip,
        ulong resumeRsp,
        ulong returnSlotAddress)
    {
        _ = GuestThreadExecution.EnterGuestThread(0x3456);
        var previousFrame = GuestThreadExecution.EnterImportCallFrame(
            returnRip,
            resumeRsp,
            returnSlotAddress);
        try
        {
            Assert.True(GuestThreadExecution.RequestCurrentThreadBlock(CreateContext(), "wait"));
            Assert.True(GuestThreadExecution.TryConsumeCurrentThreadBlock(
                out _,
                out var continuation,
                out var hasContinuation));
            Assert.False(hasContinuation);
            Assert.Equal(default, continuation);
        }
        finally
        {
            GuestThreadExecution.RestoreImportCallFrame(previousFrame);
        }
    }

    [Fact]
    public void DelegateWaiterCompatibilityRoundTripsHandlers()
    {
        _ = GuestThreadExecution.EnterGuestThread(0x4567);
        var resumeCalls = 0;
        var wakeCalls = 0;
        Assert.True(GuestThreadExecution.RequestCurrentThreadBlock(
            context: null,
            reason: "delegate-wait",
            wakeKey: "delegate-key",
            resumeHandler: () =>
            {
                resumeCalls++;
                return 17;
            },
            wakeHandler: () =>
            {
                wakeCalls++;
                return true;
            },
            blockDeadlineTimestamp: 99));

        Assert.True(GuestThreadExecution.TryConsumeCurrentThreadBlock(
            out _,
            out _,
            out _,
            out var wakeKey,
            out var resume,
            out var wake,
            out var deadline));
        Assert.Equal("delegate-key", wakeKey);
        Assert.Equal(99, deadline);
        Assert.NotNull(resume);
        Assert.NotNull(wake);
        Assert.Equal(17, resume());
        Assert.True(wake());
        Assert.Equal(1, resumeCalls);
        Assert.Equal(1, wakeCalls);
    }

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
        Assert.True(GuestThreadExecution.RequestCurrentThreadBlock("old-block"));
        GuestThreadExecution.RequestCurrentEntryExit("old-exit", 1UL);
        GuestThreadExecution.RequestCurrentContextTransfer(
            default(GuestCpuContinuation) with { Rip = 0x1234 });
        _ = GuestThreadExecution.EnterImportCallFrame(1, 2, 3);

        var previous = GuestThreadExecution.EnterGuestThread(0x789A);

        Assert.Equal(0x6789UL, previous);
        Assert.Equal(0x789AUL, GuestThreadExecution.CurrentGuestThreadHandle);
        Assert.False(GuestThreadExecution.TryConsumeCurrentThreadBlock(out _));
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

    private static CpuContext CreateContext()
    {
        var context = new CpuContext(new VirtualMemory(), Generation.Gen5)
        {
            Rflags = 0x202,
            FsBase = 0x1111,
            GsBase = 0x2222,
            FpuControlWord = 0x027F,
            Mxcsr = 0x1FA0,
        };
        foreach (var register in Enum.GetValues<CpuRegister>())
        {
            context[register] = 0x1000UL + (ulong)register;
        }

        return context;
    }

    private static void ResetAmbientState()
    {
        GuestThreadExecution.RestoreGuestThread(0);
        GuestThreadExecution.RestoreFiber(0);
        GuestThreadExecution.Scheduler = null;
    }

    private sealed class RecordingWaiter : IGuestThreadBlockWaiter
    {
        public int Resume() => 0;

        public bool TryWake() => false;
    }
}
