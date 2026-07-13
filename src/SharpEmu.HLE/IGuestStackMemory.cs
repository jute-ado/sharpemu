// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.HLE;

/// <summary>
/// Tracks guest virtual-memory ranges that were mapped as thread stacks.
/// </summary>
public interface IGuestStackMemory
{
    void RegisterStackRange(ulong start, ulong size);

    bool TryGetStackRange(ulong address, out ulong start, out ulong end);
}
