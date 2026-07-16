// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Core.Memory;
using SharpEmu.HLE;

namespace SharpEmu.Core.Cpu;

public sealed class TrackedCpuMemory : ICpuMemory, ITrackedCpuMemory, IGuestMemoryAllocator, IGuestStackMemory, IGuestVirtualMemoryQuery, IGuestImageMemory, ICpuMemoryWrapper
{
    private readonly ICpuMemory _inner;

    public TrackedCpuMemory(ICpuMemory inner)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
    }

    public CpuMemoryAccessFailure? LastFailure { get; private set; }

    public ICpuMemory Inner => _inner;

    public bool TryRead(ulong virtualAddress, Span<byte> destination)
    {
        var result = _inner.TryRead(virtualAddress, destination);
        if (!result)
        {
            LastFailure = new CpuMemoryAccessFailure(virtualAddress, destination.Length, isWrite: false);
        }

        return result;
    }

    public bool TryCompare(ulong virtualAddress, ReadOnlySpan<byte> expected) =>
        _inner.TryCompare(virtualAddress, expected);

    public bool TryWrite(ulong virtualAddress, ReadOnlySpan<byte> source)
    {
        var result = _inner.TryWrite(virtualAddress, source);
        if (!result)
        {
            LastFailure = new CpuMemoryAccessFailure(virtualAddress, source.Length, isWrite: true);
        }

        return result;
    }

    public bool TryAllocateGuestMemory(ulong size, ulong alignment, out ulong address)
    {
        if (_inner is IGuestMemoryAllocator allocator)
        {
            return allocator.TryAllocateGuestMemory(size, alignment, out address);
        }

        address = 0;
        return false;
    }

    public bool TryFreeGuestMemory(ulong address) =>
        _inner is IGuestMemoryAllocator allocator &&
        allocator.TryFreeGuestMemory(address);

    public void RegisterStackRange(ulong start, ulong size)
    {
        if (_inner is not IGuestStackMemory stackMemory)
        {
            throw new NotSupportedException("The wrapped memory does not track guest stack ranges.");
        }

        stackMemory.RegisterStackRange(start, size);
    }

    public bool TryGetStackRange(ulong address, out ulong start, out ulong end)
    {
        if (_inner is IGuestStackMemory stackMemory)
        {
            return stackMemory.TryGetStackRange(address, out start, out end);
        }

        start = 0;
        end = 0;
        return false;
    }

    public bool TryQueryMemoryRegion(
        ulong address,
        bool findNext,
        out GuestVirtualMemoryRegion region)
    {
        if (_inner is IGuestVirtualMemoryQuery query)
        {
            return query.TryQueryMemoryRegion(address, findNext, out region);
        }

        region = default;
        return false;
    }

    public void RegisterImage(IReadOnlyList<VirtualMemoryRegion> regions)
    {
        if (_inner is not IGuestImageMemory imageMemory)
        {
            throw new NotSupportedException(
                "The wrapped memory does not track loaded guest images.");
        }

        imageMemory.RegisterImage(regions);
    }

    public bool TryGetImageRegions(
        ulong address,
        out IReadOnlyList<VirtualMemoryRegion> regions)
    {
        if (_inner is IGuestImageMemory imageMemory)
        {
            return imageMemory.TryGetImageRegions(address, out regions);
        }

        regions = Array.Empty<VirtualMemoryRegion>();
        return false;
    }
}
