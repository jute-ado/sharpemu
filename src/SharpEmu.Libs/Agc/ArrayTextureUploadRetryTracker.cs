// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Collections.Concurrent;

namespace SharpEmu.Libs.Agc;

internal sealed class ArrayTextureUploadRetryTracker
{
    private readonly ConcurrentDictionary<ulong, byte> _unsupported = new();

    public bool ShouldAttempt(ulong address) =>
        !_unsupported.ContainsKey(address);

    public void MarkUnsupported(ulong address) =>
        _unsupported.TryAdd(address, 0);
}
