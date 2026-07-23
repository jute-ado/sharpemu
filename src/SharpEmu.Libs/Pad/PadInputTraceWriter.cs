// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace SharpEmu.Libs.Pad;

internal sealed class PadInputTraceWriter : IDisposable
{
    internal const int ProtocolVersion = 1;
    private const int MaximumSamples = 1_000_000;
    private const long MaximumElapsedMilliseconds = 7L * 24 * 60 * 60 * 1000;
    private static readonly (uint Mask, string Name)[] CanonicalButtons =
    [
        (OrbisPadButton.Up, "dpad_up"),
        (OrbisPadButton.Right, "dpad_right"),
        (OrbisPadButton.Down, "dpad_down"),
        (OrbisPadButton.Left, "dpad_left"),
        (OrbisPadButton.Square, "square"),
        (OrbisPadButton.Cross, "cross"),
        (OrbisPadButton.Circle, "circle"),
        (OrbisPadButton.Triangle, "triangle"),
        (OrbisPadButton.L1, "l1"),
        (OrbisPadButton.L2, "l2"),
        (OrbisPadButton.R1, "r1"),
        (OrbisPadButton.R2, "r2"),
        (OrbisPadButton.L3, "l3"),
        (OrbisPadButton.R3, "r3"),
        (OrbisPadButton.Options, "options"),
        (OrbisPadButton.TouchPad, "touchpad"),
    ];

    private readonly object _sync = new();
    private readonly FileStream _stream;
    private readonly long _startTimestamp;
    private PadState? _previous;
    private long _previousOffset = -1;
    private int _sampleCount;
    private bool _disposed;

    private PadInputTraceWriter(FileStream stream, long startTimestamp)
    {
        _stream = stream;
        _startTimestamp = startTimestamp;
        WriteHeader();
    }

    internal static PadInputTraceWriter Create(
        string path,
        long startTimestamp)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var fullPath = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(fullPath);
        if (directory is null || !Directory.Exists(directory))
        {
            throw new DirectoryNotFoundException(
                "Input trace directory does not exist.");
        }

        var stream = new FileStream(
            fullPath,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.Read,
            bufferSize: 64 * 1024,
            FileOptions.WriteThrough);
        try
        {
            return new PadInputTraceWriter(stream, startTimestamp);
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }

    internal void Record(PadState state, long timestamp)
    {
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_previous == state)
            {
                return;
            }
            if (!state.Connected)
            {
                throw new InvalidOperationException(
                    "Disconnected pad states cannot be represented by route schema v1.");
            }
            if (_sampleCount == MaximumSamples)
            {
                throw new InvalidOperationException(
                    $"Input trace exceeds {MaximumSamples} samples.");
            }

            var offset = ElapsedMilliseconds(timestamp);
            if (offset <= _previousOffset)
            {
                offset = checked(_previousOffset + 1);
            }
            if (offset > MaximumElapsedMilliseconds)
            {
                throw new InvalidOperationException(
                    "Input trace exceeds the seven-day route bound.");
            }

            WriteSample(offset, state);
            _previous = state;
            _previousOffset = offset;
            _sampleCount++;
        }
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _stream.Flush(flushToDisk: true);
            _stream.Dispose();
        }
    }

    private long ElapsedMilliseconds(long timestamp)
    {
        var elapsed = Math.Max(0, timestamp - _startTimestamp);
        return checked(
            (elapsed / Stopwatch.Frequency * 1000) +
            (elapsed % Stopwatch.Frequency * 1000 / Stopwatch.Frequency));
    }

    private void WriteHeader()
    {
        WriteJson(
            writer =>
            {
                writer.WriteStartObject();
                writer.WriteString("kind", "header");
                writer.WriteNumber("protocolVersion", ProtocolVersion);
                writer.WriteString("profile", "dualsense");
                writer.WriteString("clock", "elapsed_milliseconds");
                writer.WriteEndObject();
            });
    }

    private void WriteSample(long offset, PadState state)
    {
        WriteJson(
            writer =>
            {
                writer.WriteStartObject();
                writer.WriteString("kind", "sample");
                writer.WriteNumber("protocolVersion", ProtocolVersion);
                writer.WriteNumber("offset", offset);
                writer.WritePropertyName("state");
                writer.WriteStartObject();
                writer.WritePropertyName("buttons");
                writer.WriteStartArray();
                foreach (var (mask, name) in CanonicalButtons)
                {
                    if ((state.Buttons & mask) != 0)
                    {
                        writer.WriteStringValue(name);
                    }
                }
                writer.WriteEndArray();
                writer.WriteNumber("leftX", NormalizeStick(state.LeftX));
                writer.WriteNumber("leftY", NormalizeStick(state.LeftY));
                writer.WriteNumber("rightX", NormalizeStick(state.RightX));
                writer.WriteNumber("rightY", NormalizeStick(state.RightY));
                writer.WriteNumber("leftTrigger", state.L2 * 257);
                writer.WriteNumber("rightTrigger", state.R2 * 257);
                writer.WriteStartArray("touches");
                writer.WriteEndArray();
                writer.WriteEndObject();
                writer.WriteEndObject();
            });
    }

    private void WriteJson(Action<Utf8JsonWriter> write)
    {
        using (var writer = new Utf8JsonWriter(
                   _stream,
                   new JsonWriterOptions { Indented = false }))
        {
            write(writer);
            writer.Flush();
        }
        _stream.WriteByte((byte)'\n');
        _stream.Flush(flushToDisk: true);
    }

    private static int NormalizeStick(byte value)
    {
        if (value <= 128)
        {
            return (value - 128) * 256;
        }

        var delta = value - 128;
        return (delta * 32_767 + 63) / 127;
    }
}
