// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.Core.Memory;

internal sealed class GuestRangeAllocator
{
    private readonly Dictionary<ulong, ulong> _allocations = new();
    private readonly List<FreeRange> _freeRanges = new();
    private readonly List<ArenaRange> _arenas = new();
    private ulong _currentArenaBase;
    private ulong _currentArenaSize;
    private ulong _currentArenaOffset;
    private bool _hasCurrentArena;

    public bool TryAddArena(ulong address, ulong size, ulong startOffset)
    {
        if (size == 0 || startOffset > size || address > ulong.MaxValue - size)
        {
            return false;
        }

        var end = address + size;
        var insertIndex = 0;
        while (insertIndex < _arenas.Count && _arenas[insertIndex].Address < address)
        {
            insertIndex++;
        }

        if ((insertIndex > 0 && _arenas[insertIndex - 1].End > address) ||
            (insertIndex < _arenas.Count && _arenas[insertIndex].Address < end))
        {
            return false;
        }

        if (_hasCurrentArena && _currentArenaOffset < _currentArenaSize)
        {
            InsertFreeRange(
                _currentArenaBase + _currentArenaOffset,
                _currentArenaSize - _currentArenaOffset);
        }

        _arenas.Insert(insertIndex, new ArenaRange(address, end));
        _currentArenaBase = address;
        _currentArenaSize = size;
        _currentArenaOffset = startOffset;
        _hasCurrentArena = true;
        return true;
    }

    public bool TryAllocate(ulong size, ulong alignment, out ulong address)
    {
        address = 0;
        if (size == 0 || alignment == 0 || (alignment & (alignment - 1)) != 0)
        {
            return false;
        }

        if (TryAllocateFromFreeRanges(size, alignment, out address))
        {
            _allocations.Add(address, size);
            return true;
        }

        if (!_hasCurrentArena)
        {
            return false;
        }

        ulong nextAddress;
        ulong alignedAddress;
        ulong alignedOffset;
        try
        {
            nextAddress = checked(_currentArenaBase + _currentArenaOffset);
            alignedAddress = AlignUp(nextAddress, alignment);
            alignedOffset = checked(alignedAddress - _currentArenaBase);
        }
        catch (OverflowException)
        {
            return false;
        }

        if (alignedOffset > _currentArenaSize ||
            size > _currentArenaSize - alignedOffset)
        {
            return false;
        }

        if (alignedAddress > nextAddress)
        {
            InsertFreeRange(nextAddress, alignedAddress - nextAddress);
        }

        _currentArenaOffset = alignedOffset + size;
        _allocations.Add(alignedAddress, size);
        address = alignedAddress;
        return true;
    }

    public bool TryFree(ulong address)
    {
        if (!_allocations.Remove(address, out var size))
        {
            return false;
        }

        if (TryRewindCurrentArena(address, size))
        {
            return true;
        }

        InsertFreeRange(address, size);
        return true;
    }

    public void Clear()
    {
        _allocations.Clear();
        _freeRanges.Clear();
        _arenas.Clear();
        _currentArenaBase = 0;
        _currentArenaSize = 0;
        _currentArenaOffset = 0;
        _hasCurrentArena = false;
    }

    private bool TryAllocateFromFreeRanges(
        ulong size,
        ulong alignment,
        out ulong address)
    {
        address = 0;
        for (var index = 0; index < _freeRanges.Count; index++)
        {
            var range = _freeRanges[index];
            ulong alignedAddress;
            try
            {
                alignedAddress = AlignUp(range.Address, alignment);
            }
            catch (OverflowException)
            {
                continue;
            }

            if (alignedAddress < range.Address ||
                alignedAddress > range.End ||
                size > range.End - alignedAddress)
            {
                continue;
            }

            _freeRanges.RemoveAt(index);
            var prefixSize = alignedAddress - range.Address;
            var allocationEnd = alignedAddress + size;
            var suffixSize = range.End - allocationEnd;
            if (suffixSize != 0)
            {
                _freeRanges.Insert(index, new FreeRange(allocationEnd, suffixSize));
            }
            if (prefixSize != 0)
            {
                _freeRanges.Insert(index, new FreeRange(range.Address, prefixSize));
            }

            address = alignedAddress;
            return true;
        }

        return false;
    }

    private void InsertFreeRange(ulong address, ulong size)
    {
        var start = address;
        var end = checked(address + size);
        var index = 0;
        while (index < _freeRanges.Count)
        {
            var range = _freeRanges[index];
            if (range.End < start)
            {
                index++;
                continue;
            }

            if (range.Address > end)
            {
                break;
            }

            start = Math.Min(start, range.Address);
            end = Math.Max(end, range.End);
            _freeRanges.RemoveAt(index);
        }

        _freeRanges.Insert(index, new FreeRange(start, end - start));
    }

    private bool TryRewindCurrentArena(ulong address, ulong size)
    {
        if (!_hasCurrentArena ||
            address < _currentArenaBase ||
            size > _currentArenaSize ||
            address - _currentArenaBase > _currentArenaSize - size ||
            address + size != _currentArenaBase + _currentArenaOffset)
        {
            return false;
        }

        var newCursor = address;
        while (_freeRanges.Count != 0)
        {
            var precedingIndex = FindFreeRangeBefore(newCursor);
            if (precedingIndex < 0 ||
                _freeRanges[precedingIndex].Address < _currentArenaBase ||
                _freeRanges[precedingIndex].End != newCursor)
            {
                break;
            }

            newCursor = _freeRanges[precedingIndex].Address;
            _freeRanges.RemoveAt(precedingIndex);
        }

        _currentArenaOffset = newCursor - _currentArenaBase;
        return true;
    }

    private int FindFreeRangeBefore(ulong address)
    {
        var low = 0;
        var high = _freeRanges.Count;
        while (low < high)
        {
            var middle = low + ((high - low) >> 1);
            if (_freeRanges[middle].Address < address)
            {
                low = middle + 1;
            }
            else
            {
                high = middle;
            }
        }

        return low - 1;
    }

    private static ulong AlignUp(ulong value, ulong alignment)
    {
        var mask = alignment - 1;
        return checked((value + mask) & ~mask);
    }

    private readonly record struct ArenaRange(ulong Address, ulong End);

    private readonly record struct FreeRange(ulong Address, ulong Size)
    {
        public ulong End => Address + Size;
    }
}
