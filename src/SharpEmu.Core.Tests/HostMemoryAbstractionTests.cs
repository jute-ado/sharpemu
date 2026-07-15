// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Core.Memory;
using SharpEmu.HLE;
using SharpEmu.HLE.Host;
using Xunit;

namespace SharpEmu.Core.Tests;

public sealed class HostMemoryAbstractionTests
{
    [Fact]
    public void ExactAllocationRejectsAndReleasesAlternativeHostAddress()
    {
        const ulong desiredAddress = 0x0000_0008_0000_0000;
        var hostMemory = new RecordingHostMemory
        {
            AllocateResult = desiredAddress + 0x1000,
        };
        using var memory = new PhysicalVirtualMemory(hostMemory);

        var allocated = memory.TryAllocateAtExact(
            desiredAddress,
            0x1000,
            executable: false,
            out var actualAddress);

        Assert.False(allocated);
        Assert.Equal(0uL, actualAddress);
        Assert.Equal([desiredAddress + 0x1000], hostMemory.FreedAddresses);
        Assert.Empty(memory.SnapshotRegions());
    }

    [Fact]
    public void UnalignedAllocationTracksRoundedHostReservationAndGuestAddress()
    {
        const ulong hostAddress = 0x0000_0008_1000_0000;
        const ulong desiredAddress = hostAddress + 0x2000;
        var hostMemory = new RecordingHostMemory
        {
            AllocateResult = hostAddress,
        };
        using var memory = new PhysicalVirtualMemory(hostMemory);

        var guestAddress = memory.AllocateAt(
            desiredAddress,
            0x3000,
            executable: false,
            allowAlternative: false);

        Assert.Equal(desiredAddress, guestAddress);
        var region = Assert.Single(memory.SnapshotRegions());
        Assert.Equal(hostAddress, region.VirtualAddress);
        Assert.Equal(0x5000uL, region.MemorySize);
    }

    [Theory]
    [InlineData(GuestPageProtection.None, HostPageProtection.NoAccess)]
    [InlineData(GuestPageProtection.Read, HostPageProtection.ReadOnly)]
    [InlineData(GuestPageProtection.Write, HostPageProtection.ReadWrite)]
    [InlineData(GuestPageProtection.Execute, HostPageProtection.Execute)]
    [InlineData(GuestPageProtection.Read | GuestPageProtection.Execute, HostPageProtection.ReadExecute)]
    [InlineData(GuestPageProtection.Write | GuestPageProtection.Execute, HostPageProtection.ReadWriteExecute)]
    public void GuestProtectionMapsToHostProtection(
        GuestPageProtection guestProtection,
        HostPageProtection expectedHostProtection)
    {
        const ulong address = 0x0000_0008_1800_0000;
        var hostMemory = new RecordingHostMemory
        {
            AllocateResult = address,
        };
        using var memory = new PhysicalVirtualMemory(hostMemory);
        memory.AllocateAt(address, 0x2000, executable: false, allowAlternative: false);

        Assert.True(memory.TryProtect(address, 0x2000, guestProtection));

        var request = Assert.Single(hostMemory.ProtectionRequests);
        Assert.Equal((address, 0x2000uL, expectedHostProtection), request);
    }

    [Fact]
    public void GuestProtectionUpdatesGuestVisibleRegionBoundaries()
    {
        const ulong address = 0x0000_0008_2000_0000;
        var hostMemory = new RecordingHostMemory
        {
            AllocateResult = address,
        };
        using var memory = new PhysicalVirtualMemory(hostMemory);
        memory.AllocateAt(address, 0x3000, executable: false, allowAlternative: false);

        Assert.True(memory.TryProtect(
            address + 0x1000,
            0x1000,
            GuestPageProtection.Read | GuestPageProtection.Execute));

        Assert.True(memory.TryQueryMemoryRegion(address + 0x1800, findNext: false, out var region));
        Assert.Equal(new GuestVirtualMemoryRegion(address + 0x1000, 0x1000, 0x05), region);
        Assert.Equal([(address + 0x1000, 0x1000uL)], hostMemory.FlushedRanges);
    }

    [Fact]
    public void GuestProtectionRejectsUnmappedRangeWithoutCallingHost()
    {
        var hostMemory = new RecordingHostMemory();
        using var memory = new PhysicalVirtualMemory(hostMemory);

        Assert.False(memory.TryProtect(0x1000, 0x1000, GuestPageProtection.Read));

        Assert.Empty(hostMemory.ProtectionRequests);
    }

    [Fact]
    public void FailedGuestProtectionDoesNotChangeGuestVisibleProtection()
    {
        const ulong address = 0x0000_0008_3000_0000;
        var hostMemory = new RecordingHostMemory
        {
            AllocateResult = address,
            ProtectResult = false,
        };
        using var memory = new PhysicalVirtualMemory(hostMemory);
        memory.AllocateAt(address, 0x1000, executable: false, allowAlternative: false);

        Assert.False(memory.TryProtect(address, 0x1000, GuestPageProtection.Read));

        Assert.True(memory.TryQueryMemoryRegion(address, findNext: false, out var region));
        Assert.Equal(new GuestVirtualMemoryRegion(address, 0x1000, 0x03), region);
        Assert.Single(hostMemory.ProtectionRequests);
        Assert.Empty(hostMemory.FlushedRanges);
    }

    [Fact]
    public void DisposedMemoryRejectsGuestProtectionChanges()
    {
        var hostMemory = new RecordingHostMemory();
        var memory = new PhysicalVirtualMemory(hostMemory);
        memory.Dispose();

        Assert.Throws<ObjectDisposedException>(() =>
            memory.TryProtect(0x1000, 0x1000, GuestPageProtection.Read));
        Assert.Empty(hostMemory.ProtectionRequests);
    }

    private sealed class RecordingHostMemory : IHostMemory
    {
        public ulong AllocateResult { get; init; }

        public bool ProtectResult { get; init; } = true;

        public List<ulong> FreedAddresses { get; } = [];

        public List<(ulong Address, ulong Size, HostPageProtection Protection)> ProtectionRequests { get; } = [];

        public List<(ulong Address, ulong Size)> FlushedRanges { get; } = [];

        public ulong Allocate(ulong desiredAddress, ulong size, HostPageProtection protection) =>
            AllocateResult;

        public ulong Reserve(ulong desiredAddress, ulong size, HostPageProtection protection) => 0;

        public bool Commit(ulong address, ulong size, HostPageProtection protection) => true;

        public bool Free(ulong address)
        {
            FreedAddresses.Add(address);
            return true;
        }

        public bool Protect(
            ulong address,
            ulong size,
            HostPageProtection protection,
            out uint rawOldProtection)
        {
            ProtectionRequests.Add((address, size, protection));
            rawOldProtection = 0;
            return ProtectResult;
        }

        public bool ProtectRaw(
            ulong address,
            ulong size,
            uint rawProtection,
            out uint rawOldProtection)
        {
            rawOldProtection = 0;
            return true;
        }

        public bool Query(ulong address, out HostRegionInfo info)
        {
            info = default;
            return false;
        }

        public void FlushInstructionCache(ulong address, ulong size)
        {
            FlushedRanges.Add((address, size));
        }
    }
}
