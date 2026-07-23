// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Text.Json;
using System.Diagnostics;
using SharpEmu.Libs.Pad;
using Xunit;

namespace SharpEmu.Libs.Tests;

public sealed class PadInputTraceWriterTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), $"sharpemu-input-trace-{Guid.NewGuid():N}");

    [Fact]
    public void WritesProtocolHeaderAndOnlyChangedCompleteStates()
    {
        var path = Path.Combine(_root, "input.trace.jsonl");
        Directory.CreateDirectory(_root);
        using (var writer = PadInputTraceWriter.Create(path, startTimestamp: 1_000))
        {
            writer.Record(Neutral(), timestamp: 1_000);
            writer.Record(Neutral(), timestamp: 2_000);
            writer.Record(
                new PadState(
                    Connected: true,
                    Buttons:
                        OrbisPadButton.Right |
                        OrbisPadButton.Cross |
                        OrbisPadButton.L2,
                    LeftX: 0,
                    LeftY: 128,
                    RightX: 255,
                    RightY: 127,
                    L2: 255,
                    R2: 128),
                timestamp: 1_000 + Stopwatch.Frequency);
        }

        var lines = File.ReadAllLines(path);
        Assert.Equal(3, lines.Length);
        using var header = JsonDocument.Parse(lines[0]);
        Assert.Equal("header", header.RootElement.GetProperty("kind").GetString());
        Assert.Equal(1, header.RootElement.GetProperty("protocolVersion").GetInt32());
        Assert.Equal("dualsense", header.RootElement.GetProperty("profile").GetString());
        Assert.Equal(
            "elapsed_milliseconds",
            header.RootElement.GetProperty("clock").GetString());

        using var changed = JsonDocument.Parse(lines[2]);
        Assert.Equal(1000, changed.RootElement.GetProperty("offset").GetInt64());
        var state = changed.RootElement.GetProperty("state");
        Assert.Equal(
            ["dpad_right", "cross", "l2"],
            state.GetProperty("buttons")
                .EnumerateArray()
                .Select(item => item.GetString()!)
                .ToArray());
        Assert.Equal(-32_768, state.GetProperty("leftX").GetInt32());
        Assert.Equal(0, state.GetProperty("leftY").GetInt32());
        Assert.Equal(32_767, state.GetProperty("rightX").GetInt32());
        Assert.Equal(-256, state.GetProperty("rightY").GetInt32());
        Assert.Equal(65_535, state.GetProperty("leftTrigger").GetInt32());
        Assert.Equal(32_896, state.GetProperty("rightTrigger").GetInt32());
        Assert.Empty(state.GetProperty("touches").EnumerateArray());
    }

    [Fact]
    public void PreservesStrictOffsetsForMultipleChangesWithinOneMillisecond()
    {
        var path = Path.Combine(_root, "fast.trace.jsonl");
        Directory.CreateDirectory(_root);
        using (var writer = PadInputTraceWriter.Create(path, startTimestamp: 10_000))
        {
            writer.Record(Neutral(), timestamp: 10_000);
            writer.Record(
                Neutral() with { Buttons = OrbisPadButton.Cross },
                timestamp: 10_001);
            writer.Record(
                Neutral() with { Buttons = 0 },
                timestamp: 10_002);
        }

        var offsets = File.ReadAllLines(path)
            .Skip(1)
            .Select(line =>
            {
                using var json = JsonDocument.Parse(line);
                return json.RootElement.GetProperty("offset").GetInt64();
            })
            .ToArray();

        Assert.Equal([0L, 1L, 2L], offsets);
    }

    [Fact]
    public void RefusesToOverwriteAnExistingTrace()
    {
        Directory.CreateDirectory(_root);
        var path = Path.Combine(_root, "existing.trace.jsonl");
        File.WriteAllText(path, "keep");

        Assert.Throws<IOException>(
            () => PadInputTraceWriter.Create(path, startTimestamp: 0));
        Assert.Equal("keep", File.ReadAllText(path));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private static PadState Neutral() =>
        new(
            Connected: true,
            Buttons: 0,
            LeftX: 128,
            LeftY: 128,
            RightX: 128,
            RightY: 128,
            L2: 0,
            R2: 0);
}
