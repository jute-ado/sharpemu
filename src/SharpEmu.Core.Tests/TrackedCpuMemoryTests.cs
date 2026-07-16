// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Core.Cpu;
using SharpEmu.Core.Loader;
using SharpEmu.Core.Memory;
using SharpEmu.HLE;
using Xunit;

namespace SharpEmu.Core.Tests;

public sealed class TrackedCpuMemoryTests
{
    [Fact]
    public void ConstructorRejectsNullAndExposesWrappedMemory()
    {
        Assert.Throws<ArgumentNullException>(() => new TrackedCpuMemory(null!));

        var inner = new PlainMemory();
        var memory = new TrackedCpuMemory(inner);

        Assert.Same(inner, memory.Inner);
        Assert.Null(memory.LastFailure);
    }

    [Fact]
    public void FailedReadsAndWritesCaptureExactAccessDetails()
    {
        var inner = new PlainMemory();
        var memory = new TrackedCpuMemory(inner);

        Assert.False(memory.TryRead(0x1234, new byte[7]));
        var readFailure = Assert.IsType<CpuMemoryAccessFailure>(memory.LastFailure);
        Assert.Equal(0x1234UL, readFailure.Address);
        Assert.Equal(7, readFailure.Size);
        Assert.False(readFailure.IsWrite);

        Assert.False(memory.TryWrite(0x5678, new byte[11]));
        var writeFailure = Assert.IsType<CpuMemoryAccessFailure>(memory.LastFailure);
        Assert.Equal(0x5678UL, writeFailure.Address);
        Assert.Equal(11, writeFailure.Size);
        Assert.True(writeFailure.IsWrite);
    }

    [Fact]
    public void CompareForwardsToInnerMemoryWithoutRecordingMismatchAsFault()
    {
        var inner = new VirtualMemory();
        inner.Map(
            0x1000,
            0x1000,
            fileOffset: 0,
            [1, 2, 3, 4],
            ProgramHeaderFlags.Read | ProgramHeaderFlags.Write);
        var memory = new TrackedCpuMemory(inner);

        Assert.True(memory.TryCompare(0x1000, [1, 2, 3, 4]));
        Assert.False(memory.TryCompare(0x1000, [1, 2, 3, 5]));
        Assert.Null(memory.LastFailure);
    }

    [Fact]
    public void OptionalAllocatorCapabilityForwardsArgumentsAndResults()
    {
        var inner = new CapabilityMemory();
        var memory = new TrackedCpuMemory(inner);

        Assert.True(memory.TryAllocateGuestMemory(0x321, 0x100, out var address));
        Assert.Equal(0x9000UL, address);
        Assert.Equal((0x321UL, 0x100UL), inner.LastAllocationRequest);
        Assert.True(memory.TryFreeGuestMemory(address));
        Assert.Equal(address, inner.LastFreedAddress);
    }

    [Fact]
    public void MissingAllocatorCapabilityReturnsFailureWithoutAddress()
    {
        var memory = new TrackedCpuMemory(new PlainMemory());

        Assert.False(memory.TryAllocateGuestMemory(1, 1, out var address));
        Assert.Equal(0UL, address);
        Assert.False(memory.TryFreeGuestMemory(0x1000));
    }

    [Fact]
    public void StackAndQueryCapabilitiesForwardGuestMetadata()
    {
        var inner = new CapabilityMemory();
        var memory = new TrackedCpuMemory(inner);

        memory.RegisterStackRange(0xA000, 0x800);
        Assert.Equal((0xA000UL, 0x800UL), inner.RegisteredStack);
        Assert.True(memory.TryGetStackRange(0xA123, out var start, out var end));
        Assert.Equal(0xA000UL, start);
        Assert.Equal(0xA800UL, end);

        Assert.True(memory.TryQueryMemoryRegion(0xB123, findNext: true, out var region));
        Assert.Equal((0xB123UL, true), inner.LastQuery);
        Assert.Equal(new GuestVirtualMemoryRegion(0xB000, 0x1000, 0x05), region);
    }

    [Fact]
    public void MissingStackAndQueryCapabilitiesHaveExplicitFallbacks()
    {
        var memory = new TrackedCpuMemory(new PlainMemory());

        Assert.Throws<NotSupportedException>(() => memory.RegisterStackRange(0x1000, 0x1000));
        Assert.False(memory.TryGetStackRange(0x1000, out var start, out var end));
        Assert.Equal(0UL, start);
        Assert.Equal(0UL, end);
        Assert.False(memory.TryQueryMemoryRegion(0x1000, findNext: false, out var region));
        Assert.Equal(default, region);
    }

    private class PlainMemory : ICpuMemory
    {
        public virtual bool TryRead(ulong virtualAddress, Span<byte> destination) => false;

        public virtual bool TryWrite(ulong virtualAddress, ReadOnlySpan<byte> source) => false;

        public virtual bool TryCompare(ulong virtualAddress, ReadOnlySpan<byte> expected) => false;
    }

    private sealed class CapabilityMemory : PlainMemory, IGuestMemoryAllocator, IGuestStackMemory, IGuestVirtualMemoryQuery
    {
        public (ulong Size, ulong Alignment) LastAllocationRequest { get; private set; }

        public ulong LastFreedAddress { get; private set; }

        public (ulong Start, ulong Size) RegisteredStack { get; private set; }

        public (ulong Address, bool FindNext) LastQuery { get; private set; }

        public bool TryAllocateGuestMemory(ulong size, ulong alignment, out ulong address)
        {
            LastAllocationRequest = (size, alignment);
            address = 0x9000;
            return true;
        }

        public bool TryFreeGuestMemory(ulong address)
        {
            LastFreedAddress = address;
            return true;
        }

        public void RegisterStackRange(ulong start, ulong size) => RegisteredStack = (start, size);

        public bool TryGetStackRange(ulong address, out ulong start, out ulong end)
        {
            start = 0xA000;
            end = 0xA800;
            return true;
        }

        public bool TryQueryMemoryRegion(
            ulong address,
            bool findNext,
            out GuestVirtualMemoryRegion region)
        {
            LastQuery = (address, findNext);
            region = new GuestVirtualMemoryRegion(0xB000, 0x1000, 0x05);
            return true;
        }
    }
}
