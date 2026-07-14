// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.HLE;

/// <summary>
/// Exposes the guest-visible virtual-memory map to HLE query APIs.
/// </summary>
public interface IGuestVirtualMemoryQuery
{
    bool TryQueryMemoryRegion(
        ulong address,
        bool findNext,
        out GuestVirtualMemoryRegion region);
}

public readonly record struct GuestVirtualMemoryRegion(
    ulong Address,
    ulong Length,
    int Protection);
