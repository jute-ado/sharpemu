// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;

namespace SharpEmu.Libs.Tests;

/// <summary>
/// Minimal in-memory <see cref="ICpuMemory"/> for unit tests: sparse regions
/// backed by byte arrays. Reads and writes must fall entirely inside one region.
/// </summary>
public sealed class FakeGuestMemory : ICpuMemory, IGuestMemoryAllocator, IGuestStackMemory
{
    private readonly List<(ulong Base, byte[] Data)> _regions = [];
    private readonly List<(ulong Start, ulong End)> _stackRanges = [];
    private ulong _nextAllocationAddress = 0x0010_0000;

    public int ReadCount { get; private set; }

    public int CompareCount { get; private set; }

    public void AddRegion(ulong baseAddress, byte[] data)
        => _regions.Add((baseAddress, data));

    public bool RemoveRegion(ulong baseAddress)
    {
        var index = _regions.FindIndex(region => region.Base == baseAddress);
        if (index < 0)
        {
            return false;
        }

        _regions.RemoveAt(index);
        return true;
    }

    public void RegisterStackRange(ulong start, ulong size)
        => _stackRanges.Add((start, checked(start + size)));

    public bool TryAllocateGuestMemory(ulong size, ulong alignment, out ulong address)
    {
        address = 0;
        if (size == 0 || size > int.MaxValue ||
            alignment == 0 || (alignment & (alignment - 1)) != 0)
        {
            return false;
        }

        try
        {
            var alignedAddress = checked(
                (_nextAllocationAddress + alignment - 1) & ~(alignment - 1));
            _nextAllocationAddress = checked(alignedAddress + size);
            _regions.Add((alignedAddress, new byte[(int)size]));
            address = alignedAddress;
            return true;
        }
        catch (OverflowException)
        {
            return false;
        }
    }

    public bool TryGetStackRange(ulong address, out ulong start, out ulong end)
    {
        foreach (var range in _stackRanges)
        {
            if (address >= range.Start && address < range.End)
            {
                start = range.Start;
                end = range.End;
                return true;
            }
        }

        start = 0;
        end = 0;
        return false;
    }

    public bool TryRead(ulong virtualAddress, Span<byte> destination)
    {
        ReadCount++;
        if (!TryFind(virtualAddress, destination.Length, out var data, out var offset))
        {
            return false;
        }

        data.AsSpan(offset, destination.Length).CopyTo(destination);
        return true;
    }

    public bool TryCompare(ulong virtualAddress, ReadOnlySpan<byte> expected)
    {
        CompareCount++;
        return TryFind(virtualAddress, expected.Length, out var data, out var offset) &&
               data.AsSpan(offset, expected.Length).SequenceEqual(expected);
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
        if (length != 0 && (ulong)(length - 1) > ulong.MaxValue - address)
        {
            data = [];
            offset = 0;
            return false;
        }

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
