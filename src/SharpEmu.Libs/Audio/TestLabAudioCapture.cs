// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.Libs.Audio;

internal sealed class Pcm16CaptureFile : IDisposable
{
    private readonly FileStream _stream;
    private readonly object _gate = new();

    public Pcm16CaptureFile(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var fullPath = Path.GetFullPath(path);
        Directory.CreateDirectory(
            Path.GetDirectoryName(fullPath)
            ?? throw new ArgumentException(
                "Capture path has no parent directory.",
                nameof(path)));
        _stream = new FileStream(
            fullPath,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.Read,
            bufferSize: 64 * 1024,
            FileOptions.SequentialScan);
    }

    public void Append(ReadOnlySpan<byte> stereoPcm16)
    {
        if (stereoPcm16.Length % AudioPcmConversion.OutputFrameSize != 0)
        {
            throw new ArgumentException(
                "Stereo PCM16 submissions must contain complete frames.",
                nameof(stereoPcm16));
        }

        lock (_gate)
        {
            _stream.Write(stereoPcm16);
            _stream.Flush();
        }
    }

    public void Dispose() => _stream.Dispose();
}

internal static class TestLabAudioCapture
{
    public const string EnvironmentVariable =
        "EMULATOR_TEST_LAB_AUDIO_PCM16";

    private static readonly Lazy<Pcm16CaptureFile?> Capture = new(
        CreateFromEnvironment,
        LazyThreadSafetyMode.ExecutionAndPublication);

    public static bool IsEnabled =>
        !string.IsNullOrWhiteSpace(
            Environment.GetEnvironmentVariable(EnvironmentVariable));

    public static void Append(ReadOnlySpan<byte> stereoPcm16)
    {
        Capture.Value?.Append(stereoPcm16);
    }

    private static Pcm16CaptureFile? CreateFromEnvironment()
    {
        var path = Environment.GetEnvironmentVariable(EnvironmentVariable);
        return string.IsNullOrWhiteSpace(path)
            ? null
            : new Pcm16CaptureFile(path);
    }
}
