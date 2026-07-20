// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;

namespace SharpEmu.Libs.Tests;

/// <summary>
/// Minimal in-memory <see cref="ICpuMemory"/> for unit tests: sparse regions
/// backed by byte arrays. Reads and writes must fall entirely inside one region.
/// </summary>
public sealed class FakeGuestMemory : ICpuMemory, IGuestAddressSpace, IGuestStackMemory
{
    private readonly List<(ulong Base, byte[] Data)> _regions = [];
    private readonly List<(ulong Start, ulong End)> _stackRanges = [];
    private readonly HashSet<ulong> _guestAllocations = [];
    private ulong _nextAllocationAddress = 0x0010_0000;

    public int ReadCount { get; private set; }

    public List<ulong> ReadAddresses { get; } = [];

    public int CompareCount { get; private set; }

    public int GuestAllocationCount => _guestAllocations.Count;

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
        _guestAllocations.Remove(baseAddress);
        return true;
    }

    public void RegisterStackRange(ulong start, ulong size)
        => _stackRanges.Add((start, checked(start + size)));

    public bool TryAllocateGuestMemory(ulong size, ulong alignment, out ulong address)
    {
        return TryAllocateAtOrAbove(
            _nextAllocationAddress,
            size,
            executable: false,
            alignment,
            out address);
    }

    public ulong AllocateAt(
        ulong desiredAddress,
        ulong size,
        bool executable = true,
        bool allowAlternative = true)
    {
        _ = executable;
        if (TryAddAllocation(desiredAddress, size))
        {
            return desiredAddress;
        }

        return allowAlternative && TryAllocateGuestMemory(size, 0x1000, out var alternative)
            ? alternative
            : 0;
    }

    public bool TryBackFixedRange(ulong address, ulong size, bool executable)
    {
        _ = executable;
        if (address == 0 || size == 0 || size > int.MaxValue ||
            ulong.MaxValue - address < size)
        {
            return false;
        }

        var end = address + size;
        var cursor = address;
        foreach (var region in _regions
                     .Where(region => region.Base < end)
                     .OrderBy(region => region.Base)
                     .ToArray())
        {
            var regionEnd = checked(region.Base + (ulong)region.Data.Length);
            if (regionEnd <= cursor)
            {
                continue;
            }

            if (region.Base > cursor)
            {
                var gapEnd = Math.Min(region.Base, end);
                if (!TryAddAllocation(cursor, gapEnd - cursor))
                {
                    return false;
                }
            }

            cursor = Math.Max(cursor, regionEnd);
            if (cursor >= end)
            {
                return true;
            }
        }

        if (cursor < end)
        {
            if (!TryAddAllocation(cursor, end - cursor))
            {
                return false;
            }
        }

        return true;
    }

    public bool TryAllocateAtOrAbove(
        ulong desiredAddress,
        ulong size,
        bool executable,
        ulong alignment,
        out ulong actualAddress)
    {
        _ = executable;
        actualAddress = 0;
        if (size == 0 || size > int.MaxValue ||
            alignment == 0 || (alignment & (alignment - 1)) != 0)
        {
            return false;
        }

        try
        {
            var candidate = checked((desiredAddress + alignment - 1) & ~(alignment - 1));
            foreach (var region in _regions.OrderBy(region => region.Base))
            {
                var regionEnd = checked(region.Base + (ulong)region.Data.Length);
                var candidateEnd = checked(candidate + size);
                if (candidateEnd <= region.Base)
                {
                    break;
                }

                if (candidate < regionEnd)
                {
                    candidate = checked((regionEnd + alignment - 1) & ~(alignment - 1));
                }
            }

            if (!TryAddAllocation(candidate, size))
            {
                return false;
            }

            actualAddress = candidate;
            return true;
        }
        catch (OverflowException)
        {
            return false;
        }
    }

    public bool TryProtect(
        ulong address,
        ulong size,
        GuestPageProtection protection)
    {
        _ = protection;
        return size != 0 &&
               size <= int.MaxValue &&
               TryFind(address, checked((int)size), out _, out _);
    }

    public bool TryFreeGuestMemory(ulong address)
    {
        if (!_guestAllocations.Contains(address))
        {
            return false;
        }

        var index = _regions.FindIndex(region => region.Base == address);
        if (index < 0)
        {
            return false;
        }

        _regions.RemoveAt(index);
        _guestAllocations.Remove(address);
        return true;
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
        ReadAddresses.Add(virtualAddress);
        if (!TryFind(virtualAddress, destination.Length, out var data, out var offset))
        {
            return false;
        }

        data.AsSpan(offset, destination.Length).CopyTo(destination);
        return true;
    }

    public void ClearReadHistory()
    {
        ReadCount = 0;
        ReadAddresses.Clear();
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

    private bool TryAddAllocation(ulong address, ulong size)
    {
        if (address == 0 || size == 0 || size > int.MaxValue ||
            ulong.MaxValue - address < size)
        {
            return false;
        }

        var end = address + size;
        foreach (var region in _regions)
        {
            if (ulong.MaxValue - region.Base < (ulong)region.Data.Length)
            {
                return false;
            }

            var regionEnd = region.Base + (ulong)region.Data.Length;
            if (address < regionEnd && region.Base < end)
            {
                return false;
            }
        }

        _regions.Add((address, new byte[(int)size]));
        _guestAllocations.Add(address);
        _nextAllocationAddress = Math.Max(_nextAllocationAddress, end);
        return true;
    }
}
