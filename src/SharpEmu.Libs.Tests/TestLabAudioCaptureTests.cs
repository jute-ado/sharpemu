// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.Audio;
using Xunit;

namespace SharpEmu.Libs.Tests;

public sealed class TestLabAudioCaptureTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "sharpemu-audio-capture-tests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public void CreatesAppendOnlyPcmAndFlushesEverySubmission()
    {
        var path = Path.Combine(_root, "captures", "audio.s16le");
        using var capture = new Pcm16CaptureFile(path);

        capture.Append([1, 2, 3, 4]);
        Assert.Equal([1, 2, 3, 4], ReadShared(path));

        capture.Append([5, 6, 7, 8]);
        Assert.Equal([1, 2, 3, 4, 5, 6, 7, 8], ReadShared(path));
    }

    [Fact]
    public void RefusesToOverwriteExistingEvidence()
    {
        Directory.CreateDirectory(_root);
        var path = Path.Combine(_root, "audio.s16le");
        File.WriteAllBytes(path, [9, 9, 9, 9]);

        Assert.Throws<IOException>(() => new Pcm16CaptureFile(path));
        Assert.Equal([9, 9, 9, 9], File.ReadAllBytes(path));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private static byte[] ReadShared(string path)
    {
        using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite);
        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        return memory.ToArray();
    }
}
