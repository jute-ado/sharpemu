// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Core.Loader;
using SharpEmu.Core.Memory;
using SharpEmu.HLE.Host;
using Xunit;

namespace SharpEmu.Core.Tests;

[Collection(PhysicalVirtualMemoryTestCollection.Name)]
public sealed class PhysicalVirtualMemoryTests
{
    private const ulong OneGibibyte = 0x4000_0000UL;
    private const ulong HostBlockerSize = 0x2000_0000UL;
    private const ulong HostAllocationGranularity = 0x1_0000UL;
    private const ulong GuestAllocationArenaSize = 0x0100_0000UL;

    private static IHostMemory CreateHostMemory() => TestHostMemory.Create();

    private static PhysicalVirtualMemory CreatePhysicalMemory() =>
        TestHostMemory.CreatePhysicalMemory();

    [Fact]
    public void AllocatedRegionSupportsBoundedReadWriteAndEmptyEndAccess()
    {
        using var memory = CreatePhysicalMemory();
        var address = memory.AllocateAt(0, 0x2000, executable: false);
        var payload = new byte[] { 1, 2, 3, 4 };

        Assert.True(memory.TryWrite(address + 0x100, payload));
        Span<byte> read = stackalloc byte[payload.Length];
        Assert.True(memory.TryRead(address + 0x100, read));
        Assert.Equal(payload, read.ToArray());
        Assert.True(memory.IsAccessible(address, 0x2000));
        Assert.True(memory.IsAccessible(address + 0x2000, 0));
        Assert.True(memory.TryRead(address + 0x2000, Span<byte>.Empty));
        Assert.True(memory.TryWrite(address + 0x2000, ReadOnlySpan<byte>.Empty));

        Span<byte> crossing = stackalloc byte[2];
        Assert.False(memory.TryRead(address + 0x1FFF, crossing));
        Assert.False(memory.TryWrite(address + 0x1FFF, crossing));
        Assert.False(memory.IsAccessible(address + 0x2000, 1));
    }

    [Fact]
    public void AdjacentAllocationsSupportCrossBoundaryAccess()
    {
        using var hostMemory = new ContiguousHostMemory(
            2 * HostAllocationGranularity);
        using var memory = new PhysicalVirtualMemory(hostMemory);
        var firstAddress = hostMemory.BaseAddress;
        var secondAddress = firstAddress + HostAllocationGranularity;
        Assert.Equal(
            firstAddress,
            memory.AllocateAt(
                firstAddress,
                HostAllocationGranularity,
                executable: false,
                allowAlternative: false));
        Assert.Equal(
            secondAddress,
            memory.AllocateAt(
                secondAddress,
                HostAllocationGranularity,
                executable: false,
                allowAlternative: false));
        Assert.True(memory.TryWrite(secondAddress - 1, [0x11]));
        Assert.True(memory.TryWrite(secondAddress, [0x22]));
        Span<byte> crossing = stackalloc byte[2];

        Assert.True(memory.IsAccessible(secondAddress - 1, 2));
        Assert.True(memory.TryRead(secondAddress - 1, crossing));
        Assert.Equal(new byte[] { 0x11, 0x22 }, crossing.ToArray());
        Assert.True(memory.TryCompare(secondAddress - 1, [0x11, 0x22]));
        Assert.True(memory.TryWrite(secondAddress - 1, [0xAA, 0xBB]));
        Assert.True(memory.TryRead(secondAddress - 1, crossing));
        Assert.Equal(new byte[] { 0xAA, 0xBB }, crossing.ToArray());

        memory.Map(
            secondAddress - 0x1000,
            0x1000,
            fileOffset: 0,
            fileData: [],
            ProgramHeaderFlags.Read | ProgramHeaderFlags.Execute);
        var firstProtection = QueryHostPage(
            hostMemory,
            secondAddress - 1).Protection;
        var secondProtection = QueryHostPage(
            hostMemory,
            secondAddress).Protection;
        Assert.NotEqual(firstProtection, secondProtection);
        Assert.True(memory.TryWrite(secondAddress - 1, [0xCC, 0xDD]));
        Assert.Equal(
            firstProtection,
            QueryHostPage(hostMemory, secondAddress - 1).Protection);
        Assert.Equal(
            secondProtection,
            QueryHostPage(hostMemory, secondAddress).Protection);
        Assert.True(memory.TryRead(secondAddress - 1, crossing));
        Assert.Equal(new byte[] { 0xCC, 0xDD }, crossing.ToArray());
    }

    [Fact]
    public void AllocationGapRejectsCrossingWriteWithoutPartialMutation()
    {
        using var hostMemory = new ContiguousHostMemory(
            3 * HostAllocationGranularity);
        using var memory = new PhysicalVirtualMemory(hostMemory);
        var firstAddress = hostMemory.BaseAddress;
        var thirdAddress = firstAddress + (2 * HostAllocationGranularity);
        Assert.Equal(
            firstAddress,
            memory.AllocateAt(
                firstAddress,
                HostAllocationGranularity,
                executable: false,
                allowAlternative: false));
        Assert.Equal(
            thirdAddress,
            memory.AllocateAt(
                thirdAddress,
                HostAllocationGranularity,
                executable: false,
                allowAlternative: false));
        Assert.True(memory.TryWrite(firstAddress + HostAllocationGranularity - 1, [0x11]));
        Assert.True(memory.TryWrite(thirdAddress, [0x22]));
        var crossing = new byte[checked((int)HostAllocationGranularity + 2)];
        crossing.AsSpan().Fill(0xCC);
        var readDestination = new byte[crossing.Length];
        readDestination.AsSpan().Fill(0xDD);

        Assert.False(memory.IsAccessible(firstAddress + HostAllocationGranularity - 1, (ulong)crossing.Length));
        Assert.False(memory.TryRead(firstAddress + HostAllocationGranularity - 1, readDestination));
        Assert.All(readDestination, value => Assert.Equal(0xDD, value));
        Assert.False(memory.TryCompare(firstAddress + HostAllocationGranularity - 1, crossing));
        Assert.False(memory.TryWrite(firstAddress + HostAllocationGranularity - 1, crossing));

        Span<byte> edges = stackalloc byte[2];
        Assert.True(memory.TryRead(firstAddress + HostAllocationGranularity - 1, edges[..1]));
        Assert.True(memory.TryRead(thirdAddress, edges[1..]));
        Assert.Equal(new byte[] { 0x11, 0x22 }, edges.ToArray());
    }

    [Fact]
    public void CompareMatchesMappedBytesWithoutCrossingRegionBounds()
    {
        using var memory = CreatePhysicalMemory();
        var address = memory.AllocateAt(0, 0x2000, executable: false);
        byte[] payload = [1, 2, 3, 4];
        Assert.True(memory.TryWrite(address + 0x100, payload));

        Assert.True(memory.TryCompare(address + 0x100, payload));
        Assert.False(memory.TryCompare(address + 0x100, [1, 2, 3, 5]));
        Assert.False(memory.TryCompare(address + 0x1FFF, [0, 0]));
        Assert.True(memory.TryCompare(address + 0x2000, []));
        Assert.False(memory.TryCompare(address + 0x2000, [0]));
    }

    [Fact]
    public void CompareTemporarilyReadsExecuteOnlyMemoryAndRestoresProtection()
    {
        var hostMemory = CreateHostMemory();
        using var memory = new PhysicalVirtualMemory(hostMemory);
        var address = memory.AllocateAt(0, 0x1000, executable: true);
        byte[] payload = [1, 2, 3, 4];
        memory.Map(
            address,
            0x1000,
            fileOffset: 0,
            fileData: payload,
            ProgramHeaderFlags.Execute);
        var originalProtection = QueryHostPage(hostMemory, address).Protection;

        Assert.True(memory.TryCompare(address, payload));
        Assert.Equal(originalProtection, QueryHostPage(hostMemory, address).Protection);
        Assert.False(memory.TryCompare(address, [1, 2, 3, 5]));
        Assert.Equal(originalProtection, QueryHostPage(hostMemory, address).Protection);
    }

    [Fact]
    public void TemporaryAccessOperationsReportProtectionRestoreFailure()
    {
        var hostMemory = new FailingRawProtectionHostMemory(CreateHostMemory());
        using var memory = new PhysicalVirtualMemory(hostMemory);
        var address = memory.AllocateAt(0, 0x1000, executable: true);
        byte[] payload = [1, 2, 3, 4];
        memory.Map(
            address,
            0x1000,
            fileOffset: 0,
            fileData: payload,
            ProgramHeaderFlags.Execute);
        hostMemory.FailNextRawProtection = true;

        Assert.False(memory.TryCompare(address, payload));
        Span<byte> read = stackalloc byte[payload.Length];
        hostMemory.FailNextRawProtection = true;
        Assert.False(memory.TryRead(address, read));
        Assert.Equal(payload, read.ToArray());
        hostMemory.FailNextRawProtection = true;
        Assert.False(memory.TryWrite(address, [5, 6, 7, 8]));
        Assert.Equal(3, hostMemory.FailedRawProtectionCalls);
    }

    [Fact]
    public void MappingCopiesPayloadZeroFillsSegmentAndPreservesAdjacentBytes()
    {
        using var memory = CreatePhysicalMemory();
        var address = memory.AllocateAt(0, 0x2000, executable: false);
        var initial = Enumerable.Repeat((byte)0xCC, 0x2000).ToArray();
        Assert.True(memory.TryWrite(address, initial));

        memory.Map(
            address + 0x100,
            0x500,
            fileOffset: 0x80,
            fileData: new byte[] { 1, 2, 3, 4 },
            ProgramHeaderFlags.Read | ProgramHeaderFlags.Execute);

        Span<byte> prefix = stackalloc byte[1];
        Assert.True(memory.TryRead(address + 0xFF, prefix));
        Assert.Equal(0xCC, prefix[0]);
        Span<byte> segment = stackalloc byte[0x500];
        Assert.True(memory.TryRead(address + 0x100, segment));
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, segment[..4].ToArray());
        Assert.All(segment[4..].ToArray(), value => Assert.Equal(0, value));
        Span<byte> suffix = stackalloc byte[1];
        Assert.True(memory.TryRead(address + 0x600, suffix));
        Assert.Equal(0xCC, suffix[0]);

        Assert.True(memory.TryQueryMemoryRegion(address + 0x100, findNext: false, out var region));
        Assert.Equal(0x05, region.Protection);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void HostWriteAcrossMixedPageProtectionsRestoresEveryPage(bool executableFirst)
    {
        var hostMemory = CreateHostMemory();
        using var memory = new PhysicalVirtualMemory(hostMemory);
        var hostPageSize = checked((ulong)Environment.SystemPageSize);
        var address = memory.AllocateAt(0, 2 * hostPageSize, executable: true);
        var executableProtection = ProgramHeaderFlags.Read | ProgramHeaderFlags.Execute;
        var writableProtection = ProgramHeaderFlags.Read | ProgramHeaderFlags.Write;
        memory.Map(
            address,
            hostPageSize,
            fileOffset: 0,
            fileData: [],
            executableFirst ? executableProtection : writableProtection);
        memory.Map(
            address + hostPageSize,
            hostPageSize,
            fileOffset: 0,
            fileData: [],
            executableFirst ? writableProtection : executableProtection);
        var originalFirst = QueryHostPage(hostMemory, address).Protection;
        var originalSecond = QueryHostPage(hostMemory, address + hostPageSize).Protection;
        Assert.NotEqual(originalFirst, originalSecond);

        Assert.True(memory.TryWrite(address + hostPageSize - 1, [0xAA, 0xBB]));

        Assert.Equal(originalFirst, QueryHostPage(hostMemory, address).Protection);
        Assert.Equal(originalSecond, QueryHostPage(hostMemory, address + hostPageSize).Protection);
        Span<byte> actual = stackalloc byte[2];
        Assert.True(memory.TryRead(address + hostPageSize - 1, actual));
        Assert.Equal(new byte[] { 0xAA, 0xBB }, actual.ToArray());
    }

    [Fact]
    public void GuestAllocatorHonorsAlignmentAndReturnsDistinctRanges()
    {
        using var memory = CreatePhysicalMemory();

        Assert.True(memory.TryAllocateGuestMemory(3, 0x1000, out var first));
        Assert.True(memory.TryAllocateGuestMemory(5, 0x4000, out var second));
        Assert.Equal(0UL, first & 0xFFF);
        Assert.Equal(0UL, second & 0x3FFF);
        Assert.True(second >= first + 3);
        Assert.True(memory.TryWrite(first, new byte[] { 1, 2, 3 }));
        Assert.True(memory.TryWrite(second, new byte[] { 4, 5, 6, 7, 8 }));
    }

    [Fact]
    public void GuestAllocatorReusesFreedRangeWithRequestedAlignment()
    {
        using var memory = CreatePhysicalMemory();

        Assert.True(memory.TryAllocateGuestMemory(0x80, 0x10, out var padding));
        Assert.True(memory.TryAllocateGuestMemory(0x180, 0x10, out var freed));
        Assert.True(memory.TryAllocateGuestMemory(0x80, 0x10, out var guard));
        Assert.Equal(padding + 0x80, freed);
        Assert.True(memory.TryFreeGuestMemory(freed));

        Assert.True(memory.TryAllocateGuestMemory(0x80, 0x100, out var aligned));
        Assert.True(memory.TryAllocateGuestMemory(0x40, 0x10, out var prefix));

        Assert.Equal(freed + 0x80, aligned);
        Assert.Equal(freed, prefix);
        Assert.True(guard >= aligned + 0x80);
        Assert.Single(memory.SnapshotRegions());
    }

    [Fact]
    public void GuestAllocatorCoalescesAdjacentFreedRanges()
    {
        using var memory = CreatePhysicalMemory();

        Assert.True(memory.TryAllocateGuestMemory(0x100, 0x10, out var first));
        Assert.True(memory.TryAllocateGuestMemory(0x100, 0x10, out var second));
        Assert.True(memory.TryAllocateGuestMemory(0x100, 0x10, out var third));
        Assert.Equal(first + 0x100, second);
        Assert.Equal(second + 0x100, third);

        Assert.True(memory.TryFreeGuestMemory(second));
        Assert.True(memory.TryFreeGuestMemory(first));
        Assert.True(memory.TryAllocateGuestMemory(0x180, 0x10, out var coalesced));

        Assert.Equal(first, coalesced);
        Assert.Equal(third, coalesced + 0x200);
    }

    [Fact]
    public void GuestAllocatorRejectsInteriorUnknownAndDoubleFrees()
    {
        using var memory = CreatePhysicalMemory();

        Assert.True(memory.TryAllocateGuestMemory(0x100, 0x10, out var address));

        Assert.False(memory.TryFreeGuestMemory(0));
        Assert.False(memory.TryFreeGuestMemory(address + 1));
        Assert.False(memory.TryFreeGuestMemory(address + 0x100));
        Assert.True(memory.TryFreeGuestMemory(address));
        Assert.False(memory.TryFreeGuestMemory(address));
    }

    [Fact]
    public void GuestAllocatorGrowsAfterCurrentArenaIsExhausted()
    {
        using var memory = CreatePhysicalMemory();
        var firstSize = GuestAllocationArenaSize - 0x1000;

        Assert.True(memory.TryAllocateGuestMemory(firstSize, 0x1000, out var first));
        Assert.True(memory.TryAllocateGuestMemory(1, 0x1000, out var second));
        Assert.True(second >= first + firstSize || first >= second + 1);
        Assert.Equal(2, memory.SnapshotRegions().Count);
        Assert.True(memory.TryWrite(first + firstSize - 1, [0xA5]));
        Assert.True(memory.TryWrite(second, [0x5A]));

        Span<byte> firstTail = stackalloc byte[1];
        Span<byte> secondContents = stackalloc byte[1];
        Assert.True(memory.TryRead(first + firstSize - 1, firstTail));
        Assert.True(memory.TryRead(second, secondContents));
        Assert.Equal(0xA5, firstTail[0]);
        Assert.Equal(0x5A, secondContents[0]);
    }

    [Fact]
    public void GuestAllocatorCreatesArenaLargeEnoughForOversizedRequest()
    {
        using var memory = CreatePhysicalMemory();
        const ulong requestedSize = GuestAllocationArenaSize + 0x20_0000;

        Assert.True(memory.TryAllocateGuestMemory(requestedSize, 0x20_000, out var address));
        Assert.Equal(0UL, address & 0x1_FFFF);
        var region = Assert.Single(memory.SnapshotRegions());
        Assert.True(region.MemorySize >= requestedSize);
        Assert.True(memory.TryWrite(address, [0x11]));
        Assert.True(memory.TryWrite(address + requestedSize - 1, [0x22]));

        Span<byte> edges = stackalloc byte[2];
        Assert.True(memory.TryRead(address, edges[..1]));
        Assert.True(memory.TryRead(address + requestedSize - 1, edges[1..]));
        Assert.Equal(new byte[] { 0x11, 0x22 }, edges.ToArray());
    }

    [Fact]
    public void GuestAllocatorRejectsInvalidRequests()
    {
        using var memory = CreatePhysicalMemory();

        Assert.False(memory.TryAllocateGuestMemory(0, 0x1000, out _));
        Assert.False(memory.TryAllocateGuestMemory(1, 0, out _));
        Assert.False(memory.TryAllocateGuestMemory(1, 3, out _));
    }

    [Fact]
    public void ClearReleasesRegionsAndAllowsFreshAllocation()
    {
        using var memory = CreatePhysicalMemory();
        var address = memory.AllocateAt(0, 0x1000, executable: false);
        Assert.True(memory.TryWrite(address, new byte[] { 1 }));
        var resetVersion = memory.ResetVersion;

        memory.Clear();

        Assert.NotEqual(resetVersion, memory.ResetVersion);
        Assert.Empty(memory.SnapshotRegions());
        Span<byte> one = stackalloc byte[1];
        Assert.False(memory.TryRead(address, one));
        var nextAddress = memory.AllocateAt(0, 0x1000, executable: false);
        Assert.True(memory.TryRead(nextAddress, one));
        Assert.Equal(0, one[0]);
    }

    private static bool TryFindReservedPage(
        IHostMemory hostMemory,
        ulong address,
        ulong size,
        out ulong reservedAddress)
    {
        var end = checked(address + size);
        var cursor = address;
        while (cursor < end)
        {
            if (!hostMemory.Query(cursor, out var information) ||
                information.RegionSize == 0)
            {
                break;
            }
            var regionEnd = information.RegionSize > ulong.MaxValue - information.BaseAddress
                ? ulong.MaxValue
                : information.BaseAddress + information.RegionSize;
            if (information.State == HostRegionState.Reserved)
            {
                reservedAddress = Math.Max(cursor, information.BaseAddress);
                return reservedAddress < end;
            }

            if (regionEnd <= cursor)
            {
                break;
            }

            cursor = regionEnd;
        }

        reservedAddress = 0;
        return false;
    }

    private static HostRegionInfo QueryHostPage(IHostMemory hostMemory, ulong address)
    {
        Assert.True(hostMemory.Query(address, out var information));
        return information;
    }

    private sealed class ContiguousHostMemory : IHostMemory, IDisposable
    {
        private readonly IHostMemory _inner = TestHostMemory.Create();
        private readonly Dictionary<ulong, ulong> _allocationSizes = [];
        private readonly ulong _reservationBase;
        private readonly ulong _reservedPrefixSize;
        private readonly ulong _size;
        private bool _disposed;

        public ContiguousHostMemory(
            ulong size,
            ulong reservedPrefixSize = 0)
        {
            if (reservedPrefixSize > size)
            {
                throw new ArgumentOutOfRangeException(nameof(reservedPrefixSize));
            }

            _size = size;
            _reservedPrefixSize = reservedPrefixSize;
            // Keep one complete address window reserved for the test's lifetime,
            // then commit individual child allocations inside it. This exercises
            // the same IHostMemory contract on Windows, Linux, and macOS without
            // letting unrelated allocations steal gaps between test mappings.
            var reservationSize = checked(size + HostAllocationGranularity);
            var reservation = _inner.Reserve(
                0,
                reservationSize,
                HostPageProtection.NoAccess);
            if (reservation == 0)
            {
                throw new OutOfMemoryException(
                    $"Could not reserve {size} bytes for the contiguous-memory test.");
            }

            _reservationBase = reservation;
            BaseAddress = (reservation + HostAllocationGranularity - 1) &
                ~(HostAllocationGranularity - 1);
        }

        public ulong BaseAddress { get; }

        public ulong Allocate(
            ulong desiredAddress,
            ulong size,
            HostPageProtection protection)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (size == 0 || size > _size)
            {
                return 0;
            }

            if (desiredAddress == 0)
            {
                desiredAddress = BaseAddress + _reservedPrefixSize;
                while (_allocationSizes.Any(allocation =>
                    RangesOverlap(
                        desiredAddress,
                        size,
                        allocation.Key,
                        allocation.Value)))
                {
                    desiredAddress = checked(desiredAddress + HostAllocationGranularity);
                }
            }

            var allocationBase = desiredAddress & ~(HostAllocationGranularity - 1);
            var allocationSize = checked(desiredAddress - allocationBase + size);
            if (desiredAddress < BaseAddress ||
                desiredAddress > BaseAddress + _size - size ||
                allocationBase < BaseAddress ||
                allocationBase < BaseAddress + _reservedPrefixSize ||
                allocationSize > BaseAddress + _size - allocationBase ||
                _allocationSizes.Any(allocation =>
                    RangesOverlap(
                        allocationBase,
                        allocationSize,
                        allocation.Key,
                        allocation.Value)))
            {
                return 0;
            }

            if (!_inner.Commit(allocationBase, allocationSize, protection))
            {
                return 0;
            }

            _allocationSizes.Add(allocationBase, allocationSize);
            return allocationBase;
        }

        private static bool RangesOverlap(
            ulong firstAddress,
            ulong firstSize,
            ulong secondAddress,
            ulong secondSize) =>
            firstAddress < secondAddress + secondSize &&
            secondAddress < firstAddress + firstSize;

        public ulong Reserve(
            ulong desiredAddress,
            ulong size,
            HostPageProtection protection)
        {
            _ = desiredAddress;
            _ = size;
            _ = protection;
            return 0;
        }

        public bool Commit(
            ulong address,
            ulong size,
            HostPageProtection protection) =>
            _inner.Commit(address, size, protection);

        public bool Free(ulong address)
        {
            // Child regions stay committed until the owning reservation is
            // released. PhysicalVirtualMemory still observes successful frees,
            // while this fixture retains a stable contiguous window.
            return _allocationSizes.Remove(address);
        }

        public bool Protect(
            ulong address,
            ulong size,
            HostPageProtection protection,
            out uint rawOldProtection) =>
            _inner.Protect(address, size, protection, out rawOldProtection);

        public bool ProtectRaw(
            ulong address,
            ulong size,
            uint rawProtection,
            out uint rawOldProtection) =>
            _inner.ProtectRaw(address, size, rawProtection, out rawOldProtection);

        public bool Query(ulong address, out HostRegionInfo info)
        {
            if (!_inner.Query(address, out info))
            {
                return false;
            }

            // The fixture owns this reservation, so uncommitted portions are
            // available to its child allocator even though the OS correctly
            // reports them as reserved to unrelated processes.
            if (address >= BaseAddress &&
                address < BaseAddress + _size &&
                info.State == HostRegionState.Reserved)
            {
                var reservedEnd = BaseAddress + _reservedPrefixSize;
                var logicalEnd = BaseAddress + _size;
                var state = address < reservedEnd
                    ? HostRegionState.Reserved
                    : HostRegionState.Free;
                var runEnd = state == HostRegionState.Reserved
                    ? reservedEnd
                    : logicalEnd;
                info = info with
                {
                    BaseAddress = address,
                    AllocationBase = BaseAddress,
                    RegionSize = runEnd - address,
                    State = state,
                    Protection = HostPageProtection.NoAccess,
                };
            }

            return true;
        }

        public void FlushInstructionCache(ulong address, ulong size) =>
            _inner.FlushInstructionCache(address, size);

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            Assert.True(_inner.Free(_reservationBase));
        }
    }

    private sealed class FailingRawProtectionHostMemory(IHostMemory inner) : IHostMemory
    {
        public bool FailNextRawProtection { get; set; }

        public int FailedRawProtectionCalls { get; private set; }

        public ulong Allocate(ulong desiredAddress, ulong size, HostPageProtection protection) =>
            inner.Allocate(desiredAddress, size, protection);

        public ulong Reserve(ulong desiredAddress, ulong size, HostPageProtection protection) =>
            inner.Reserve(desiredAddress, size, protection);

        public bool Commit(ulong address, ulong size, HostPageProtection protection) =>
            inner.Commit(address, size, protection);

        public bool Free(ulong address) => inner.Free(address);

        public bool Protect(
            ulong address,
            ulong size,
            HostPageProtection protection,
            out uint rawOldProtection) =>
            inner.Protect(address, size, protection, out rawOldProtection);

        public bool ProtectRaw(
            ulong address,
            ulong size,
            uint rawProtection,
            out uint rawOldProtection)
        {
            if (FailNextRawProtection)
            {
                FailNextRawProtection = false;
                FailedRawProtectionCalls++;
                rawOldProtection = 0;
                return false;
            }

            return inner.ProtectRaw(address, size, rawProtection, out rawOldProtection);
        }

        public bool Query(ulong address, out HostRegionInfo info) =>
            inner.Query(address, out info);

        public void FlushInstructionCache(ulong address, ulong size) =>
            inner.FlushInstructionCache(address, size);
    }

    [Theory]
    [InlineData(OneGibibyte - 0x1000, false, false)]
    [InlineData(OneGibibyte, false, true)]
    [InlineData(2 * OneGibibyte, false, true)]
    [InlineData(8 * OneGibibyte, false, true)]
    [InlineData(OneGibibyte, true, false)]
    [InlineData(8 * OneGibibyte, true, false)]
    public void LargeNonExecutableMappingsUseLazyReservation(
        ulong alignedSize,
        bool executable,
        bool expected)
    {
        Assert.Equal(
            expected,
            PhysicalVirtualMemory.ShouldReserveWithoutCommit(alignedSize, executable));
    }

    [Fact]
    public unsafe void GetPointerCommitsLazyReservedPageBeforeReturning()
    {
        var hostMemory = CreateHostMemory();
        using var memory = new PhysicalVirtualMemory(hostMemory);
        var address = memory.AllocateAt(0, OneGibibyte, executable: false);
        Assert.True(TryFindReservedPage(
            hostMemory,
            address,
            OneGibibyte,
            out var reservedAddress));
        Assert.Equal(
            HostRegionState.Reserved,
            QueryHostPage(hostMemory, reservedAddress).State);

        var pointer = memory.GetPointer(reservedAddress);

        Assert.NotEqual(0, (nint)pointer);
        Assert.Equal(
            HostRegionState.Committed,
            QueryHostPage(hostMemory, reservedAddress).State);
        *(byte*)pointer = 0xA5;
        Span<byte> actual = stackalloc byte[1];
        Assert.True(memory.TryRead(reservedAddress, actual));
        Assert.Equal(0xA5, actual[0]);
    }

    [Fact]
    public void AllocationSearchSkipsLargeHostReservation()
    {
        using var hostMemory = new ContiguousHostMemory(
            HostBlockerSize + HostAllocationGranularity,
            reservedPrefixSize: HostBlockerSize);
        using var memory = new PhysicalVirtualMemory(hostMemory);
        var desiredAddress = hostMemory.BaseAddress;

        Assert.True(memory.TryAllocateAtOrAbove(
            desiredAddress,
            size: HostAllocationGranularity,
            executable: false,
            alignment: 0x1000,
            out var actualAddress));
        Assert.Equal(desiredAddress + HostBlockerSize, actualAddress);
    }

    [Fact]
    public void AllocationSearchRejectsNonPowerOfTwoAlignmentWithoutMutation()
    {
        using var memory = CreatePhysicalMemory();

        Assert.False(memory.TryAllocateAtOrAbove(
            0x0000_0008_4000_0000,
            size: 0x1000,
            executable: false,
            alignment: 0x1800,
            out _));
        Assert.Empty(memory.SnapshotRegions());
    }

    [Fact]
    public void AllocationSearchReturnsFalseInsteadOfThrowingOnOverflow()
    {
        using var memory = CreatePhysicalMemory();
        var result = true;

        var addressException = Record.Exception(() =>
            result = memory.TryAllocateAtOrAbove(
                ulong.MaxValue - 0x100,
                size: 0x1000,
                executable: false,
                alignment: 0x1000,
                out _));

        Assert.Null(addressException);
        Assert.False(result);

        result = true;
        var sizeException = Record.Exception(() =>
            result = memory.TryAllocateAtOrAbove(
                0x1_0000,
                size: ulong.MaxValue,
                executable: false,
                alignment: 0x1000,
                out _));

        Assert.Null(sizeException);
        Assert.False(result);
        Assert.Empty(memory.SnapshotRegions());
    }

    [Fact]
    public void UnalignedRequestedAllocationPreservesGuestAddressAndTail()
    {
        using var hostMemory = new ContiguousHostMemory(HostAllocationGranularity);
        using var memory = new PhysicalVirtualMemory(hostMemory);
        var desiredAddress = hostMemory.BaseAddress + 0x1000;
        var actualAddress = memory.AllocateAt(
            desiredAddress,
            0x2000,
            executable: false,
            allowAlternative: false);

        Assert.Equal(desiredAddress, actualAddress);
        Assert.True(memory.IsAccessible(desiredAddress, 0x2000));
        Assert.True(memory.TryWrite(desiredAddress + 0x1FFF, [0xA5]));
        Span<byte> tail = stackalloc byte[1];
        Assert.True(memory.TryRead(desiredAddress + 0x1FFF, tail));
        Assert.Equal(0xA5, tail[0]);
    }

    [Fact]
    public void AllocationSearchReturnsUnalignedGuestCandidateInsteadOfRoundedHostBase()
    {
        using var hostMemory = new ContiguousHostMemory(HostAllocationGranularity);
        using var memory = new PhysicalVirtualMemory(hostMemory);
        var desiredAddress = hostMemory.BaseAddress + 0x1000;

        Assert.True(memory.TryAllocateAtOrAbove(
            desiredAddress,
            size: 0x2000,
            executable: false,
            alignment: 0x1000,
            out var actualAddress));
        Assert.Equal(desiredAddress, actualAddress);
        Assert.Single(memory.SnapshotRegions());
        Assert.True(memory.IsAccessible(actualAddress, 0x2000));
    }

    [Fact]
    public void DisposedMemoryRejectsFurtherOperations()
    {
        var memory = CreatePhysicalMemory();
        memory.Dispose();

        Assert.Throws<ObjectDisposedException>(() =>
            memory.TryAllocateAtExact(0, 0x1000, executable: false, out _));
        Assert.Throws<ObjectDisposedException>(() =>
            memory.AllocateAt(0, 0x1000, executable: false));
        Assert.Throws<ObjectDisposedException>(() =>
            memory.TryAllocateAtOrAbove(
                0x10000,
                0x1000,
                executable: false,
                alignment: 0x1000,
                out _));
        Assert.Throws<ObjectDisposedException>(() =>
            memory.TryAllocateGuestMemory(0x1000, 0x1000, out _));
        Assert.Throws<ObjectDisposedException>(() =>
            memory.Map(
                0x10000,
                0x1000,
                fileOffset: 0,
                fileData: Array.Empty<byte>(),
                ProgramHeaderFlags.Read));
        Assert.Throws<ObjectDisposedException>(() =>
            memory.RegisterStackRange(0x10000, 0x1000));
        Assert.Throws<ObjectDisposedException>(() =>
            memory.TryGetStackRange(0x10000, out _, out _));
        Assert.Throws<ObjectDisposedException>(() => memory.SnapshotRegions());
        Assert.Throws<ObjectDisposedException>(() =>
            memory.TryQueryMemoryRegion(0x10000, findNext: false, out _));
        Assert.Throws<ObjectDisposedException>(() =>
            memory.TryRead(0x10000, new byte[1]));
        Assert.Throws<ObjectDisposedException>(() =>
            memory.TryWrite(0x10000, new byte[1]));
        Assert.Throws<ObjectDisposedException>(() =>
            memory.TryCompare(0x10000, new byte[1]));
        Assert.Throws<ObjectDisposedException>(() =>
            memory.TryWriteUInt64(0x10000, 0));
        Assert.Throws<ObjectDisposedException>(() =>
            memory.IsAccessible(0x10000, 1));
        Assert.Throws<ObjectDisposedException>(memory.Clear);

        memory.Dispose();
    }
}
