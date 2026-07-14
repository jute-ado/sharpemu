// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Runtime.InteropServices;
using SharpEmu.Core.Loader;
using SharpEmu.HLE;
using SharpEmu.Logging;

namespace SharpEmu.Core.Memory;

public sealed unsafe class PhysicalVirtualMemory : IVirtualMemory, IGuestMemoryAllocator, IGuestStackMemory, IGuestVirtualMemoryQuery, IDisposable
{
    private static readonly SharpEmuLogger Log = SharpEmuLog.For("VMEM");

    private readonly ReaderWriterLockSlim _gate = new(LockRecursionPolicy.SupportsRecursion);
    private readonly object _guestAllocationGate = new();
    private readonly object _allocationSearchHintGate = new();
    private readonly List<MemoryRegion> _regions = new();
    private readonly List<StackRange> _stackRanges = new();
    private readonly Dictionary<(ulong DesiredAddress, ulong Alignment, bool Executable), ulong> _allocationSearchHints = new();
    private readonly Dictionary<ulong, ProgramHeaderFlags> _pageProtections = new();
    private bool _disposed;
    private const ulong PageSize = 0x1000;
    private const ulong GuestAllocationArenaAddress = 0x00006000_0000_0000;
    private const ulong GuestAllocationArenaSize = 0x0100_0000;
    private const ulong GuestAllocationArenaStartOffset = PageSize;
    private const ulong LargeDataReserveThreshold = 0x4000_0000UL; // 1 GiB
    private const ulong DefaultLazyReservePrimeBytes = 0x0400_0000UL; // 64 MiB
    private const ulong LazyReservePrimeChunkBytes = 0x0200_0000UL; // 32 MiB

    private const uint MEM_COMMIT = 0x1000;
    private const uint MEM_RESERVE = 0x2000;
    private const uint MEM_RELEASE = 0x8000;
    private const uint MEM_FREE = 0x10000;
    private const uint PAGE_EXECUTE_READ = 0x20;
    private const uint PAGE_EXECUTE_READWRITE = 0x40;
    private const uint PAGE_EXECUTE = 0x10;
    private const uint PAGE_EXECUTE_WRITECOPY = 0x80;
    private const uint PAGE_NOACCESS = 0x01;
    private const uint PAGE_READWRITE = 0x04;
    private const uint PAGE_READONLY = 0x02;

    private ulong _guestAllocationArenaBase;
    private ulong _guestAllocationOffset;
    private static readonly ulong LazyReservePrimeBytes = ResolveLazyReservePrimeBytes();

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern void* VirtualAlloc(void* lpAddress, nuint dwSize, uint flAllocationType, uint flProtect);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool VirtualFree(void* lpAddress, nuint dwSize, uint dwFreeType);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool VirtualProtect(void* lpAddress, nuint dwSize, uint flNewProtect, out uint lpflOldProtect);

    [DllImport("kernel32.dll")]
    private static extern nuint VirtualQuery(void* lpAddress, out MemoryBasicInformation64 lpBuffer, nuint dwLength);

    [DllImport("kernel32.dll")]
    private static extern void* GetCurrentProcess();

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool FlushInstructionCache(void* hProcess, void* lpBaseAddress, nuint dwSize);

    public bool TryAllocateAtExact(ulong desiredAddress, ulong size, bool executable, out ulong actualAddress)
    {
        ThrowIfDisposed();
        actualAddress = 0;
        if (size == 0)
        {
            return false;
        }

        var alignedSize = (size + 0xFFF) & ~0xFFFUL;
        var protection = executable ? PAGE_EXECUTE_READWRITE : PAGE_READWRITE;
        var allocationType = MEM_COMMIT | MEM_RESERVE;
        var result = VirtualAlloc((void*)desiredAddress, (nuint)alignedSize, allocationType, protection);
        if (result == null)
        {
            return false;
        }

        actualAddress = (ulong)result;
        if (actualAddress != desiredAddress)
        {
            VirtualFree(result, 0, MEM_RELEASE);
            actualAddress = 0;
            return false;
        }

        _gate.EnterWriteLock();
        try
        {
            InsertRegionSorted(new MemoryRegion
            {
                VirtualAddress = actualAddress,
                Size = alignedSize,
                IsExecutable = executable,
                IsReservedOnly = false,
                Protection = protection
            });
        }
        finally
        {
            _gate.ExitWriteLock();
        }

        var allocationKind = executable ? "executable memory" : "data memory";
        TraceVmem($"Allocated exact {allocationKind}: 0x{actualAddress:X16} - 0x{actualAddress + alignedSize:X16} ({alignedSize} bytes)");
        return true;
    }

    public ulong AllocateAt(ulong desiredAddress, ulong size, bool executable = true, bool allowAlternative = true)
    {
        ThrowIfDisposed();
        if (size == 0)
            throw new ArgumentOutOfRangeException(nameof(size), "Size must be greater than zero");

        var alignedSize = (size + 0xFFF) & ~0xFFFUL;

        var protection = executable ? PAGE_EXECUTE_READWRITE : PAGE_READWRITE;
        var allocationType = MEM_COMMIT | MEM_RESERVE;
        var reservedOnly = false;
        var preferReserveOnly = ShouldReserveWithoutCommit(alignedSize, executable);

        void* result = null;
        if (preferReserveOnly)
        {
            result = VirtualAlloc((void*)desiredAddress, (nuint)alignedSize, MEM_RESERVE, PAGE_READWRITE);
            if (result == null && allowAlternative)
            {
                result = VirtualAlloc(null, (nuint)alignedSize, MEM_RESERVE, PAGE_READWRITE);
            }

            if (result != null)
            {
                reservedOnly = true;
            }
        }

        if (result == null)
        {
            result = VirtualAlloc((void*)desiredAddress, (nuint)alignedSize, allocationType, protection);
        }

        if (result == null)
        {
            if (!allowAlternative)
            {
                throw new InvalidOperationException($"Failed to allocate exact mapping at 0x{desiredAddress:X16} ({alignedSize} bytes)");
            }

            TraceVmem($"Could not allocate at 0x{desiredAddress:X16}, trying any address...");
            result = VirtualAlloc(null, (nuint)alignedSize, allocationType, protection);

            if (result == null)
            {
                if (!executable)
                {
                    result = VirtualAlloc((void*)desiredAddress, (nuint)alignedSize, MEM_RESERVE, PAGE_READWRITE);
                    if (result == null && allowAlternative)
                    {
                        result = VirtualAlloc(null, (nuint)alignedSize, MEM_RESERVE, PAGE_READWRITE);
                    }

                    if (result != null)
                    {
                        reservedOnly = true;
                    }
                }

                if (result == null)
                {
                    throw new OutOfMemoryException($"Failed to allocate {alignedSize} bytes of virtual memory");
                }
            }
        }

        var actualAddress = (ulong)result;

        var lazyPrimeState = "n/a";
        if (reservedOnly)
        {
            var primeBytes = Math.Min(alignedSize, LazyReservePrimeBytes);
            if (primeBytes != 0)
            {
                ulong committedBytes = 0;
                while (committedBytes < primeBytes)
                {
                    var remaining = primeBytes - committedBytes;
                    var chunkBytes = Math.Min(remaining, LazyReservePrimeChunkBytes);
                    var commitAddress = (void*)(actualAddress + committedBytes);
                    var committed = VirtualAlloc(commitAddress, (nuint)chunkBytes, MEM_COMMIT, PAGE_READWRITE);
                    if (committed == null)
                    {
                        break;
                    }

                    committedBytes += chunkBytes;
                }

                if (committedBytes != 0)
                {
                    lazyPrimeState = committedBytes == primeBytes
                        ? $"ok:{committedBytes:X}"
                        : $"partial:{committedBytes:X}/{primeBytes:X}";
                    TraceVmem($"Primed lazy region: 0x{actualAddress:X16} - 0x{actualAddress + committedBytes:X16} ({committedBytes} bytes)");
                }
                else
                {
                    lazyPrimeState = $"fail:{primeBytes:X}";
                    TraceVmem($"Failed to prime lazy region at 0x{actualAddress:X16} ({primeBytes} bytes), continuing with on-demand commit");
                }
            }
            else
            {
                lazyPrimeState = "skip:0";
            }
        }

        _gate.EnterWriteLock();
        try
        {
            InsertRegionSorted(new MemoryRegion
            {
                VirtualAddress = actualAddress,
                Size = alignedSize,
                IsExecutable = executable,
                IsReservedOnly = reservedOnly,
                Protection = protection
            });
        }
        finally
        {
            _gate.ExitWriteLock();
        }

        var allocationKind = reservedOnly
            ? "reserved data memory (lazy commit)"
            : (executable ? "executable memory" : "data memory");
        TraceVmem($"Allocated {allocationKind}: 0x{actualAddress:X16} - 0x{actualAddress + alignedSize:X16} ({alignedSize} bytes) lazy_prime={lazyPrimeState}");

        return actualAddress;
    }

    public bool TryAllocateAtOrAbove(
        ulong desiredAddress,
        ulong size,
        bool executable,
        ulong alignment,
        out ulong actualAddress)
    {
        ThrowIfDisposed();
        actualAddress = 0;
        if (size == 0)
        {
            return false;
        }

        var alignedSize = AlignUp(size, PageSize);
        var effectiveAlignment = Math.Max(PageSize, alignment == 0 ? PageSize : alignment);
        var requestedCursor = AlignUp(desiredAddress, effectiveAlignment);
        var cursor = GetAllocationSearchCursor(desiredAddress, requestedCursor, effectiveAlignment, executable);

        for (var attempt = 0; attempt < 0x10000; attempt++)
        {
            if (cursor == 0 || ulong.MaxValue - cursor < alignedSize)
            {
                return false;
            }

            if (TryGetOverlappingRegionEnd(cursor, alignedSize, out var overlapEnd))
            {
                cursor = AlignUp(overlapEnd, effectiveAlignment);
                continue;
            }

            if (!TryFindHostFreeCandidate(
                    cursor,
                    alignedSize,
                    effectiveAlignment,
                    out var hostCandidate,
                    out var nextCursor))
            {
                if (nextCursor <= cursor)
                {
                    return false;
                }

                cursor = nextCursor;
                continue;
            }

            cursor = hostCandidate;

            try
            {
                actualAddress = AllocateAt(cursor, alignedSize, executable, allowAlternative: false);
                if (actualAddress == cursor)
                {
                    UpdateAllocationSearchCursor(desiredAddress, effectiveAlignment, executable, actualAddress + alignedSize);
                    return true;
                }

                actualAddress = 0;
            }
            catch
            {
            }

            cursor = AlignUp(cursor + effectiveAlignment, effectiveAlignment);
        }

        return false;
    }

    private static bool TryFindHostFreeCandidate(
        ulong cursor,
        ulong size,
        ulong alignment,
        out ulong candidate,
        out ulong nextCursor)
    {
        candidate = cursor;
        nextCursor = cursor;
        if (!OperatingSystem.IsWindows() ||
            VirtualQuery((void*)cursor, out var memoryInfo, (nuint)sizeof(MemoryBasicInformation64)) == 0 ||
            memoryInfo.RegionSize == 0)
        {
            return true;
        }

        var regionEnd = memoryInfo.RegionSize > ulong.MaxValue - memoryInfo.BaseAddress
            ? ulong.MaxValue
            : memoryInfo.BaseAddress + memoryInfo.RegionSize;
        if (memoryInfo.State == MEM_FREE)
        {
            var freeCandidate = AlignUp(Math.Max(cursor, memoryInfo.BaseAddress), alignment);
            if (freeCandidate <= regionEnd && size <= regionEnd - freeCandidate)
            {
                candidate = freeCandidate;
                return true;
            }
        }

        nextCursor = regionEnd == ulong.MaxValue
            ? ulong.MaxValue
            : AlignUp(regionEnd, alignment);
        return false;
    }

    public bool TryAllocateGuestMemory(ulong size, ulong alignment, out ulong address)
    {
        ThrowIfDisposed();
        address = 0;
        if (size == 0 || alignment == 0 || (alignment & (alignment - 1)) != 0)
        {
            return false;
        }

        lock (_guestAllocationGate)
        {
            if (_guestAllocationArenaBase == 0)
            {
                try
                {
                    _guestAllocationArenaBase = AllocateAt(
                        GuestAllocationArenaAddress,
                        GuestAllocationArenaSize,
                        executable: false,
                        allowAlternative: true);
                    _guestAllocationOffset = GuestAllocationArenaStartOffset;
                }
                catch (Exception)
                {
                    return false;
                }
            }

            var alignedOffset = AlignUp(_guestAllocationOffset, alignment);
            if (alignedOffset > GuestAllocationArenaSize || size > GuestAllocationArenaSize - alignedOffset)
            {
                return false;
            }

            address = _guestAllocationArenaBase + alignedOffset;
            _guestAllocationOffset = alignedOffset + size;
            return true;
        }
    }

    public void Clear()
    {
        ThrowIfDisposed();
        lock (_guestAllocationGate)
        {
            _gate.EnterWriteLock();
            try
            {
                foreach (var region in _regions)
                {
                    VirtualFree((void*)region.VirtualAddress, 0, MEM_RELEASE);
                }
                _regions.Clear();
                _stackRanges.Clear();
                _pageProtections.Clear();
                lock (_allocationSearchHintGate)
                {
                    _allocationSearchHints.Clear();
                }
            }
            finally
            {
                _gate.ExitWriteLock();
            }

            _guestAllocationArenaBase = 0;
            _guestAllocationOffset = 0;
        }
    }

    public void RegisterStackRange(ulong start, ulong size)
    {
        ThrowIfDisposed();
        if (size == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(size));
        }

        var end = checked(start + size);
        _gate.EnterWriteLock();
        try
        {
            var range = new StackRange(start, end);
            if (!_stackRanges.Contains(range))
            {
                _stackRanges.Add(range);
            }
        }
        finally
        {
            _gate.ExitWriteLock();
        }
    }

    public bool TryGetStackRange(ulong address, out ulong start, out ulong end)
    {
        ThrowIfDisposed();
        _gate.EnterReadLock();
        try
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
        finally
        {
            _gate.ExitReadLock();
        }

        start = 0;
        end = 0;
        return false;
    }

    public void Map(ulong virtualAddress, ulong memorySize, ulong fileOffset, ReadOnlySpan<byte> fileData, ProgramHeaderFlags protection)
    {
        ThrowIfDisposed();
        if (memorySize == 0)
            throw new ArgumentOutOfRangeException(nameof(memorySize));

        if ((ulong)fileData.Length > memorySize)
            throw new ArgumentOutOfRangeException(nameof(fileData), "File size cannot exceed memory size");

        var mapStart = AlignDown(virtualAddress, PageSize);
        var segmentEnd = checked(virtualAddress + memorySize);
        var mapEnd = AlignUp(segmentEnd, PageSize);
        var mapSize = checked(mapEnd - mapStart);

        _gate.EnterWriteLock();
        try
        {
            var existingRegion = FindRegion(mapStart, mapSize);
            if (existingRegion == null)
            {
                var isExecutable = (protection & ProgramHeaderFlags.Execute) != 0;
                AllocateAt(mapStart, mapSize, isExecutable, allowAlternative: false);
            }

            var stageProtection = (protection & ProgramHeaderFlags.Execute) != 0
                ? ProgramHeaderFlags.Read | ProgramHeaderFlags.Write | ProgramHeaderFlags.Execute
                : ProgramHeaderFlags.Read | ProgramHeaderFlags.Write;
            SetProtection(mapStart, mapSize, stageProtection);

            if (!fileData.IsEmpty)
            {
                var destPtr = (void*)virtualAddress;
                fixed (byte* srcPtr = fileData)
                {
                    Buffer.MemoryCopy(srcPtr, destPtr, (nuint)memorySize, (nuint)fileData.Length);
                }
            }

            var zeroFillSize = memorySize - (ulong)fileData.Length;
            if (zeroFillSize != 0)
            {
                NativeMemory.Clear((void*)(virtualAddress + (ulong)fileData.Length), (nuint)zeroFillSize);
            }

            ApplySegmentProtection(mapStart, mapEnd, protection);

            TraceVmem($"Mapped segment: 0x{virtualAddress:X16} - 0x{virtualAddress + memorySize:X16} (file: {fileData.Length} bytes, prot: {protection})");
        }
        finally
        {
            _gate.ExitWriteLock();
        }
    }

    private void ApplySegmentProtection(ulong mapStart, ulong mapEnd, ProgramHeaderFlags flags)
    {
        for (var pageAddress = mapStart; pageAddress < mapEnd; pageAddress += PageSize)
        {
            _pageProtections.TryGetValue(pageAddress, out var existingFlags);
            var mergedFlags = existingFlags | flags;
            _pageProtections[pageAddress] = mergedFlags;
            SetProtection(pageAddress, PageSize, mergedFlags);
        }
    }

    private void SetProtection(ulong address, ulong size, ProgramHeaderFlags flags)
    {
        uint protection;

        if (flags == ProgramHeaderFlags.None)
        {
            protection = PAGE_NOACCESS;
        }
        else if ((flags & ProgramHeaderFlags.Execute) != 0)
        {
            protection = (flags & ProgramHeaderFlags.Write) != 0
                ? PAGE_EXECUTE_READWRITE
                : PAGE_EXECUTE_READ;
        }
        else if ((flags & ProgramHeaderFlags.Write) != 0)
        {
            protection = PAGE_READWRITE;
        }
        else
        {
            protection = PAGE_READONLY;
        }

        if (!VirtualProtect((void*)address, (nuint)size, protection, out _))
        {
            throw new InvalidOperationException($"Failed to set memory protection at 0x{address:X16}");
        }

        if ((flags & ProgramHeaderFlags.Execute) != 0)
        {
            FlushInstructionCache(GetCurrentProcess(), (void*)address, (nuint)size);
        }
    }

    public IReadOnlyList<VirtualMemoryRegion> SnapshotRegions()
    {
        ThrowIfDisposed();
        _gate.EnterReadLock();
        try
        {
            var snapshot = new List<VirtualMemoryRegion>(_regions.Count);
            var pageProtections = _pageProtections
                .OrderBy(pair => pair.Key)
                .ToArray();
            var pageIndex = 0;
            foreach (var region in _regions)
            {
                var regionEnd = checked(region.VirtualAddress + region.Size);
                var cursor = region.VirtualAddress;
                var defaultProtection = GetDefaultProgramHeaderProtection(region);
                while (pageIndex < pageProtections.Length &&
                       pageProtections[pageIndex].Key < region.VirtualAddress)
                {
                    pageIndex++;
                }

                while (pageIndex < pageProtections.Length &&
                       pageProtections[pageIndex].Key < regionEnd)
                {
                    var page = pageProtections[pageIndex];
                    if (cursor < page.Key)
                    {
                        AddSnapshotRegion(
                            snapshot,
                            region.VirtualAddress,
                            cursor,
                            page.Key,
                            defaultProtection);
                    }

                    var pageEnd = regionEnd - page.Key <= PageSize
                        ? regionEnd
                        : page.Key + PageSize;
                    AddSnapshotRegion(
                        snapshot,
                        region.VirtualAddress,
                        page.Key,
                        pageEnd,
                        page.Value);
                    cursor = pageEnd;
                    pageIndex++;
                }

                if (cursor < regionEnd)
                {
                    AddSnapshotRegion(
                        snapshot,
                        region.VirtualAddress,
                        cursor,
                        regionEnd,
                        defaultProtection);
                }
            }

            return snapshot;
        }
        finally
        {
            _gate.ExitReadLock();
        }
    }

    private static void AddSnapshotRegion(
        List<VirtualMemoryRegion> snapshot,
        ulong allocationStart,
        ulong start,
        ulong end,
        ProgramHeaderFlags protection)
    {
        var size = end - start;
        if (snapshot.Count != 0)
        {
            var previous = snapshot[^1];
            if (previous.VirtualAddress >= allocationStart &&
                previous.VirtualAddress + previous.MemorySize == start &&
                previous.Protection == protection)
            {
                var combinedSize = checked(previous.MemorySize + size);
                snapshot[^1] = new VirtualMemoryRegion(
                    previous.VirtualAddress,
                    combinedSize,
                    0,
                    combinedSize,
                    protection);
                return;
            }
        }

        snapshot.Add(new VirtualMemoryRegion(start, size, 0, size, protection));
    }

    private static ProgramHeaderFlags GetDefaultProgramHeaderProtection(MemoryRegion region)
    {
        return (region.Protection & 0xFF) switch
        {
            PAGE_READONLY => ProgramHeaderFlags.Read,
            PAGE_READWRITE => ProgramHeaderFlags.Read | ProgramHeaderFlags.Write,
            PAGE_EXECUTE => ProgramHeaderFlags.Execute,
            PAGE_EXECUTE_READ => ProgramHeaderFlags.Read | ProgramHeaderFlags.Execute,
            PAGE_EXECUTE_READWRITE or PAGE_EXECUTE_WRITECOPY =>
                ProgramHeaderFlags.Read | ProgramHeaderFlags.Write | ProgramHeaderFlags.Execute,
            _ => ProgramHeaderFlags.None,
        };
    }

    public bool TryQueryMemoryRegion(
        ulong address,
        bool findNext,
        out GuestVirtualMemoryRegion region)
    {
        ThrowIfDisposed();
        _gate.EnterReadLock();
        try
        {
            MemoryRegion? next = null;
            foreach (var candidate in _regions)
            {
                if (candidate.Size > ulong.MaxValue - candidate.VirtualAddress)
                {
                    continue;
                }

                var candidateEnd = candidate.VirtualAddress + candidate.Size;
                if (address >= candidate.VirtualAddress && address < candidateEnd)
                {
                    region = ToGuestMemoryRegion(candidate, address);
                    return true;
                }

                if (findNext &&
                    candidate.VirtualAddress >= address &&
                    (next is null || candidate.VirtualAddress < next.VirtualAddress))
                {
                    next = candidate;
                }
            }

            if (next is not null)
            {
                region = ToGuestMemoryRegion(next, next.VirtualAddress);
                return true;
            }
        }
        finally
        {
            _gate.ExitReadLock();
        }

        region = default;
        return false;
    }

    public bool TryRead(ulong virtualAddress, Span<byte> destination)
    {
        ThrowIfDisposed();
        var requiresExclusiveAccess = false;
        _gate.EnterReadLock();
        try
        {
            var region = FindRegion(virtualAddress, (ulong)destination.Length);
            if (region is not null &&
                TryResolveRegionOffset(
                    virtualAddress,
                    (ulong)destination.Length,
                    region,
                    out var offset))
            {
                var srcPtr = (void*)(region.VirtualAddress + offset);
                if (destination.IsEmpty)
                {
                    return true;
                }

                if (region.IsReservedOnly)
                {
                    if (!EnsureRangeCommitted((ulong)srcPtr, (ulong)destination.Length, region))
                    {
                        return false;
                    }
                }

                if (!CanReadWithoutProtectionChange((ulong)srcPtr, (ulong)destination.Length, region))
                {
                    requiresExclusiveAccess = true;
                }
                else
                {
                    fixed (byte* destPtr = destination)
                    {
                        Buffer.MemoryCopy(srcPtr, destPtr, (nuint)destination.Length, (nuint)destination.Length);
                    }

                    return true;
                }
            }
        }
        finally
        {
            _gate.ExitReadLock();
        }

        if (!requiresExclusiveAccess)
        {
            return false;
        }

        _gate.EnterWriteLock();
        try
        {
            return TryReadExclusive(virtualAddress, destination);
        }
        finally
        {
            _gate.ExitWriteLock();
        }
    }

    public bool TryWrite(ulong virtualAddress, ReadOnlySpan<byte> source)
    {
        ThrowIfDisposed();
        var requiresExclusiveAccess = false;
        _gate.EnterReadLock();
        try
        {
            var region = FindRegion(virtualAddress, (ulong)source.Length);
            if (region is not null &&
                TryResolveRegionOffset(
                    virtualAddress,
                    (ulong)source.Length,
                    region,
                    out var offset))
            {
                var destPtr = (void*)(region.VirtualAddress + offset);
                if (source.IsEmpty)
                {
                    return true;
                }

                if (region.IsReservedOnly)
                {
                    if (!EnsureRangeCommitted((ulong)destPtr, (ulong)source.Length, region))
                    {
                        return false;
                    }
                }

                if (!CanWriteWithoutProtectionChange((ulong)destPtr, (ulong)source.Length, region))
                {
                    requiresExclusiveAccess = true;
                }
                else
                {
                    fixed (byte* srcPtr = source)
                    {
                        Buffer.MemoryCopy(srcPtr, destPtr, (nuint)source.Length, (nuint)source.Length);
                    }

                    return true;
                }
            }
        }
        finally
        {
            _gate.ExitReadLock();
        }

        if (!requiresExclusiveAccess)
        {
            return false;
        }

        _gate.EnterWriteLock();
        try
        {
            return TryWriteExclusive(virtualAddress, source);
        }
        finally
        {
            _gate.ExitWriteLock();
        }
    }

    private bool TryReadExclusive(ulong virtualAddress, Span<byte> destination)
    {
        var region = FindRegion(virtualAddress, (ulong)destination.Length);
        if (region is not null &&
            TryResolveRegionOffset(
                virtualAddress,
                (ulong)destination.Length,
                region,
                out var offset))
        {
            var srcPtr = (void*)(region.VirtualAddress + offset);
            if (!EnsureRangeCommitted((ulong)srcPtr, (ulong)destination.Length, region))
            {
                return false;
            }

            if (CanReadWithoutProtectionChange((ulong)srcPtr, (ulong)destination.Length, region))
            {
                fixed (byte* destPtr = destination)
                {
                    Buffer.MemoryCopy(srcPtr, destPtr, (nuint)destination.Length, (nuint)destination.Length);
                }

                return true;
            }

            if (!TryTemporarilyProtectForRead((ulong)srcPtr, (ulong)destination.Length, region, out var touchedPages))
            {
                return false;
            }

            try
            {
                fixed (byte* destPtr = destination)
                {
                    Buffer.MemoryCopy(srcPtr, destPtr, (nuint)destination.Length, (nuint)destination.Length);
                }
            }
            finally
            {
                RestorePageProtections(touchedPages);
            }

            return true;
        }

        return false;
    }

    private bool TryWriteExclusive(ulong virtualAddress, ReadOnlySpan<byte> source)
    {
        var region = FindRegion(virtualAddress, (ulong)source.Length);
        if (region is not null &&
            TryResolveRegionOffset(
                virtualAddress,
                (ulong)source.Length,
                region,
                out var offset))
        {
            var destPtr = (void*)(region.VirtualAddress + offset);
            if (!EnsureRangeCommitted((ulong)destPtr, (ulong)source.Length, region))
            {
                return false;
            }

            if (CanWriteWithoutProtectionChange((ulong)destPtr, (ulong)source.Length, region))
            {
                fixed (byte* srcPtr = source)
                {
                    Buffer.MemoryCopy(srcPtr, destPtr, (nuint)source.Length, (nuint)source.Length);
                }

                return true;
            }

            if (!VirtualProtect(destPtr, (nuint)source.Length, PAGE_EXECUTE_READWRITE, out var oldProtect))
            {
                return false;
            }

            try
            {
                fixed (byte* srcPtr = source)
                {
                    Buffer.MemoryCopy(srcPtr, destPtr, (nuint)source.Length, (nuint)source.Length);
                }
            }
            finally
            {
                VirtualProtect(destPtr, (nuint)source.Length, oldProtect, out _);
                if (IsExecutableProtection(oldProtect))
                {
                    FlushInstructionCache(GetCurrentProcess(), destPtr, (nuint)source.Length);
                }
            }

            return true;
        }

        return false;
    }

    public bool TryWriteUInt64(ulong virtualAddress, ulong value)
    {
        ThrowIfDisposed();
        Span<byte> buffer = stackalloc byte[sizeof(ulong)];
        BitConverter.TryWriteBytes(buffer, value);
        return TryWrite(virtualAddress, buffer);
    }

    public void* GetPointer(ulong virtualAddress)
    {
        ThrowIfDisposed();
        _gate.EnterReadLock();
        try
        {
            return FindRegion(virtualAddress, 1) is not null
                ? (void*)virtualAddress
                : null;
        }
        finally
        {
            _gate.ExitReadLock();
        }
    }

    public bool IsAccessible(ulong virtualAddress, ulong size)
    {
        ThrowIfDisposed();
        _gate.EnterReadLock();
        try
        {
            return FindRegion(virtualAddress, size) is not null;
        }
        finally
        {
            _gate.ExitReadLock();
        }
    }

    private MemoryRegion? FindRegion(ulong address, ulong size)
    {
        var low = 0;
        var high = _regions.Count - 1;
        MemoryRegion? candidate = null;
        while (low <= high)
        {
            var middle = low + ((high - low) >> 1);
            var region = _regions[middle];
            if (region.VirtualAddress <= address)
            {
                candidate = region;
                low = middle + 1;
            }
            else
            {
                high = middle - 1;
            }
        }

        return candidate is not null &&
            TryResolveRegionOffset(address, size, candidate, out _)
                ? candidate
                : null;
    }

    private void InsertRegionSorted(MemoryRegion region)
    {
        var low = 0;
        var high = _regions.Count;
        while (low < high)
        {
            var middle = low + ((high - low) >> 1);
            if (_regions[middle].VirtualAddress < region.VirtualAddress)
            {
                low = middle + 1;
            }
            else
            {
                high = middle;
            }
        }

        _regions.Insert(low, region);
    }

    private bool TryGetOverlappingRegionEnd(ulong address, ulong size, out ulong overlapEnd)
    {
        overlapEnd = 0;
        if (size == 0 || ulong.MaxValue - address < size - 1)
        {
            return false;
        }

        var end = address + size;
        _gate.EnterReadLock();
        try
        {
            foreach (var region in _regions)
            {
                var regionEnd = region.VirtualAddress + region.Size;
                if (region.VirtualAddress >= end)
                {
                    break;
                }

                if (regionEnd <= address)
                {
                    continue;
                }

                if (address < regionEnd && region.VirtualAddress < end)
                {
                    overlapEnd = Math.Max(overlapEnd, regionEnd);
                }
            }
        }
        finally
        {
            _gate.ExitReadLock();
        }

        return overlapEnd != 0;
    }

    private ulong GetAllocationSearchCursor(
        ulong desiredAddress,
        ulong requestedCursor,
        ulong alignment,
        bool executable)
    {
        lock (_allocationSearchHintGate)
        {
            var key = (desiredAddress, alignment, executable);
            if (_allocationSearchHints.TryGetValue(key, out var hintedCursor) &&
                hintedCursor > requestedCursor)
            {
                return AlignUp(hintedCursor, alignment);
            }
        }

        return requestedCursor;
    }

    private void UpdateAllocationSearchCursor(
        ulong desiredAddress,
        ulong alignment,
        bool executable,
        ulong nextCursor)
    {
        lock (_allocationSearchHintGate)
        {
            _allocationSearchHints[(desiredAddress, alignment, executable)] = AlignUp(nextCursor, alignment);
        }
    }

    private static bool TryResolveRegionOffset(ulong address, ulong size, MemoryRegion region, out ulong offset)
    {
        offset = 0;
        if (address < region.VirtualAddress)
        {
            return false;
        }

        offset = address - region.VirtualAddress;
        if (offset > region.Size)
        {
            return false;
        }

        if (size > region.Size - offset)
        {
            return false;
        }

        return true;
    }

    private static bool IsExecutableProtection(uint protection)
    {
        return protection is PAGE_EXECUTE or PAGE_EXECUTE_READ or PAGE_EXECUTE_READWRITE or PAGE_EXECUTE_WRITECOPY;
    }

    private bool CanReadWithoutProtectionChange(ulong address, ulong size, MemoryRegion region) =>
        CanAccessWithoutProtectionChange(address, size, region, write: false);

    private bool CanWriteWithoutProtectionChange(ulong address, ulong size, MemoryRegion region) =>
        CanAccessWithoutProtectionChange(address, size, region, write: true);

    private bool CanAccessWithoutProtectionChange(ulong address, ulong size, MemoryRegion region, bool write)
    {
        var startPage = AlignDown(address, PageSize);
        var endPage = AlignUp(address + size, PageSize);
        for (var pageAddress = startPage; pageAddress < endPage; pageAddress += PageSize)
        {
            if (_pageProtections.TryGetValue(pageAddress, out var flags))
            {
                if (write ? (flags & ProgramHeaderFlags.Write) == 0 : (flags & ProgramHeaderFlags.Read) == 0)
                {
                    return false;
                }
            }
            else if (write ? !IsWritableProtection(region.Protection) : !IsReadableProtection(region.Protection))
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsReadableProtection(uint protection)
    {
        return protection is PAGE_READONLY or PAGE_READWRITE or PAGE_EXECUTE_READ or PAGE_EXECUTE_READWRITE;
    }

    private static bool IsWritableProtection(uint protection)
    {
        return protection is PAGE_READWRITE or PAGE_EXECUTE_READWRITE;
    }

    private static uint GetCommitProtection(MemoryRegion region)
    {
        return region.IsExecutable ? PAGE_EXECUTE_READWRITE : PAGE_READWRITE;
    }

    private static unsafe bool EnsureRangeCommitted(ulong address, ulong size, MemoryRegion region)
    {
        if (size == 0 || !region.IsReservedOnly)
        {
            return true;
        }

        var startPage = AlignDown(address, PageSize);
        var endPage = AlignUp(address + size, PageSize);
        var commitProtection = GetCommitProtection(region);

        var pageAddress = startPage;
        while (pageAddress < endPage)
        {
            if (VirtualQuery((void*)pageAddress, out var info, (nuint)sizeof(MemoryBasicInformation64)) == 0)
            {
                return false;
            }

            var queriedEnd = info.RegionSize > ulong.MaxValue - info.BaseAddress
                ? ulong.MaxValue
                : info.BaseAddress + info.RegionSize;
            var rangeEnd = Math.Min(endPage, queriedEnd);
            if (rangeEnd <= pageAddress)
            {
                return false;
            }

            if (info.State == MEM_COMMIT)
            {
                pageAddress = rangeEnd;
                continue;
            }

            if (info.State != MEM_RESERVE)
            {
                return false;
            }

            var commitSize = rangeEnd - pageAddress;
            if (VirtualAlloc((void*)pageAddress, (nuint)commitSize, MEM_COMMIT, commitProtection) == null)
            {
                return false;
            }

            pageAddress = rangeEnd;
        }

        return true;
    }

    private bool TryTemporarilyProtectForRead(
        ulong address,
        ulong size,
        MemoryRegion region,
        out List<(ulong Address, uint Protection)> touchedPages)
    {
        touchedPages = new List<(ulong Address, uint Protection)>();

        var startPage = AlignDown(address, PageSize);
        var endPage = AlignUp(address + size, PageSize);
        var temporaryProtection = region.IsExecutable ? PAGE_EXECUTE_READWRITE : PAGE_READWRITE;

        for (var pageAddress = startPage; pageAddress < endPage; pageAddress += PageSize)
        {
            if (!VirtualProtect((void*)pageAddress, (nuint)PageSize, temporaryProtection, out var oldProtection))
            {
                RestorePageProtections(touchedPages);
                touchedPages.Clear();
                return false;
            }

            touchedPages.Add((pageAddress, oldProtection));
        }

        return true;
    }

    private static void RestorePageProtections(List<(ulong Address, uint Protection)> touchedPages)
    {
        foreach (var (pageAddress, protection) in touchedPages)
        {
            VirtualProtect((void*)pageAddress, (nuint)PageSize, protection, out _);
        }
    }

    private static ulong AlignDown(ulong value, ulong alignment)
    {
        var mask = alignment - 1;
        return value & ~mask;
    }

    private static ulong AlignUp(ulong value, ulong alignment)
    {
        var mask = alignment - 1;
        return checked((value + mask) & ~mask);
    }

    private static ulong ResolveLazyReservePrimeBytes()
    {
        var configured = Environment.GetEnvironmentVariable("SHARPEMU_LAZY_RESERVE_PRIME_MB");
        if (ulong.TryParse(configured, out var megabytes))
        {
            return megabytes == 0
                ? 0
                : checked(Math.Min(megabytes, 4096UL) * 1024UL * 1024UL);
        }

        return DefaultLazyReservePrimeBytes;
    }

    internal static bool ShouldReserveWithoutCommit(ulong alignedSize, bool executable) =>
        !executable && alignedSize >= LargeDataReserveThreshold;

    private static void TraceVmem(string message)
    {
        if (!string.Equals(Environment.GetEnvironmentVariable("SHARPEMU_LOG_VMEM"), "1", StringComparison.Ordinal))
        {
            return;
        }

        Log.Debug(message);
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            Clear();
            _disposed = true;
        }
    }

    private class MemoryRegion
    {
        public ulong VirtualAddress { get; set; }
        public ulong Size { get; set; }
        public bool IsExecutable { get; set; }
        public bool IsReservedOnly { get; set; }
        public uint Protection { get; set; }
    }

    private GuestVirtualMemoryRegion ToGuestMemoryRegion(MemoryRegion region, ulong address)
    {
        var pageAddress = address & ~(PageSize - 1);
        var protection = GetOrbisProtection(region, pageAddress);
        var regionEnd = region.VirtualAddress + region.Size;
        var queryStart = Math.Max(region.VirtualAddress, pageAddress);
        var pageEnd = pageAddress > ulong.MaxValue - PageSize
            ? ulong.MaxValue
            : pageAddress + PageSize;
        var queryEnd = Math.Min(regionEnd, pageEnd);

        while (queryStart > region.VirtualAddress)
        {
            var previousPage = (queryStart - 1) & ~(PageSize - 1);
            if (GetOrbisProtection(region, previousPage) != protection)
            {
                break;
            }

            queryStart = Math.Max(region.VirtualAddress, previousPage);
        }

        while (queryEnd < regionEnd && GetOrbisProtection(region, queryEnd) == protection)
        {
            queryEnd = regionEnd - queryEnd <= PageSize
                ? regionEnd
                : queryEnd + PageSize;
        }

        return new GuestVirtualMemoryRegion(
            queryStart,
            queryEnd - queryStart,
            protection);
    }

    private int GetOrbisProtection(MemoryRegion region, ulong pageAddress)
    {
        return _pageProtections.TryGetValue(pageAddress, out var pageProtection)
            ? ToOrbisProtection(pageProtection)
            : ToOrbisProtection(region.Protection);
    }

    private static int ToOrbisProtection(ProgramHeaderFlags protection)
    {
        var result = 0;
        if ((protection & ProgramHeaderFlags.Read) != 0)
        {
            result |= 0x01;
        }

        if ((protection & ProgramHeaderFlags.Write) != 0)
        {
            result |= 0x02;
        }

        if ((protection & ProgramHeaderFlags.Execute) != 0)
        {
            result |= 0x04;
        }

        return result;
    }

    private static int ToOrbisProtection(uint protection)
    {
        return (protection & 0xFF) switch
        {
            PAGE_READONLY => 0x01,
            PAGE_READWRITE => 0x03,
            PAGE_EXECUTE => 0x04,
            PAGE_EXECUTE_READ => 0x05,
            PAGE_EXECUTE_READWRITE or PAGE_EXECUTE_WRITECOPY => 0x07,
            _ => 0,
        };
    }

    private readonly record struct StackRange(ulong Start, ulong End);

    private struct MemoryBasicInformation64
    {
        public ulong BaseAddress;
        public ulong AllocationBase;
        public uint AllocationProtect;
        public uint Alignment1;
        public ulong RegionSize;
        public uint State;
        public uint Protect;
        public uint Type;
        public uint Alignment2;
    }
}
