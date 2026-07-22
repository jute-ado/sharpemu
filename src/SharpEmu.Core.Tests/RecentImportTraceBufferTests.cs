// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Core.Cpu.Native;
using Xunit;

namespace SharpEmu.Core.Tests;

public sealed class RecentImportTraceBufferTests
{
    [Fact]
    public void RecordingAfterWarmupDoesNotAllocatePerImport()
    {
        var trace = new RecentImportTraceBuffer(capacity: 4);
        var entry = Data(dispatchIndex: 1, threadHandle: 1);
        trace.Record(entry).Complete(returnValue: 0x1234);

        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var dispatchIndex = 2; dispatchIndex <= 10_001; dispatchIndex++)
        {
            trace.Record(entry with { DispatchIndex = dispatchIndex })
                .Complete(returnValue: (ulong)dispatchIndex);
        }
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.InRange(allocated, 0, 256);
    }

    [Fact]
    public void LateCompletionCannotMutateAReusedRingSlot()
    {
        var trace = new RecentImportTraceBuffer(capacity: 1);
        var overwritten = trace.Record(Data(dispatchIndex: 1, threadHandle: 1));
        var current = trace.Record(Data(dispatchIndex: 2, threadHandle: 1));

        overwritten.Complete(returnValue: 0x1111);
        var pending = trace.Build(1, prioritizedThreadHandle: null);
        Assert.DoesNotContain("#1 ", pending, StringComparison.Ordinal);
        Assert.Contains("#2 ", pending, StringComparison.Ordinal);
        Assert.Contains("rax=<pending>", pending, StringComparison.Ordinal);

        current.Complete(returnValue: 0x2222);
        Assert.Contains(
            "rax=0x0000000000002222",
            trace.Build(1, prioritizedThreadHandle: null),
            StringComparison.Ordinal);
    }

    [Fact]
    public void LateOlderWriterCannotReplaceANewerSlotPublication()
    {
        var slot = new RecentImportTraceBuffer.Slot();
        slot.Write(sequence: 2, Data(dispatchIndex: 2, threadHandle: 1));

        slot.Write(sequence: 1, Data(dispatchIndex: 1, threadHandle: 1));

        Assert.True(slot.TryRead(
            expectedSequence: 2,
            out var snapshot));
        Assert.Equal(2, snapshot.Data.DispatchIndex);
    }

    [Fact]
    public void ConcurrentWritersRetainABoundedCompletedTail()
    {
        const int capacity = 32;
        var trace = new RecentImportTraceBuffer(capacity);
        long nextDispatchIndex = 0;

        Parallel.For(0, 8, writer =>
        {
            for (var index = 0; index < 1_000; index++)
            {
                var dispatchIndex = Interlocked.Increment(ref nextDispatchIndex);
                trace.Record(Data(dispatchIndex, (ulong)writer + 1))
                    .Complete((ulong)dispatchIndex);
            }
        });

        var snapshot = trace.BuildSnapshot(
            requestedLimit: capacity,
            prioritizedThreadHandle: null);
        Assert.NotNull(snapshot.Entries);
        Assert.InRange(snapshot.Entries.Count, 1, capacity);
        Assert.Equal(
            snapshot.Entries.Count,
            snapshot.Entries.Select(entry => entry.DispatchIndex).Distinct().Count());
        Assert.All(snapshot.Entries, entry => Assert.NotNull(entry.ReturnValue));
    }

    [Fact]
    public void FaultThreadRetainsAReservedSliceWhenGlobalHistoryIsFlooded()
    {
        var trace = new RecentImportTraceBuffer(capacity: 4);
        trace.Record(Data(1, threadHandle: 0));
        trace.Record(Data(2, threadHandle: 0));
        for (var dispatch = 3; dispatch <= 8; dispatch++)
        {
            trace.Record(Data(dispatch, threadHandle: 0xCAFE));
        }

        var formatted = trace.Build(requestedLimit: 4, prioritizedThreadHandle: 0);

        Assert.NotNull(formatted);
        var lines = formatted.Split(Environment.NewLine);
        Assert.Equal(4, lines.Length);
        Assert.Contains("#1 ", formatted, StringComparison.Ordinal);
        Assert.Contains("#2 ", formatted, StringComparison.Ordinal);
        Assert.Contains("#8 ", formatted, StringComparison.Ordinal);
        Assert.Contains("thread=0x0000000000000000", formatted, StringComparison.Ordinal);
        Assert.Contains("thread=0x000000000000CAFE", formatted, StringComparison.Ordinal);
    }

    [Fact]
    public void TraceWithoutPriorityRetainsOnlyRequestedGlobalTail()
    {
        var trace = new RecentImportTraceBuffer(capacity: 2);
        trace.Record(Data(1, threadHandle: 1));
        trace.Record(Data(2, threadHandle: 2));
        trace.Record(Data(3, threadHandle: 3));

        var formatted = trace.Build(requestedLimit: 2, prioritizedThreadHandle: null);

        Assert.NotNull(formatted);
        Assert.DoesNotContain("#1 ", formatted, StringComparison.Ordinal);
        Assert.Contains("#2 ", formatted, StringComparison.Ordinal);
        Assert.Contains("#3 ", formatted, StringComparison.Ordinal);
    }

    [Fact]
    public void CombinedFaultThreadAndGlobalHistoryRemainsChronological()
    {
        var trace = new RecentImportTraceBuffer(capacity: 4);
        trace.Record(Data(1, threadHandle: 1));
        trace.Record(Data(2, threadHandle: 2));
        trace.Record(Data(3, threadHandle: 0xCAFE));
        trace.Record(Data(4, threadHandle: 0xCAFE));

        var formatted = trace.Build(requestedLimit: 4, prioritizedThreadHandle: 0xCAFE);

        Assert.NotNull(formatted);
        var lines = formatted.Split(Environment.NewLine);
        Assert.Equal(4, lines.Length);
        Assert.StartsWith("#1 ", lines[0], StringComparison.Ordinal);
        Assert.StartsWith("#2 ", lines[1], StringComparison.Ordinal);
        Assert.StartsWith("#3 ", lines[2], StringComparison.Ordinal);
        Assert.StartsWith("#4 ", lines[3], StringComparison.Ordinal);
    }

    [Fact]
    public void CompletionAddsReturnValueWithoutAppendingAnotherTraceEntry()
    {
        var trace = new RecentImportTraceBuffer(capacity: 2);
        var first = trace.Record(Data(1, threadHandle: 1));
        trace.Record(Data(2, threadHandle: 1));

        Assert.Contains("rax=<pending>", trace.Build(2, prioritizedThreadHandle: null), StringComparison.Ordinal);

        first.Complete(returnValue: 0x1234);

        var formatted = trace.Build(2, prioritizedThreadHandle: null);
        Assert.NotNull(formatted);
        Assert.Equal(2, formatted.Split(Environment.NewLine).Length);
        Assert.Contains("#1 ", formatted, StringComparison.Ordinal);
        Assert.Contains("rax=0x0000000000001234", formatted, StringComparison.Ordinal);
        Assert.Contains("#2 ", formatted, StringComparison.Ordinal);
        Assert.Contains("rax=<pending>", formatted, StringComparison.Ordinal);
    }

    [Fact]
    public void StructuredSnapshotRetainsTheSameCompletionStateAsFormattedText()
    {
        var trace = new RecentImportTraceBuffer(capacity: 1);
        var entry = trace.Record(Data(1, threadHandle: 1));

        var pending = trace.BuildSnapshot(1, prioritizedThreadHandle: null);
        entry.Complete(returnValue: 0x1234);

        Assert.Contains("rax=<pending>", pending.Formatted, StringComparison.Ordinal);
        Assert.Null(Assert.Single(pending.Entries!).ReturnValue);

        var completed = trace.BuildSnapshot(1, prioritizedThreadHandle: null);
        Assert.Contains("rax=0x0000000000001234", completed.Formatted, StringComparison.Ordinal);
        Assert.Equal(0x1234UL, Assert.Single(completed.Entries!).ReturnValue);
    }

    private static RecentImportTraceData Data(long dispatchIndex, ulong threadHandle) =>
        new(
            dispatchIndex,
            $"nid-{dispatchIndex}",
            LibraryName: "libTest",
            ExportName: "testExport",
            threadHandle,
            ReturnRip: (ulong)dispatchIndex,
            Arg0: 1,
            Arg1: 2,
            Arg2: 3,
            Arg3: 4,
            Arg4: 5,
            Arg5: 6);
}
