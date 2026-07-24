// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace SharpEmu.Libs.VideoOut;

internal readonly record struct PresentedFrameTimingTraceRequest(
    string Path,
    long StartFrame,
    int SampleCount)
{
    private const long MaximumPresentedFrame = 50_000_000;
    private const int MaximumSampleCount = 1_000_001;

    public static bool TryParse(
        string? path,
        string? startFrame,
        string? sampleCount,
        out PresentedFrameTimingTraceRequest request)
    {
        request = default;
        if (string.IsNullOrWhiteSpace(path) ||
            !long.TryParse(
                startFrame,
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out var start) ||
            !int.TryParse(
                sampleCount,
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out var count) ||
            start is < 1 or > MaximumPresentedFrame ||
            count is < 2 or > MaximumSampleCount ||
            start + count - 1 > MaximumPresentedFrame)
        {
            return false;
        }

        request = new PresentedFrameTimingTraceRequest(path, start, count);
        return true;
    }
}

internal sealed class PresentedFrameTimingTrace : IDisposable
{
    private const string Header =
        "{\"kind\":\"header\",\"protocolVersion\":1," +
        "\"source\":\"presented_frame\"," +
        "\"clock\":\"monotonic_nanoseconds\"}";

    private readonly StreamWriter _writer;
    private readonly long _startFrame;
    private readonly long _finalFrame;
    private long _lastWrittenFrame = -1;
    private bool _completed;
    private bool _disposed;

    private PresentedFrameTimingTrace(
        StreamWriter writer,
        PresentedFrameTimingTraceRequest request)
    {
        _writer = writer;
        _startFrame = request.StartFrame;
        _finalFrame = checked(request.StartFrame + request.SampleCount - 1);
        _writer.WriteLine(Header);
    }

    public static PresentedFrameTimingTrace CreateNew(
        PresentedFrameTimingTraceRequest request)
    {
        var stream = new FileStream(
            request.Path,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.Read,
            bufferSize: 64 * 1024,
            FileOptions.SequentialScan);
        try
        {
            var writer = new StreamWriter(
                stream,
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
                bufferSize: 64 * 1024,
                leaveOpen: false)
            {
                NewLine = "\n",
            };
            return new PresentedFrameTimingTrace(writer, request);
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }

    public static PresentedFrameTimingTrace? TryCreateFromEnvironment()
    {
        var path = Environment.GetEnvironmentVariable(
            "SHARPEMU_PERFORMANCE_TRACE_PATH");
        var start = Environment.GetEnvironmentVariable(
            "SHARPEMU_PERFORMANCE_TRACE_START_FRAME");
        var count = Environment.GetEnvironmentVariable(
            "SHARPEMU_PERFORMANCE_TRACE_SAMPLE_COUNT");
        if (path is null && start is null && count is null)
        {
            return null;
        }
        if (!PresentedFrameTimingTraceRequest.TryParse(
                path,
                start,
                count,
                out var request))
        {
            Console.Error.WriteLine(
                "[LOADER][ERROR] Invalid SharpEmu performance trace request.");
            return null;
        }

        try
        {
            return CreateNew(request);
        }
        catch (Exception exception)
            when (exception is IOException or UnauthorizedAccessException or
                  ArgumentException or NotSupportedException)
        {
            Console.Error.WriteLine(
                $"[LOADER][ERROR] Cannot create SharpEmu performance trace: " +
                $"{exception.GetType().Name}.");
            return null;
        }
    }

    public void Record(long presentedFrame, long timestampNanoseconds)
    {
        if (_disposed ||
            _completed ||
            presentedFrame < _startFrame ||
            presentedFrame > _finalFrame)
        {
            return;
        }
        if (_lastWrittenFrame >= 0 &&
            presentedFrame != _lastWrittenFrame + 1)
        {
            return;
        }

        _writer.Write(
            "{\"kind\":\"sample\",\"protocolVersion\":1," +
            $"\"presentedFrame\":{presentedFrame.ToString(CultureInfo.InvariantCulture)}," +
            $"\"timestampNanoseconds\":{timestampNanoseconds.ToString(CultureInfo.InvariantCulture)}}}");
        _writer.WriteLine();
        _lastWrittenFrame = presentedFrame;
        if (presentedFrame == _finalFrame)
        {
            _writer.Flush();
            _completed = true;
        }
    }

    public static long GetMonotonicNanoseconds() =>
        checked(Stopwatch.GetElapsedTime(0, Stopwatch.GetTimestamp()).Ticks * 100);

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _writer.Dispose();
        _disposed = true;
    }
}
