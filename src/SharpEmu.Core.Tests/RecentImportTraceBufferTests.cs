// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Core.Cpu.Native;
using Xunit;

namespace SharpEmu.Core.Tests;

public sealed class RecentImportTraceBufferTests
{
    [Fact]
    public void FaultThreadRetainsAReservedSliceWhenGlobalHistoryIsFlooded()
    {
        var trace = new RecentImportTraceBuffer(capacity: 4);
        trace.Record(Entry(1, threadHandle: 0));
        trace.Record(Entry(2, threadHandle: 0));
        for (var dispatch = 3; dispatch <= 8; dispatch++)
        {
            trace.Record(Entry(dispatch, threadHandle: 0xCAFE));
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
        trace.Record(Entry(1, threadHandle: 1));
        trace.Record(Entry(2, threadHandle: 2));
        trace.Record(Entry(3, threadHandle: 3));

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
        trace.Record(Entry(1, threadHandle: 1));
        trace.Record(Entry(2, threadHandle: 2));
        trace.Record(Entry(3, threadHandle: 0xCAFE));
        trace.Record(Entry(4, threadHandle: 0xCAFE));

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
        var first = Entry(1, threadHandle: 1);
        var second = Entry(2, threadHandle: 1);
        trace.Record(first);
        trace.Record(second);

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
        var entry = Entry(1, threadHandle: 1);
        trace.Record(entry);

        var pending = trace.BuildSnapshot(1, prioritizedThreadHandle: null);
        entry.Complete(returnValue: 0x1234);

        Assert.Contains("rax=<pending>", pending.Formatted, StringComparison.Ordinal);
        Assert.Null(Assert.Single(pending.Entries!).ReturnValue);

        var completed = trace.BuildSnapshot(1, prioritizedThreadHandle: null);
        Assert.Contains("rax=0x0000000000001234", completed.Formatted, StringComparison.Ordinal);
        Assert.Equal(0x1234UL, Assert.Single(completed.Entries!).ReturnValue);
    }

    private static RecentImportTraceEntry Entry(long dispatchIndex, ulong threadHandle) =>
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
