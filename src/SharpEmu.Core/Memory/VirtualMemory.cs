// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Core.Loader;
using SharpEmu.HLE;

namespace SharpEmu.Core.Memory;

public sealed class VirtualMemory : IVirtualMemory, IGuestStackMemory, IGuestVirtualMemoryQuery
{
    private readonly object _gate = new();
    private readonly List<MappedRegion> _regions = new();
    private readonly List<StackRange> _stackRanges = new();
    private ulong _resetVersion = 1;

    public ulong ResetVersion
    {
        get
        {
            lock (_gate)
            {
                return _resetVersion;
            }
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            _regions.Clear();
            _stackRanges.Clear();
            _resetVersion++;
            if (_resetVersion == 0)
            {
                _resetVersion = 1;
            }
        }
    }

    public void RegisterStackRange(ulong start, ulong size)
    {
        if (size == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(size));
        }

        var end = checked(start + size);
        lock (_gate)
        {
            var range = new StackRange(start, end);
            if (!_stackRanges.Contains(range))
            {
                _stackRanges.Add(range);
            }
        }
    }

    public bool TryGetStackRange(ulong address, out ulong start, out ulong end)
    {
        lock (_gate)
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
        }

        start = 0;
        end = 0;
        return false;
    }

    public void Map(ulong virtualAddress, ulong memorySize, ulong fileOffset, ReadOnlySpan<byte> fileData, ProgramHeaderFlags protection)
    {
        if (memorySize == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(memorySize), "Memory size must be greater than zero.");
        }

        if ((ulong)fileData.Length > memorySize)
        {
            throw new ArgumentOutOfRangeException(nameof(fileData), "File size cannot exceed memory size.");
        }

        if (memorySize > int.MaxValue)
        {
            throw new NotSupportedException("Virtual memory regions larger than 2 GB are not currently supported.");
        }

        var endAddress = checked(virtualAddress + memorySize);

        lock (_gate)
        {
            foreach (var existing in _regions)
            {
                if (virtualAddress < existing.EndAddress && endAddress > existing.Region.VirtualAddress)
                {
                    throw new InvalidOperationException("Attempted to map an overlapping virtual memory region.");
                }
            }

            var backingMemory = new byte[(int)memorySize];
            fileData.CopyTo(backingMemory);
            var mappedRegion = new MappedRegion(
                new VirtualMemoryRegion(virtualAddress, memorySize, fileOffset, (ulong)fileData.Length, protection),
                endAddress,
                backingMemory);
            var insertionIndex = 0;
            while (insertionIndex < _regions.Count &&
                   _regions[insertionIndex].Region.VirtualAddress < virtualAddress)
            {
                insertionIndex++;
            }

            _regions.Insert(insertionIndex, mappedRegion);
        }
    }

    public IReadOnlyList<VirtualMemoryRegion> SnapshotRegions()
    {
        lock (_gate)
        {
            var snapshot = new VirtualMemoryRegion[_regions.Count];
            for (var i = 0; i < _regions.Count; i++)
            {
                snapshot[i] = _regions[i].Region;
            }

            return snapshot;
        }
    }

    public bool TryQueryMemoryRegion(
        ulong address,
        bool findNext,
        out GuestVirtualMemoryRegion region)
    {
        lock (_gate)
        {
            MappedRegion? next = null;
            foreach (var candidate in _regions)
            {
                if (address >= candidate.Region.VirtualAddress && address < candidate.EndAddress)
                {
                    region = ToGuestMemoryRegion(candidate);
                    return true;
                }

                if (findNext &&
                    candidate.Region.VirtualAddress >= address &&
                    (next is null || candidate.Region.VirtualAddress < next.Value.Region.VirtualAddress))
                {
                    next = candidate;
                }
            }

            if (next is not null)
            {
                region = ToGuestMemoryRegion(next.Value);
                return true;
            }
        }

        region = default;
        return false;
    }

    public bool TryRead(ulong virtualAddress, Span<byte> destination)
    {
        lock (_gate)
        {
            if (!TryResolveRange(
                    virtualAddress,
                    destination.Length,
                    ProgramHeaderFlags.Read,
                    out var regionIndex))
            {
                return false;
            }

            var copied = 0;
            while (copied < destination.Length)
            {
                var region = _regions[regionIndex++];
                var address = virtualAddress + (ulong)copied;
                var offset = checked((int)(address - region.Region.VirtualAddress));
                var length = Math.Min(destination.Length - copied, region.BackingMemory.Length - offset);
                region.BackingMemory.AsSpan(offset, length).CopyTo(destination[copied..]);
                copied += length;
            }

            return true;
        }
    }

    public bool TryCompare(ulong virtualAddress, ReadOnlySpan<byte> expected)
    {
        lock (_gate)
        {
            if (!TryResolveRange(
                    virtualAddress,
                    expected.Length,
                    ProgramHeaderFlags.Read,
                    out var regionIndex))
            {
                return false;
            }

            var compared = 0;
            while (compared < expected.Length)
            {
                var region = _regions[regionIndex++];
                var address = virtualAddress + (ulong)compared;
                var offset = checked((int)(address - region.Region.VirtualAddress));
                var length = Math.Min(expected.Length - compared, region.BackingMemory.Length - offset);
                if (!region.BackingMemory.AsSpan(offset, length).SequenceEqual(expected.Slice(compared, length)))
                {
                    return false;
                }

                compared += length;
            }

            return true;
        }
    }

    public bool TryWrite(ulong virtualAddress, ReadOnlySpan<byte> source)
    {
        lock (_gate)
        {
            if (!TryResolveRange(
                    virtualAddress,
                    source.Length,
                    ProgramHeaderFlags.Write,
                    out var regionIndex))
            {
                return false;
            }

            var copied = 0;
            while (copied < source.Length)
            {
                var region = _regions[regionIndex++];
                var address = virtualAddress + (ulong)copied;
                var offset = checked((int)(address - region.Region.VirtualAddress));
                var length = Math.Min(source.Length - copied, region.BackingMemory.Length - offset);
                source.Slice(copied, length).CopyTo(region.BackingMemory.AsSpan(offset, length));
                copied += length;
            }

            return true;
        }
    }

    private bool TryResolveRange(
        ulong virtualAddress,
        int length,
        ProgramHeaderFlags requiredProtection,
        out int firstRegionIndex)
    {
        var index = FindStartingRegionIndex(virtualAddress, length, out _);
        if (index < 0)
        {
            firstRegionIndex = 0;
            return false;
        }

        firstRegionIndex = index;
        var address = virtualAddress;
        var remaining = (ulong)length;
        while (remaining != 0)
        {
            if (index >= _regions.Count)
            {
                return false;
            }

            var candidate = _regions[index];
            if (address < candidate.Region.VirtualAddress || address >= candidate.EndAddress)
            {
                return false;
            }

            if ((candidate.Region.Protection & requiredProtection) != requiredProtection)
            {
                return false;
            }

            var lengthInRegion = Math.Min(remaining, candidate.EndAddress - address);
            remaining -= lengthInRegion;
            address += lengthInRegion;
            index++;
        }

        return true;
    }

    internal int CountRegionLookupProbes(ulong virtualAddress)
    {
        lock (_gate)
        {
            _ = FindStartingRegionIndex(virtualAddress, length: 1, out var probes);
            return probes;
        }
    }

    private int FindStartingRegionIndex(ulong virtualAddress, int length, out int probes)
    {
        probes = 0;
        var low = 0;
        var high = _regions.Count - 1;
        var candidateIndex = -1;
        while (low <= high)
        {
            probes++;
            var middle = low + ((high - low) / 2);
            if (_regions[middle].Region.VirtualAddress <= virtualAddress)
            {
                candidateIndex = middle;
                low = middle + 1;
            }
            else
            {
                high = middle - 1;
            }
        }

        if (candidateIndex < 0)
        {
            return -1;
        }

        var candidate = _regions[candidateIndex];
        return virtualAddress < candidate.EndAddress ||
               (virtualAddress == candidate.EndAddress && length == 0)
            ? candidateIndex
            : -1;
    }

    private static GuestVirtualMemoryRegion ToGuestMemoryRegion(MappedRegion region)
    {
        var protection = 0;
        if ((region.Region.Protection & ProgramHeaderFlags.Read) != 0)
        {
            protection |= 0x01;
        }

        if ((region.Region.Protection & ProgramHeaderFlags.Write) != 0)
        {
            protection |= 0x02;
        }

        if ((region.Region.Protection & ProgramHeaderFlags.Execute) != 0)
        {
            protection |= 0x04;
        }

        return new GuestVirtualMemoryRegion(
            region.Region.VirtualAddress,
            region.Region.MemorySize,
            protection);
    }

    private readonly record struct MappedRegion(VirtualMemoryRegion Region, ulong EndAddress, byte[] BackingMemory);

    private readonly record struct StackRange(ulong Start, ulong End);
}
