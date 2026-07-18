// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Runtime.InteropServices;
using SharpEmu.Core.Loader;
using SharpEmu.HLE;
using SharpEmu.HLE.Host;
using SharpEmu.Logging;

namespace SharpEmu.Core.Memory;

public sealed unsafe class PhysicalVirtualMemory : IVirtualMemory, IGuestMemoryAllocator, IGuestStackMemory, IGuestVirtualMemoryQuery, IGuestAddressSpace, IGuestImageMemory, IDisposable
{
    private static readonly SharpEmuLogger Log = SharpEmuLog.For("VMEM");

    private readonly ReaderWriterLockSlim _gate = new(LockRecursionPolicy.SupportsRecursion);
    private readonly object _guestAllocationGate = new();
    private readonly object _allocationSearchHintGate = new();
    private readonly List<MemoryRegion> _regions = new();
    private readonly List<StackRange> _stackRanges = new();
    private readonly GuestImageRegionRegistry _imageRegions = new();
    private readonly Dictionary<(ulong DesiredAddress, ulong Alignment, bool Executable), ulong> _allocationSearchHints = new();
    private readonly Dictionary<ulong, ProgramHeaderFlags> _pageProtections = new();
    private readonly GuestRangeAllocator _guestAllocator = new();
    private bool _disposed;
    private const ulong PageSize = 0x1000;
    private const ulong HostAllocationGranularity = 0x1_0000;
    private const ulong GuestAllocationArenaAddress = 0x00006000_0000_0000;
    private const ulong GuestAllocationArenaSize = 0x0100_0000;
    private const ulong GuestAllocationArenaStartOffset = PageSize;
    private const ulong LargeDataReserveThreshold = 0x4000_0000UL; // 1 GiB
    private const ulong DefaultLazyReservePrimeBytes = 0x0400_0000UL; // 64 MiB
    private const ulong LazyReservePrimeChunkBytes = 0x0200_0000UL; // 32 MiB

    // Raw Windows PAGE_* values retained for the internal region/protection
    // bookkeeping: regions and saved old-protection values always carry the raw
    // value of the host platform in use, and these classification helpers only
    // ever see values this class itself assigned (see IHostMemory.ProtectRaw).
    private const uint PAGE_EXECUTE_READ = 0x20;
    private const uint PAGE_EXECUTE_READWRITE = 0x40;
    private const uint PAGE_EXECUTE = 0x10;
    private const uint PAGE_EXECUTE_WRITECOPY = 0x80;
    private const uint PAGE_READWRITE = 0x04;
    private const uint PAGE_READONLY = 0x02;

    private ulong _resetVersion = 1;
    private static readonly ulong LazyReservePrimeBytes = ResolveLazyReservePrimeBytes();
    private readonly IHostMemory _hostMemory;

    internal ulong ProtectionApplicationCount { get; private set; }

    public ulong ResetVersion
    {
        get
        {
            ThrowIfDisposed();
            _gate.EnterReadLock();
            try
            {
                return _resetVersion;
            }
            finally
            {
                _gate.ExitReadLock();
            }
        }
    }

    public PhysicalVirtualMemory(IHostMemory? hostMemory = null)
    {
        _hostMemory = hostMemory ?? HostPlatform.Current.Memory;
    }

    public bool TryAllocateAtExact(ulong desiredAddress, ulong size, bool executable, out ulong actualAddress)
    {
        ThrowIfDisposed();
        actualAddress = 0;
        if (size == 0 ||
            !TryAlignAllocationSize(size, out var alignedSize) ||
            (desiredAddress != 0 && desiredAddress > ulong.MaxValue - alignedSize))
        {
            return false;
        }

        var protection = executable ? PAGE_EXECUTE_READWRITE : PAGE_READWRITE;
        var hostProtection = executable ? HostPageProtection.ReadWriteExecute : HostPageProtection.ReadWrite;
        var result = _hostMemory.Allocate(desiredAddress, alignedSize, hostProtection);
        if (result == 0)
        {
            return false;
        }

        actualAddress = result;
        if (actualAddress != desiredAddress)
        {
            _hostMemory.Free(result);
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

    public string DescribeAddressForDiagnostics(ulong address)
    {
        if (!_hostMemory.Query(address, out var info))
        {
            return "unable to query host memory at this address";
        }

        return info.State switch
        {
            HostRegionState.Free => "address reports free, but the exact-address reservation still failed",
            HostRegionState.Reserved =>
                $"already reserved by another host allocation (base=0x{info.AllocationBase:X16}, size=0x{info.RegionSize:X})",
            HostRegionState.Committed =>
                $"already committed by another host allocation (base=0x{info.AllocationBase:X16}, size=0x{info.RegionSize:X}, protect=0x{info.RawProtection:X})",
            _ => $"in an unexpected host state (raw=0x{info.RawState:X})",
        };
    }

    public ulong AllocateAt(ulong desiredAddress, ulong size, bool executable = true, bool allowAlternative = true)
    {
        ThrowIfDisposed();
        if (size == 0)
            throw new ArgumentOutOfRangeException(nameof(size), "Size must be greater than zero");

        if (!TryAlignAllocationSize(size, out var alignedSize))
        {
            throw new ArgumentOutOfRangeException(nameof(size), "Size exceeds the guest address space.");
        }
        if (desiredAddress != 0 && desiredAddress > ulong.MaxValue - alignedSize)
        {
            throw new ArgumentOutOfRangeException(
                nameof(desiredAddress),
                "Requested allocation range exceeds the guest address space.");
        }

        var protection = executable ? PAGE_EXECUTE_READWRITE : PAGE_READWRITE;
        var hostProtection = executable ? HostPageProtection.ReadWriteExecute : HostPageProtection.ReadWrite;
        var reservedOnly = false;
        var preferReserveOnly = ShouldReserveWithoutCommit(alignedSize, executable);
        var allocatedRequestedRange = false;

        ulong result = 0;
        if (preferReserveOnly)
        {
            result = _hostMemory.Reserve(desiredAddress, alignedSize, HostPageProtection.ReadWrite);
            allocatedRequestedRange = result != 0 && desiredAddress != 0;
            if (result == 0 && allowAlternative)
            {
                result = _hostMemory.Reserve(0, alignedSize, HostPageProtection.ReadWrite);
                allocatedRequestedRange = false;
            }

            if (result != 0)
            {
                reservedOnly = true;
            }
        }

        if (result == 0)
        {
            result = _hostMemory.Allocate(desiredAddress, alignedSize, hostProtection);
            allocatedRequestedRange = result != 0 && desiredAddress != 0;
        }

        if (result == 0)
        {
            if (!allowAlternative)
            {
                throw new InvalidOperationException($"Failed to allocate exact mapping at 0x{desiredAddress:X16} ({alignedSize} bytes)");
            }

            TraceVmem($"Could not allocate at 0x{desiredAddress:X16}, trying any address...");
            result = _hostMemory.Allocate(0, alignedSize, hostProtection);
            allocatedRequestedRange = false;

            if (result == 0)
            {
                if (!executable)
                {
                    result = _hostMemory.Reserve(desiredAddress, alignedSize, HostPageProtection.ReadWrite);
                    allocatedRequestedRange = result != 0 && desiredAddress != 0;
                    if (result == 0 && allowAlternative)
                    {
                        result = _hostMemory.Reserve(0, alignedSize, HostPageProtection.ReadWrite);
                        allocatedRequestedRange = false;
                    }

                    if (result != 0)
                    {
                        reservedOnly = true;
                    }
                }

                if (result == 0)
                {
                    throw new OutOfMemoryException($"Failed to allocate {alignedSize} bytes of virtual memory");
                }
            }
        }

        var hostAddress = result;
        if (allocatedRequestedRange && hostAddress > desiredAddress)
        {
            _hostMemory.Free(result);
            throw new InvalidOperationException(
                $"Host allocation at 0x{hostAddress:X16} does not contain requested address 0x{desiredAddress:X16}.");
        }

        var guestAddress = allocatedRequestedRange ? desiredAddress : hostAddress;
        var trackedSize = allocatedRequestedRange
            ? checked(desiredAddress - hostAddress + alignedSize)
            : alignedSize;

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
                    var commitAddress = hostAddress + committedBytes;
                    if (!_hostMemory.Commit(commitAddress, chunkBytes, HostPageProtection.ReadWrite))
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
                    TraceVmem($"Primed lazy region: 0x{hostAddress:X16} - 0x{hostAddress + committedBytes:X16} ({committedBytes} bytes)");
                }
                else
                {
                    lazyPrimeState = $"fail:{primeBytes:X}";
                    TraceVmem($"Failed to prime lazy region at 0x{hostAddress:X16} ({primeBytes} bytes), continuing with on-demand commit");
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
                VirtualAddress = hostAddress,
                Size = trackedSize,
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
        TraceVmem(
            $"Allocated {allocationKind}: guest=0x{guestAddress:X16} host=0x{hostAddress:X16} " +
            $"host_end=0x{hostAddress + trackedSize:X16} requested={alignedSize} tracked={trackedSize} lazy_prime={lazyPrimeState}");

        return guestAddress;
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
        if (size == 0 ||
            (alignment != 0 && (alignment & (alignment - 1)) != 0))
        {
            return false;
        }

        try
        {
            return TryAllocateAtOrAboveCore(
                desiredAddress,
                size,
                executable,
                alignment,
                out actualAddress);
        }
        catch (OverflowException)
        {
            actualAddress = 0;
            return false;
        }
    }

    private bool TryAllocateAtOrAboveCore(
        ulong desiredAddress,
        ulong size,
        bool executable,
        ulong alignment,
        out ulong actualAddress)
    {
        actualAddress = 0;
        var alignedSize = AlignUp(size, PageSize);
        var effectiveAlignment = Math.Max(PageSize, alignment == 0 ? PageSize : alignment);
        var requestedCursor = AlignUp(desiredAddress, effectiveAlignment);
        var cursor = GetAllocationSearchCursor(desiredAddress, requestedCursor, effectiveAlignment, executable);

        // Under Rosetta 2 the kernel can ignore placement hints for whole
        // windows, so page-stepped exact probes are pathological on macOS.
        // Linux must keep using the exact-address search below: PS5 resource
        // descriptors cannot represent ordinary 0x7F... host mappings. Linux
        // The POSIX host-memory backend uses MAP_FIXED_NOREPLACE, making those
        // low-address probes safe without clobbering existing host mappings.
        if (OperatingSystem.IsMacOS())
        {
            // Prefer the requested low address.  Besides matching the guest
            // address model, this keeps the allocation representable by every
            // PS5 GPU descriptor (the strictest ones carry 40 address bits).
            try
            {
                var exactAddress = AllocateAt(
                    cursor,
                    alignedSize,
                    executable,
                    allowAlternative: false);
                if (exactAddress == cursor)
                {
                    actualAddress = exactAddress;
                    UpdateAllocationSearchCursor(
                        desiredAddress,
                        effectiveAlignment,
                        executable,
                        exactAddress + alignedSize);
                    return true;
                }
            }
            catch
            {
            }

            // Over-allocate by the alignment so a kernel-chosen placement
            // always contains an aligned start; the unused head/tail stays
            // part of the tracked region and is simply never handed out.
            var reserveSize = effectiveAlignment > PageSize
                ? alignedSize + effectiveAlignment
                : alignedSize;
            try
            {
                var posixAddress = AllocateAt(cursor, reserveSize, executable, allowAlternative: true);
                if (posixAddress != 0)
                {
                    var alignedBase = AlignUp(posixAddress, effectiveAlignment);
                    const ulong gpuAddressLimit = 1UL << 40;
                    if (alignedBase < gpuAddressLimit &&
                        alignedSize <= gpuAddressLimit - alignedBase &&
                        alignedBase + alignedSize <= posixAddress + reserveSize)
                    {
                        actualAddress = alignedBase;
                        UpdateAllocationSearchCursor(desiredAddress, effectiveAlignment, executable, alignedBase + alignedSize);
                        return true;
                    }

                    ReleaseUntrackedAllocation(posixAddress);
                }
            }
            catch
            {
            }

            return false;
        }

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

    private bool TryFindHostFreeCandidate(
        ulong cursor,
        ulong size,
        ulong alignment,
        out ulong candidate,
        out ulong nextCursor)
    {
        candidate = cursor;
        nextCursor = cursor;
        if (!_hostMemory.Query(cursor, out var memoryInfo) ||
            memoryInfo.RegionSize == 0)
        {
            return true;
        }

        var regionEnd = memoryInfo.RegionSize > ulong.MaxValue - memoryInfo.BaseAddress
            ? ulong.MaxValue
            : memoryInfo.BaseAddress + memoryInfo.RegionSize;
        if (memoryInfo.State == HostRegionState.Free)
        {
            var freeCandidate = AlignUp(Math.Max(cursor, memoryInfo.BaseAddress), alignment);
            // The POSIX shadow table cannot describe unrelated Linux mappings
            // after its final tracked region, so it conservatively reports one
            // free page there. MAP_FIXED_NOREPLACE still makes a larger exact
            // probe safe: it fails without replacing any host mapping. Accept
            // the candidate and let AllocateAt perform that authoritative probe.
            var canProbeBeyondReportedFreeRun =
                OperatingSystem.IsLinux() &&
                freeCandidate < regionEnd;
            if (freeCandidate <= regionEnd &&
                (size <= regionEnd - freeCandidate ||
                 canProbeBeyondReportedFreeRun))
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

    private void ReleaseUntrackedAllocation(ulong address)
    {
        _gate.EnterWriteLock();
        try
        {
            for (var i = 0; i < _regions.Count; i++)
            {
                if (_regions[i].VirtualAddress == address)
                {
                    _regions.RemoveAt(i);
                    break;
                }
            }
        }
        finally
        {
            _gate.ExitWriteLock();
        }

        _hostMemory.Free(address);
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
            if (_guestAllocator.TryAllocate(size, alignment, out address))
            {
                return true;
            }

            if (!TryCreateGuestAllocationArena(size, alignment) ||
                !_guestAllocator.TryAllocate(size, alignment, out address))
            {
                return false;
            }

            return true;
        }
    }

    public bool TryFreeGuestMemory(ulong address)
    {
        ThrowIfDisposed();
        if (address == 0)
        {
            return false;
        }

        lock (_guestAllocationGate)
        {
            return _guestAllocator.TryFree(address);
        }
    }

    private bool TryCreateGuestAllocationArena(ulong size, ulong alignment)
    {
        try
        {
            var minimumArenaSize = checked(Math.Max(GuestAllocationArenaStartOffset, alignment) + size);
            var arenaSize = AlignUp(
                Math.Max(GuestAllocationArenaSize, minimumArenaSize),
                HostAllocationGranularity);
            var arenaBase = AllocateAt(
                GuestAllocationArenaAddress,
                arenaSize,
                executable: false,
                allowAlternative: true);

            return _guestAllocator.TryAddArena(
                arenaBase,
                arenaSize,
                GuestAllocationArenaStartOffset);
        }
        catch (Exception)
        {
            return false;
        }
    }

    public bool TryProtect(ulong address, ulong size, GuestPageProtection protection)
    {
        ThrowIfDisposed();
        if (size == 0 || address > ulong.MaxValue - size)
        {
            return false;
        }

        var endAddress = address + size;
        if (endAddress > ulong.MaxValue - (PageSize - 1))
        {
            return false;
        }

        var pageStart = AlignDown(address, PageSize);
        var pageEnd = AlignUp(endAddress, PageSize);
        var pageLength = pageEnd - pageStart;
        var pageProtection = ResolveProgramHeaderProtection(protection);

        _gate.EnterWriteLock();
        try
        {
            if (!TryResolveRange(pageStart, pageLength, out _))
            {
                return false;
            }

            if (!_hostMemory.Protect(pageStart, pageLength, ResolveProtection(protection), out _))
            {
                return false;
            }

            for (var pageAddress = pageStart; pageAddress < pageEnd; pageAddress += PageSize)
            {
                _pageProtections[pageAddress] = pageProtection;
            }

            if ((protection & GuestPageProtection.Execute) != 0)
            {
                _hostMemory.FlushInstructionCache(pageStart, pageLength);
            }

            return true;
        }
        finally
        {
            _gate.ExitWriteLock();
        }
    }

    // Reproduces the decomposition KernelMemoryCompatExports.ResolveHostProtection
    // performed before this seam existed; the Windows backend maps each case back
    // to the identical PAGE_* value.
    private static HostPageProtection ResolveProtection(GuestPageProtection protection)
    {
        var read = (protection & GuestPageProtection.Read) != 0;
        var write = (protection & GuestPageProtection.Write) != 0;
        var execute = (protection & GuestPageProtection.Execute) != 0;

        if (execute)
        {
            return write
                ? HostPageProtection.ReadWriteExecute
                : read
                    ? HostPageProtection.ReadExecute
                    : HostPageProtection.Execute;
        }

        return write
            ? HostPageProtection.ReadWrite
            : read
                ? HostPageProtection.ReadOnly
                : HostPageProtection.NoAccess;
    }

    private static ProgramHeaderFlags ResolveProgramHeaderProtection(GuestPageProtection protection)
    {
        var result = ProgramHeaderFlags.None;
        if ((protection & GuestPageProtection.Read) != 0)
        {
            result |= ProgramHeaderFlags.Read;
        }

        if ((protection & GuestPageProtection.Write) != 0)
        {
            result |= ProgramHeaderFlags.Write;
        }

        if ((protection & GuestPageProtection.Execute) != 0)
        {
            result |= ProgramHeaderFlags.Execute;
        }

        return result;
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
                    _hostMemory.Free(region.VirtualAddress);
                }
                _regions.Clear();
                _stackRanges.Clear();
                _imageRegions.Clear();
                _pageProtections.Clear();
                lock (_allocationSearchHintGate)
                {
                    _allocationSearchHints.Clear();
                }
                _resetVersion++;
                if (_resetVersion == 0)
                {
                    _resetVersion = 1;
                }
            }
            finally
            {
                _gate.ExitWriteLock();
            }

            _guestAllocator.Clear();
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
        var allocationStart = AlignDown(mapStart, HostAllocationGranularity);
        var allocationEnd = AlignUp(mapEnd, HostAllocationGranularity);
        var allocationSize = checked(allocationEnd - allocationStart);

        _gate.EnterWriteLock();
        try
        {
            var existingRegion = FindRegion(mapStart, mapSize);
            if (existingRegion == null)
            {
                var isExecutable = (protection & ProgramHeaderFlags.Execute) != 0;
                AllocateAt(allocationStart, allocationSize, isExecutable, allowAlternative: false);
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
        var runStart = mapStart;
        var runFlags = ProgramHeaderFlags.None;
        for (var pageAddress = mapStart; pageAddress < mapEnd; pageAddress += PageSize)
        {
            _pageProtections.TryGetValue(pageAddress, out var existingFlags);
            var mergedFlags = existingFlags | flags;
            _pageProtections[pageAddress] = mergedFlags;

            if (pageAddress == mapStart)
            {
                runFlags = mergedFlags;
                continue;
            }

            if (mergedFlags == runFlags)
            {
                continue;
            }

            SetProtection(runStart, pageAddress - runStart, runFlags);
            runStart = pageAddress;
            runFlags = mergedFlags;
        }

        SetProtection(runStart, mapEnd - runStart, runFlags);
    }

    private void SetProtection(ulong address, ulong size, ProgramHeaderFlags flags)
    {
        ProtectionApplicationCount++;
        HostPageProtection protection;

        if (flags == ProgramHeaderFlags.None)
        {
            protection = HostPageProtection.NoAccess;
        }
        else if ((flags & ProgramHeaderFlags.Execute) != 0)
        {
            protection = (flags & ProgramHeaderFlags.Write) != 0
                ? HostPageProtection.ReadWriteExecute
                : HostPageProtection.ReadExecute;
        }
        else if ((flags & ProgramHeaderFlags.Write) != 0)
        {
            protection = HostPageProtection.ReadWrite;
        }
        else
        {
            protection = HostPageProtection.ReadOnly;
        }

        if (!_hostMemory.Protect(address, size, protection, out _))
        {
            throw new InvalidOperationException($"Failed to set memory protection at 0x{address:X16}");
        }

        if ((flags & ProgramHeaderFlags.Execute) != 0)
        {
            _hostMemory.FlushInstructionCache(address, size);
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

    public void RegisterImage(IReadOnlyList<VirtualMemoryRegion> regions)
    {
        ThrowIfDisposed();
        _gate.EnterWriteLock();
        try
        {
            _imageRegions.Register(regions);
        }
        finally
        {
            _gate.ExitWriteLock();
        }
    }

    public bool TryGetImageRegions(
        ulong address,
        out IReadOnlyList<VirtualMemoryRegion> regions)
    {
        ThrowIfDisposed();
        _gate.EnterReadLock();
        try
        {
            return _imageRegions.TryGet(address, out regions);
        }
        finally
        {
            _gate.ExitReadLock();
        }
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
            var regionIndex = FindQueryRegionIndex(address, findNext, out _);
            if (regionIndex >= 0)
            {
                var matchedRegion = _regions[regionIndex];
                var queryAddress = address >= matchedRegion.VirtualAddress &&
                    address - matchedRegion.VirtualAddress < matchedRegion.Size
                        ? address
                        : matchedRegion.VirtualAddress;
                region = ToGuestMemoryRegion(matchedRegion, queryAddress);
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

    internal int CountRegionQueryProbes(ulong address, bool findNext)
    {
        ThrowIfDisposed();
        _gate.EnterReadLock();
        try
        {
            _ = FindQueryRegionIndex(address, findNext, out var probes);
            return probes;
        }
        finally
        {
            _gate.ExitReadLock();
        }
    }

    private int FindQueryRegionIndex(ulong address, bool findNext, out int probes)
    {
        probes = 0;
        var low = 0;
        var high = _regions.Count - 1;
        var candidateIndex = -1;
        while (low <= high)
        {
            probes++;
            var middle = low + ((high - low) >> 1);
            if (_regions[middle].VirtualAddress <= address)
            {
                candidateIndex = middle;
                low = middle + 1;
            }
            else
            {
                high = middle - 1;
            }
        }

        if (candidateIndex >= 0)
        {
            var candidate = _regions[candidateIndex];
            if (address - candidate.VirtualAddress < candidate.Size)
            {
                return candidateIndex;
            }
        }

        if (!findNext)
        {
            return -1;
        }

        var nextIndex = candidateIndex + 1;
        return nextIndex < _regions.Count ? nextIndex : -1;
    }

    public bool TryRead(ulong virtualAddress, Span<byte> destination)
    {
        ThrowIfDisposed();
        var requiresExclusiveAccess = false;
        _gate.EnterReadLock();
        try
        {
            var size = (ulong)destination.Length;
            if (!TryResolveRange(virtualAddress, size, out var firstRegionIndex))
            {
                return false;
            }

            if (destination.IsEmpty)
            {
                return true;
            }

            if (!TryPrepareRangeForAccess(
                    virtualAddress,
                    size,
                    firstRegionIndex,
                    write: false,
                    out requiresExclusiveAccess))
            {
                return false;
            }

            if (!requiresExclusiveAccess)
            {
                fixed (byte* destPtr = destination)
                {
                    Buffer.MemoryCopy(
                        (void*)virtualAddress,
                        destPtr,
                        (nuint)destination.Length,
                        (nuint)destination.Length);
                }

                return true;
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

    public bool TryCompare(ulong virtualAddress, ReadOnlySpan<byte> expected)
    {
        ThrowIfDisposed();
        var requiresExclusiveAccess = false;
        _gate.EnterReadLock();
        try
        {
            var size = (ulong)expected.Length;
            if (!TryResolveRange(virtualAddress, size, out var firstRegionIndex))
            {
                return false;
            }

            if (expected.IsEmpty)
            {
                return true;
            }

            if (!TryPrepareRangeForAccess(
                    virtualAddress,
                    size,
                    firstRegionIndex,
                    write: false,
                    out requiresExclusiveAccess))
            {
                return false;
            }

            if (!requiresExclusiveAccess)
            {
                return new ReadOnlySpan<byte>((void*)virtualAddress, expected.Length).SequenceEqual(expected);
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
            return TryCompareExclusive(virtualAddress, expected);
        }
        finally
        {
            _gate.ExitWriteLock();
        }
    }

    public bool TryWrite(ulong virtualAddress, ReadOnlySpan<byte> source)
    {
        ThrowIfDisposed();
        // Managed writes cannot resume through the native page-fault bridge.
        // Pre-visit tracked image pages so their owners are dirtied and the
        // pages become writable before the copy.
        GuestImageWriteTracker.NotifyManagedWrite(
            virtualAddress,
            (ulong)source.Length);

        var requiresExclusiveAccess = false;
        _gate.EnterReadLock();
        try
        {
            var size = (ulong)source.Length;
            if (!TryResolveRange(virtualAddress, size, out var firstRegionIndex))
            {
                return false;
            }

            if (source.IsEmpty)
            {
                return true;
            }

            if (!TryPrepareRangeForAccess(
                    virtualAddress,
                    size,
                    firstRegionIndex,
                    write: true,
                    out requiresExclusiveAccess))
            {
                return false;
            }

            if (!requiresExclusiveAccess)
            {
                fixed (byte* srcPtr = source)
                {
                    Buffer.MemoryCopy(
                        srcPtr,
                        (void*)virtualAddress,
                        (nuint)source.Length,
                        (nuint)source.Length);
                }

                return true;
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
        if (!TryPrepareRangeForExclusiveAccess(
                virtualAddress,
                (ulong)destination.Length,
                write: false,
                out var touchedPages))
        {
            return false;
        }

        var readSucceeded = false;
        var protectionsRestored = false;
        try
        {
            fixed (byte* destPtr = destination)
            {
                Buffer.MemoryCopy(
                    (void*)virtualAddress,
                    destPtr,
                    (nuint)destination.Length,
                    (nuint)destination.Length);
            }
            readSucceeded = true;
        }
        finally
        {
            protectionsRestored = RestorePageProtections(touchedPages);
        }

        return readSucceeded && protectionsRestored;
    }

    private bool TryCompareExclusive(ulong virtualAddress, ReadOnlySpan<byte> expected)
    {
        if (!TryPrepareRangeForExclusiveAccess(
                virtualAddress,
                (ulong)expected.Length,
                write: false,
                out var touchedPages))
        {
            return false;
        }

        var matches = false;
        var protectionsRestored = false;
        try
        {
            matches = new ReadOnlySpan<byte>((void*)virtualAddress, expected.Length).SequenceEqual(expected);
        }
        finally
        {
            protectionsRestored = RestorePageProtections(touchedPages);
        }

        return protectionsRestored && matches;
    }

    private bool TryWriteExclusive(ulong virtualAddress, ReadOnlySpan<byte> source)
    {
        if (!TryPrepareRangeForExclusiveAccess(
                virtualAddress,
                (ulong)source.Length,
                write: true,
                out var touchedPages))
        {
            return false;
        }

        var memoryModified = false;
        var protectionsRestored = false;
        try
        {
            memoryModified = true;
            fixed (byte* srcPtr = source)
            {
                Buffer.MemoryCopy(
                    srcPtr,
                    (void*)virtualAddress,
                    (nuint)source.Length,
                    (nuint)source.Length);
            }
        }
        finally
        {
            protectionsRestored = RestorePageProtections(touchedPages);
            if (memoryModified)
            {
                FlushModifiedExecutablePages(touchedPages);
            }
        }

        return protectionsRestored;
    }

    private bool TryPrepareRangeForExclusiveAccess(
        ulong virtualAddress,
        ulong size,
        bool write,
        out List<(ulong Address, uint Protection)> touchedPages)
    {
        touchedPages = new List<(ulong Address, uint Protection)>();
        if (!TryResolveRange(virtualAddress, size, out var regionIndex))
        {
            return false;
        }

        var address = virtualAddress;
        var remaining = size;
        while (remaining != 0)
        {
            var region = _regions[regionIndex++];
            var length = GetRangeLengthInRegion(address, remaining, region);
            if (!EnsureRangeCommitted(address, length, region))
            {
                _ = RestorePageProtections(touchedPages);
                touchedPages.Clear();
                return false;
            }

            var canAccess = write
                ? CanWriteWithoutProtectionChange(address, length, region)
                : CanReadWithoutProtectionChange(address, length, region);
            if (!canAccess)
            {
                if (!TryTemporarilyProtectForAccess(address, length, region, out var regionPages))
                {
                    _ = RestorePageProtections(touchedPages);
                    touchedPages.Clear();
                    return false;
                }

                touchedPages.AddRange(regionPages);
            }

            address += length;
            remaining -= length;
        }

        return true;
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
            var region = FindRegion(virtualAddress, 1);
            if (region is null ||
                (region.IsReservedOnly && !EnsureRangeCommitted(virtualAddress, 1, region)))
            {
                return null;
            }

            return (void*)virtualAddress;
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
            return TryResolveRange(virtualAddress, size, out _);
        }
        finally
        {
            _gate.ExitReadLock();
        }
    }

    private bool TryPrepareRangeForAccess(
        ulong virtualAddress,
        ulong size,
        int firstRegionIndex,
        bool write,
        out bool requiresExclusiveAccess)
    {
        requiresExclusiveAccess = false;
        var address = virtualAddress;
        var remaining = size;
        var regionIndex = firstRegionIndex;
        while (remaining != 0)
        {
            var region = _regions[regionIndex++];
            var length = GetRangeLengthInRegion(address, remaining, region);
            if (region.IsReservedOnly && !EnsureRangeCommitted(address, length, region))
            {
                return false;
            }

            if (write
                    ? !CanWriteWithoutProtectionChange(address, length, region)
                    : !CanReadWithoutProtectionChange(address, length, region))
            {
                requiresExclusiveAccess = true;
            }

            address += length;
            remaining -= length;
        }

        return true;
    }

    private bool TryResolveRange(ulong address, ulong size, out int firstRegionIndex)
    {
        firstRegionIndex = -1;
        var low = 0;
        var high = _regions.Count - 1;
        while (low <= high)
        {
            var middle = low + ((high - low) >> 1);
            if (_regions[middle].VirtualAddress <= address)
            {
                firstRegionIndex = middle;
                low = middle + 1;
            }
            else
            {
                high = middle - 1;
            }
        }

        if (firstRegionIndex < 0)
        {
            return false;
        }

        var firstRegion = _regions[firstRegionIndex];
        var firstOffset = address - firstRegion.VirtualAddress;
        if (size == 0)
        {
            return firstOffset <= firstRegion.Size;
        }

        if (firstOffset >= firstRegion.Size)
        {
            return false;
        }

        var regionIndex = firstRegionIndex;
        var currentAddress = address;
        var remaining = size;
        while (remaining != 0)
        {
            if (regionIndex >= _regions.Count)
            {
                return false;
            }

            var region = _regions[regionIndex++];
            if (currentAddress < region.VirtualAddress)
            {
                return false;
            }

            var offset = currentAddress - region.VirtualAddress;
            if (offset >= region.Size)
            {
                return false;
            }

            var length = Math.Min(remaining, region.Size - offset);
            currentAddress += length;
            remaining -= length;
        }

        return true;
    }

    private static ulong GetRangeLengthInRegion(
        ulong address,
        ulong remaining,
        MemoryRegion region)
    {
        var offset = address - region.VirtualAddress;
        return Math.Min(remaining, region.Size - offset);
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

    private static HostPageProtection GetCommitProtection(MemoryRegion region)
    {
        return region.IsExecutable ? HostPageProtection.ReadWriteExecute : HostPageProtection.ReadWrite;
    }

    private bool EnsureRangeCommitted(ulong address, ulong size, MemoryRegion region)
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
            if (!_hostMemory.Query(pageAddress, out var info))
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

            if (info.State == HostRegionState.Committed)
            {
                pageAddress = rangeEnd;
                continue;
            }

            if (info.State != HostRegionState.Reserved)
            {
                return false;
            }

            var commitSize = rangeEnd - pageAddress;
            if (!_hostMemory.Commit(pageAddress, commitSize, commitProtection))
            {
                return false;
            }

            pageAddress = rangeEnd;
        }

        return true;
    }

    private bool TryTemporarilyProtectForAccess(
        ulong address,
        ulong size,
        MemoryRegion region,
        out List<(ulong Address, uint Protection)> touchedPages)
    {
        touchedPages = new List<(ulong Address, uint Protection)>();

        var startPage = AlignDown(address, PageSize);
        var endPage = AlignUp(address + size, PageSize);
        var temporaryProtection = region.IsExecutable ? HostPageProtection.ReadWriteExecute : HostPageProtection.ReadWrite;

        for (var pageAddress = startPage; pageAddress < endPage; pageAddress += PageSize)
        {
            if (!_hostMemory.Protect(pageAddress, PageSize, temporaryProtection, out var oldProtection))
            {
                _ = RestorePageProtections(touchedPages);
                touchedPages.Clear();
                return false;
            }

            touchedPages.Add((pageAddress, oldProtection));
        }

        return true;
    }

    private bool RestorePageProtections(List<(ulong Address, uint Protection)> touchedPages)
    {
        var restored = true;
        for (var index = touchedPages.Count - 1; index >= 0; index--)
        {
            var (pageAddress, protection) = touchedPages[index];
            if (!_hostMemory.ProtectRaw(pageAddress, PageSize, protection, out _))
            {
                restored = false;
            }
        }

        return restored;
    }

    private void FlushModifiedExecutablePages(
        List<(ulong Address, uint Protection)> touchedPages)
    {
        foreach (var (pageAddress, protection) in touchedPages)
        {
            if (IsExecutableProtection(protection))
            {
                _hostMemory.FlushInstructionCache(pageAddress, PageSize);
            }
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

    private static bool TryAlignAllocationSize(ulong size, out ulong alignedSize)
    {
        try
        {
            alignedSize = AlignUp(size, PageSize);
            return true;
        }
        catch (OverflowException)
        {
            alignedSize = 0;
            return false;
        }
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
}
