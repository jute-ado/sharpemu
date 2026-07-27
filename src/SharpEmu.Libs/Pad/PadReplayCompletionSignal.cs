// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Text;

namespace SharpEmu.Libs.Pad;

internal sealed class PadReplayCompletionSignal
{
    private static readonly byte[] Contents =
        Encoding.UTF8.GetBytes("complete\n");

    private readonly object _gate = new();
    private readonly string _path;
    private bool _completed;
    private bool _failed;

    private PadReplayCompletionSignal(string path)
    {
        _path = path;
    }

    public static PadReplayCompletionSignal Create(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!Path.IsPathFullyQualified(path))
        {
            throw new ArgumentException(
                "Replay completion path must be fully qualified.",
                nameof(path));
        }
        return new PadReplayCompletionSignal(Path.GetFullPath(path));
    }

    public bool TryComplete()
    {
        lock (_gate)
        {
            if (_completed)
            {
                return true;
            }
            if (_failed)
            {
                return false;
            }

            try
            {
                using var stream = new FileStream(
                    _path,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.Read,
                    bufferSize: 4096,
                    FileOptions.WriteThrough);
                stream.Write(Contents);
                stream.Flush(flushToDisk: true);
                _completed = true;
                return true;
            }
            catch (Exception exception)
                when (exception is IOException or
                      UnauthorizedAccessException)
            {
                _failed = true;
                return false;
            }
        }
    }
}
