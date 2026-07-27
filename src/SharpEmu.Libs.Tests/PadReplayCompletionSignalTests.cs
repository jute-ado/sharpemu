// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.Pad;
using Xunit;

namespace SharpEmu.Libs.Tests;

public sealed class PadReplayCompletionSignalTests : IDisposable
{
    private readonly string _root =
        Path.Combine(
            Path.GetTempPath(),
            $"sharpemu-replay-completion-{Guid.NewGuid():N}");

    [Fact]
    public void WritesOneBoundedCompletionRecord()
    {
        Directory.CreateDirectory(_root);
        var path = Path.Combine(_root, "route.complete");
        var signal = PadReplayCompletionSignal.Create(path);

        Assert.True(signal.TryComplete());
        Assert.True(signal.TryComplete());

        Assert.Equal("complete\n", File.ReadAllText(path));
    }

    [Fact]
    public void MissingParentFallsBackWithoutThrowing()
    {
        var path = Path.Combine(_root, "missing", "route.complete");
        var signal = PadReplayCompletionSignal.Create(path);

        Assert.False(signal.TryComplete());
        Assert.False(signal.TryComplete());
        Assert.False(File.Exists(path));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("relative.complete")]
    public void RejectsMissingOrRelativePaths(string? path)
    {
        Assert.ThrowsAny<ArgumentException>(
            () => PadReplayCompletionSignal.Create(path!));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
        GC.SuppressFinalize(this);
    }
}
