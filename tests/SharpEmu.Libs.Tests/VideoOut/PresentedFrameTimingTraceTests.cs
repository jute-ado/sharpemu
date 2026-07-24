// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Text.Json;
using SharpEmu.Libs.VideoOut;
using Xunit;

namespace SharpEmu.Libs.Tests.VideoOut;

public sealed class PresentedFrameTimingTraceTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        $"sharpemu-performance-trace-{Guid.NewGuid():N}");

    public PresentedFrameTimingTraceTests() => Directory.CreateDirectory(_root);

    [Theory]
    [InlineData("100", "31", 100, 31)]
    [InlineData("0", "1000001", 0, 1_000_001)]
    public void ParsesBoundedTraceWindow(
        string start,
        string count,
        long expectedStart,
        int expectedCount)
    {
        Assert.True(
            PresentedFrameTimingTraceRequest.TryParse(
                "trace.jsonl",
                start,
                count,
                out var request));

        Assert.Equal(expectedStart, request.StartFrame);
        Assert.Equal(expectedCount, request.SampleCount);
    }

    [Theory]
    [InlineData(null, "1", "2")]
    [InlineData("trace.jsonl", "-1", "2")]
    [InlineData("trace.jsonl", "1", "1")]
    [InlineData("trace.jsonl", "1", "1000002")]
    [InlineData("trace.jsonl", "one", "2")]
    public void RejectsIncompleteOrUnboundedTraceWindow(
        string? path,
        string start,
        string count)
    {
        Assert.False(
            PresentedFrameTimingTraceRequest.TryParse(
                path,
                start,
                count,
                out _));
    }

    [Fact]
    public void WritesOnlyRequestedPresentedFramesAndFlushesFinalSample()
    {
        var path = Path.Combine(_root, "trace.jsonl");
        using (var trace = PresentedFrameTimingTrace.CreateNew(
                   new PresentedFrameTimingTraceRequest(path, 10, 3)))
        {
            trace.Record(9, 900);
            trace.Record(10, 1_000);
            trace.Record(11, 1_100);
            trace.Record(12, 1_200);
            trace.Record(13, 1_300);
        }

        var lines = File.ReadAllLines(path);
        Assert.Equal(4, lines.Length);
        using var header = JsonDocument.Parse(lines[0]);
        Assert.Equal("header", header.RootElement.GetProperty("kind").GetString());
        Assert.Equal(
            "presented_frame",
            header.RootElement.GetProperty("source").GetString());
        Assert.Equal(
            [10L, 11L, 12L],
            lines.Skip(1)
                .Select(line => JsonDocument.Parse(line))
                .Select(
                    document =>
                    {
                        using (document)
                        {
                            return document.RootElement
                                .GetProperty("presentedFrame")
                                .GetInt64();
                        }
                    }));
    }

    public void Dispose()
    {
        Directory.Delete(_root, recursive: true);
        GC.SuppressFinalize(this);
    }
}
