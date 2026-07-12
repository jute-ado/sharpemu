// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;

namespace SharpEmu.Libs.Tests;

/// <summary>
/// Minimal in-memory <see cref="ICpuMemory"/> for unit tests: sparse regions
/// backed by byte arrays. Reads and writes must fall entirely inside one region.
/// </summary>
public sealed class FakeGuestMemory : ICpuMemory
{
    private readonly List<(ulong Base, byte[] Data)> _regions = [];

    public void AddRegion(ulong baseAddress, byte[] data)
        => _regions.Add((baseAddress, data));

    public bool TryRead(ulong virtualAddress, Span<byte> destination)
    {
        if (!TryFind(virtualAddress, destination.Length, out var data, out var offset))
        {
            return false;
        }

        data.AsSpan(offset, destination.Length).CopyTo(destination);
        return true;
    }

    public bool TryWrite(ulong virtualAddress, ReadOnlySpan<byte> source)
    {
        if (!TryFind(virtualAddress, source.Length, out var data, out var offset))
        {
            return false;
        }

        source.CopyTo(data.AsSpan(offset, source.Length));
        return true;
    }

    private bool TryFind(ulong address, int length, out byte[] data, out int offset)
    {
        foreach (var (regionBase, regionData) in _regions)
        {
            if (address < regionBase)
            {
                continue;
            }

            var regionOffset = address - regionBase;
            if (regionOffset <= (ulong)regionData.Length &&
                (ulong)length <= (ulong)regionData.Length - regionOffset)
            {
                data = regionData;
                offset = checked((int)regionOffset);
                return true;
            }
        }

        data = [];
        offset = 0;
        return false;
    }
}
