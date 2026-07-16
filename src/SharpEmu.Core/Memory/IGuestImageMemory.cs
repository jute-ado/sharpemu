// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.Core.Memory;

/// <summary>
/// Tracks the mapped regions that belong to each loaded guest image.
/// </summary>
public interface IGuestImageMemory
{
    void RegisterImage(IReadOnlyList<VirtualMemoryRegion> regions);

    bool TryGetImageRegions(
        ulong address,
        out IReadOnlyList<VirtualMemoryRegion> regions);
}
