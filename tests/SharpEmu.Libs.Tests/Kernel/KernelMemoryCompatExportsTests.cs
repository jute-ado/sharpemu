// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using SharpEmu.Libs.Kernel;
using System.Globalization;
using System.Text;
using Xunit;

namespace SharpEmu.Libs.Tests.Kernel;

[CollectionDefinition(KernelMemoryCompatStateCollection.Name, DisableParallelization = true)]
public sealed class KernelMemoryCompatStateCollection
{
    public const string Name = "KernelMemoryCompatState";
}

[Collection(KernelMemoryCompatStateCollection.Name)]
public sealed class KernelMemoryCompatExportsTests
{
    private const ulong GuestMemoryBase = 0x1_0000_0000;
    private const ulong AllocationOutAddress = GuestMemoryBase + 0x100;
    private const ulong SpanStartOutAddress = GuestMemoryBase + 0x108;
    private const ulong SpanSizeOutAddress = GuestMemoryBase + 0x110;

    [Fact]
    public void PosixStat_MissingFileReturnsMinusOne()
    {
        const ulong memoryBase = 0x1_0000_0000;
        const ulong pathAddress = memoryBase + 0x100;
        const ulong statAddress = memoryBase + 0x400;
        var memory = new FakeCpuMemory(memoryBase, 0x1000);
        var context = new CpuContext(memory, Generation.Gen5);
        memory.WriteCString(pathAddress, "/__sharpemu_test_missing__/shader.cache");
        context[CpuRegister.Rdi] = pathAddress;
        context[CpuRegister.Rsi] = statAddress;

        var result = KernelMemoryCompatExports.PosixStat(context);

        Assert.Equal(-1, result);
        Assert.Equal(ulong.MaxValue, context[CpuRegister.Rax]);
    }

    [Fact]
    public void PosixOpen_MissingFileReturnsMinusOne()
    {
        const ulong memoryBase = 0x1_0000_0000;
        const ulong pathAddress = memoryBase + 0x100;
        var memory = new FakeCpuMemory(memoryBase, 0x1000);
        var context = new CpuContext(memory, Generation.Gen5);
        memory.WriteCString(pathAddress, "/__sharpemu_test_missing__/il2cpp.usym");
        context[CpuRegister.Rdi] = pathAddress;
        context[CpuRegister.Rsi] = 0; // O_RDONLY

        var result = KernelMemoryCompatExports.PosixOpen(context);

        // A libc open() failure must be -1, not the raw 0x8002xxxx sentinel the
        // guest would otherwise store as a valid fd and later dereference.
        Assert.Equal(-1, result);
        Assert.Equal(ulong.MaxValue, context[CpuRegister.Rax]);
    }

    [Fact]
    public void PosixFstat_BadDescriptorReturnsMinusOne()
    {
        const ulong memoryBase = 0x1_0000_0000;
        const ulong statAddress = memoryBase + 0x400;
        var memory = new FakeCpuMemory(memoryBase, 0x1000);
        var context = new CpuContext(memory, Generation.Gen5);
        context[CpuRegister.Rdi] = 0x80020002; // the not-found sentinel misused as an fd
        context[CpuRegister.Rsi] = statAddress;

        var result = KernelMemoryCompatExports.PosixFstat(context);

        Assert.Equal(-1, result);
        Assert.Equal(ulong.MaxValue, context[CpuRegister.Rax]);
    }

    [Fact]
    public void PosixClose_BadDescriptorReturnsMinusOne()
    {
        const ulong memoryBase = 0x1_0000_0000;
        var memory = new FakeCpuMemory(memoryBase, 0x1000);
        var context = new CpuContext(memory, Generation.Gen5);
        context[CpuRegister.Rdi] = 0x80020002; // never-opened / sentinel fd

        var result = KernelMemoryCompatExports.PosixClose(context);

        Assert.Equal(-1, result);
        Assert.Equal(ulong.MaxValue, context[CpuRegister.Rax]);
    }

    [Fact]
    public void PosixRead_BadDescriptorReturnsMinusOne()
    {
        const ulong memoryBase = 0x1_0000_0000;
        const ulong bufferAddress = memoryBase + 0x200;
        var memory = new FakeCpuMemory(memoryBase, 0x1000);
        var context = new CpuContext(memory, Generation.Gen5);
        context[CpuRegister.Rdi] = 0x80020002; // never-opened / sentinel fd
        context[CpuRegister.Rsi] = bufferAddress;
        context[CpuRegister.Rdx] = 0x40;

        var result = KernelMemoryCompatExports.PosixRead(context);

        Assert.Equal(-1, result);
        Assert.Equal(ulong.MaxValue, context[CpuRegister.Rax]);
    }

    [Fact]
    public void PosixWrite_BadDescriptorReturnsMinusOne()
    {
        const ulong memoryBase = 0x1_0000_0000;
        const ulong bufferAddress = memoryBase + 0x200;
        var memory = new FakeCpuMemory(memoryBase, 0x1000);
        var context = new CpuContext(memory, Generation.Gen5);
        memory.WriteCString(bufferAddress, "payload");
        context[CpuRegister.Rdi] = 0x80020002; // never-opened / sentinel fd
        context[CpuRegister.Rsi] = bufferAddress;
        context[CpuRegister.Rdx] = 0x7;

        var result = KernelMemoryCompatExports.PosixWrite(context);

        Assert.Equal(-1, result);
        Assert.Equal(ulong.MaxValue, context[CpuRegister.Rax]);
    }

    [Fact]
    public void Sprintf_ReadsVariadicDoubleFromXmmRegister()
    {
        const ulong memoryBase = 0x1_0000_0000;
        const ulong destinationAddress = memoryBase + 0x100;
        const ulong formatAddress = memoryBase + 0x200;
        var memory = new FakeCpuMemory(memoryBase, 0x1000);
        var context = new CpuContext(memory, Generation.Gen5);
        memory.WriteCString(formatAddress, "%.4f");
        context[CpuRegister.Rdi] = destinationAddress;
        context[CpuRegister.Rsi] = formatAddress;
        context.SetXmmRegister(
            0,
            unchecked((ulong)BitConverter.DoubleToInt64Bits(0.5576)),
            0);

        var previousCulture = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("es-ES");

            var result = KernelMemoryCompatExports.Sprintf(context);

            Assert.Equal(0, result);
            Assert.Equal(6UL, context[CpuRegister.Rax]);
            Span<byte> output = stackalloc byte[7];
            Assert.True(memory.TryRead(destinationAddress, output));
            Assert.Equal("0.5576\0", Encoding.UTF8.GetString(output));
        }
        finally
        {
            CultureInfo.CurrentCulture = previousCulture;
        }
    }

    [Fact]
    public void AvailableDirectMemorySize_FragmentedRangeReturnsLargestAlignedSpan()
    {
        const ulong firstAllocationStart = 0x0020_0000;
        const ulong firstAllocationLength = 0x0020_0000;
        const ulong secondAllocationStart = 0x00C0_0000;
        const ulong secondAllocationLength = 0x0040_0000;
        var context = new CpuContext(new FakeCpuMemory(GuestMemoryBase, 0x1000), Generation.Gen5);

        try
        {
            AllocateDirectMemory(context, firstAllocationStart, firstAllocationLength);
            AllocateDirectMemory(context, secondAllocationStart, secondAllocationLength);

            QueryAvailableDirectMemory(context, 0, 0x0100_0000, 0x4000);

            Assert.True(context.TryReadUInt64(SpanStartOutAddress, out var spanStart));
            Assert.True(context.TryReadUInt64(SpanSizeOutAddress, out var spanSize));
            Assert.Equal(0x0040_0000UL, spanStart);
            Assert.Equal(0x0080_0000UL, spanSize);
        }
        finally
        {
            ReleaseDirectMemory(context, firstAllocationStart, firstAllocationLength);
            ReleaseDirectMemory(context, secondAllocationStart, secondAllocationLength);
        }
    }

    [Fact]
    public void AvailableDirectMemorySize_AppliesAlignmentBeforeComparingSpans()
    {
        const ulong allocationStart = 0x0070_0000;
        const ulong allocationLength = 0x0010_0000;
        var context = new CpuContext(new FakeCpuMemory(GuestMemoryBase, 0x1000), Generation.Gen5);

        try
        {
            AllocateDirectMemory(context, allocationStart, allocationLength);

            QueryAvailableDirectMemory(context, 0x0010_0000, 0x00C0_0000, 0x0040_0000);

            Assert.True(context.TryReadUInt64(SpanStartOutAddress, out var spanStart));
            Assert.True(context.TryReadUInt64(SpanSizeOutAddress, out var spanSize));
            Assert.Equal(0x0080_0000UL, spanStart);
            Assert.Equal(0x0040_0000UL, spanSize);
        }
        finally
        {
            ReleaseDirectMemory(context, allocationStart, allocationLength);
        }
    }

    [Fact]
    public void MapDirectMemory2_UsesShiftedAbiAndTracksVirtualMemoryType()
    {
        const ulong physicalStart = 0x0200_0000;
        const ulong physicalLength = 0x0004_0000;
        const ulong directMemoryStart = physicalStart + 0x4000;
        const ulong mappedLength = 0xC000;
        const ulong mappingAlignment = 0x10000;
        const ulong expectedMappedAddress = 0x0201_0000;
        const ulong inOutAddress = GuestMemoryBase + 0x200;
        const ulong stackPointer = GuestMemoryBase + 0x400;
        const ulong virtualQueryInfo = GuestMemoryBase + 0x600;
        const ulong directQueryInfo = GuestMemoryBase + 0x700;
        const int allocationMemoryType = 0x11;
        const int mappingMemoryType = 0x2A;
        const int middleMemoryType = 0x35;
        const int initialProtection = 0x13;
        const int middleProtection = 0x03;

        var memory = new FakeGuestMemory();
        memory.AddRegion(GuestMemoryBase, new byte[0x1000]);
        var context = new CpuContext(memory, Generation.Gen5);

        try
        {
            AllocateDirectMemory(
                context,
                physicalStart,
                physicalLength,
                allocationMemoryType);
            Assert.True(context.TryWriteUInt64(inOutAddress, 0));
            Assert.True(context.TryWriteUInt64(stackPointer, 0x1234_5678));
            Assert.True(context.TryWriteUInt64(stackPointer + sizeof(ulong), mappingAlignment));

            context[CpuRegister.Rdi] = inOutAddress;
            context[CpuRegister.Rsi] = mappedLength;
            context[CpuRegister.Rdx] = unchecked((ulong)mappingMemoryType);
            context[CpuRegister.Rcx] = unchecked((ulong)initialProtection);
            context[CpuRegister.R8] = 0;
            context[CpuRegister.R9] = directMemoryStart;
            context[CpuRegister.Rsp] = stackPointer;

            Assert.Equal(0, KernelMemoryCompatExports.KernelMapDirectMemory2(context));
            Assert.True(context.TryReadUInt64(inOutAddress, out var mappedAddress));
            Assert.Equal(expectedMappedAddress, mappedAddress);

            QueryVirtualMemory(
                context,
                expectedMappedAddress + 0x100,
                virtualQueryInfo,
                out var queriedProtection,
                out var queriedMemoryType);
            Assert.Equal(initialProtection, queriedProtection);
            Assert.Equal(mappingMemoryType, queriedMemoryType);

            context[CpuRegister.Rdi] = physicalStart;
            context[CpuRegister.Rsi] = 0;
            context[CpuRegister.Rdx] = directQueryInfo;
            context[CpuRegister.Rcx] = 24;
            Assert.Equal(0, KernelMemoryCompatExports.KernelDirectMemoryQuery(context));
            Assert.True(context.TryReadInt32(directQueryInfo + 16, out var queriedAllocationType));
            Assert.Equal(allocationMemoryType, queriedAllocationType);

            context[CpuRegister.Rdi] = expectedMappedAddress + 0x4000;
            context[CpuRegister.Rsi] = 0x4000;
            context[CpuRegister.Rdx] = unchecked((ulong)middleMemoryType);
            context[CpuRegister.Rcx] = unchecked((ulong)middleProtection);
            Assert.Equal(0, KernelMemoryCompatExports.KernelMtypeprotect(context));

            Assert.Equal(
                mappingMemoryType,
                QueryVirtualMemoryType(context, expectedMappedAddress, virtualQueryInfo));
            QueryVirtualMemory(
                context,
                expectedMappedAddress + 0x4000,
                virtualQueryInfo,
                out queriedProtection,
                out queriedMemoryType);
            Assert.Equal(middleProtection, queriedProtection);
            Assert.Equal(middleMemoryType, queriedMemoryType);
            Assert.Equal(
                mappingMemoryType,
                QueryVirtualMemoryType(context, expectedMappedAddress + 0x8000, virtualQueryInfo));

            context[CpuRegister.Rdi] = physicalStart;
            context[CpuRegister.Rsi] = 0;
            context[CpuRegister.Rdx] = directQueryInfo;
            context[CpuRegister.Rcx] = 24;
            Assert.Equal(0, KernelMemoryCompatExports.KernelDirectMemoryQuery(context));
            Assert.True(context.TryReadInt32(directQueryInfo + 16, out queriedAllocationType));
            Assert.Equal(allocationMemoryType, queriedAllocationType);
        }
        finally
        {
            TryUnmap(context, expectedMappedAddress, mappedLength);
            TryUnmap(context, expectedMappedAddress, 0x4000);
            TryUnmap(context, expectedMappedAddress + 0x4000, 0x4000);
            TryUnmap(context, expectedMappedAddress + 0x8000, 0x4000);
            _ = memory.TryFreeGuestMemory(expectedMappedAddress);
            ReleaseDirectMemory(context, physicalStart, physicalLength);
        }
    }

    [Fact]
    public void MapDirectMemory2_UnreadableStackAlignmentReturnsMemoryFault()
    {
        const ulong inOutAddress = GuestMemoryBase + 0x100;
        var memory = new FakeGuestMemory();
        memory.AddRegion(GuestMemoryBase, new byte[0x1000]);
        var context = new CpuContext(memory, Generation.Gen5);
        Assert.True(context.TryWriteUInt64(inOutAddress, 0));
        context[CpuRegister.Rdi] = inOutAddress;
        context[CpuRegister.Rsi] = 0x4000;
        context[CpuRegister.Rdx] = 1;
        context[CpuRegister.Rcx] = 3;
        context[CpuRegister.R8] = 0;
        context[CpuRegister.R9] = 0x0040_0000;
        context[CpuRegister.Rsp] = GuestMemoryBase + 0x2000;

        var result = KernelMemoryCompatExports.KernelMapDirectMemory2(context);

        Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT, result);
        Assert.True(context.TryReadUInt64(inOutAddress, out var mappedAddress));
        Assert.Equal(0UL, mappedAddress);
    }

    [Fact]
    public void MapDirectMemory2_IsRegisteredForBothConsoleGenerations()
    {
        const string nid = "BQQniolj9tQ";
        var manager = new ModuleManager();
        manager.RegisterExports(
            SharpEmu.Generated.SysAbiExportRegistry.CreateExports(
                Generation.Gen4 | Generation.Gen5));

        Assert.True(manager.TryGetExport(nid, out var export));
        Assert.Equal("sceKernelMapDirectMemory2", export.Name);
        Assert.Equal("libKernel", export.LibraryName);
        Assert.Equal(Generation.Gen4 | Generation.Gen5, export.Target);
    }

    private static void AllocateDirectMemory(
        CpuContext context,
        ulong start,
        ulong length,
        int memoryType = 0)
    {
        context[CpuRegister.Rdi] = start;
        context[CpuRegister.Rsi] = start + length;
        context[CpuRegister.Rdx] = length;
        context[CpuRegister.Rcx] = 0x4000;
        context[CpuRegister.R8] = unchecked((ulong)memoryType);
        context[CpuRegister.R9] = AllocationOutAddress;

        Assert.Equal(0, KernelMemoryCompatExports.KernelAllocateDirectMemory(context));
        Assert.True(context.TryReadUInt64(AllocationOutAddress, out var allocatedAddress));
        Assert.Equal(start, allocatedAddress);
    }

    private static int QueryVirtualMemoryType(
        CpuContext context,
        ulong address,
        ulong infoAddress)
    {
        QueryVirtualMemory(
            context,
            address,
            infoAddress,
            out _,
            out var memoryType);
        return memoryType;
    }

    private static void QueryVirtualMemory(
        CpuContext context,
        ulong address,
        ulong infoAddress,
        out int protection,
        out int memoryType)
    {
        context[CpuRegister.Rdi] = address;
        context[CpuRegister.Rsi] = 0;
        context[CpuRegister.Rdx] = infoAddress;
        context[CpuRegister.Rcx] = 72;

        Assert.Equal(0, KernelMemoryCompatExports.KernelVirtualQuery(context));
        Assert.True(context.TryReadInt32(infoAddress + 24, out protection));
        Assert.True(context.TryReadInt32(infoAddress + 28, out memoryType));
    }

    private static void TryUnmap(CpuContext context, ulong address, ulong length)
    {
        context[CpuRegister.Rdi] = address;
        context[CpuRegister.Rsi] = length;
        _ = KernelMemoryCompatExports.KernelMunmap(context);
    }

    private static void QueryAvailableDirectMemory(
        CpuContext context,
        ulong searchStart,
        ulong searchEnd,
        ulong alignment)
    {
        context[CpuRegister.Rdi] = searchStart;
        context[CpuRegister.Rsi] = searchEnd;
        context[CpuRegister.Rdx] = alignment;
        context[CpuRegister.Rcx] = SpanStartOutAddress;
        context[CpuRegister.R8] = SpanSizeOutAddress;

        Assert.Equal(0, KernelMemoryCompatExports.KernelAvailableDirectMemorySize(context));
    }

    private static void ReleaseDirectMemory(CpuContext context, ulong start, ulong length)
    {
        context[CpuRegister.Rdi] = start;
        context[CpuRegister.Rsi] = length;

        Assert.Equal(0, KernelMemoryCompatExports.KernelReleaseDirectMemory(context));
    }

    [Fact]
    public void MapNamedFlexibleMemory_NullInOutPointerReturnsInvalidArgument()
    {
        var memory = new FakeCpuMemory(0x1_0000_0000, 0x1000);
        var context = new CpuContext(memory, Generation.Gen5);
        context[CpuRegister.Rdi] = 0;
        context[CpuRegister.Rsi] = 0x1000;
        context[CpuRegister.Rdx] = 0x03; // CPU read|write
        context[CpuRegister.Rcx] = 0;

        var result = KernelMemoryCompatExports.KernelMapNamedFlexibleMemory(context);

        Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT, result);
    }

    [Fact]
    public void MapNamedFlexibleMemory_ZeroLengthReturnsInvalidArgument()
    {
        const ulong memoryBase = 0x1_0000_0000;
        const ulong inOutAddress = memoryBase + 0x100;
        var memory = new FakeCpuMemory(memoryBase, 0x1000);
        var context = new CpuContext(memory, Generation.Gen5);
        memory.TryWrite(inOutAddress, BitConverter.GetBytes(0UL));
        context[CpuRegister.Rdi] = inOutAddress;
        context[CpuRegister.Rsi] = 0;
        context[CpuRegister.Rdx] = 0x03;
        context[CpuRegister.Rcx] = 0;

        var result = KernelMemoryCompatExports.KernelMapNamedFlexibleMemory(context);

        Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT, result);
    }

    [Fact]
    public void MapNamedFlexibleMemory_UnreadableInOutPointerReturnsMemoryFault()
    {
        // The in-out pointer points outside the FakeCpuMemory backing store, so
        // the first TryReadUInt64 must fail before any reservation is attempted.
        const ulong memoryBase = 0x1_0000_0000;
        const ulong unreachableInOut = memoryBase + 0x10_0000;
        var memory = new FakeCpuMemory(memoryBase, 0x1000);
        var context = new CpuContext(memory, Generation.Gen5);
        context[CpuRegister.Rdi] = unreachableInOut;
        context[CpuRegister.Rsi] = 0x1000;
        context[CpuRegister.Rdx] = 0x03;
        context[CpuRegister.Rcx] = 0;

        var result = KernelMemoryCompatExports.KernelMapNamedFlexibleMemory(context);

        Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT, result);
    }

    [Fact]
    public void Mprotect_ZeroAddressReturnsInvalidArgument()
    {
        var memory = new FakeCpuMemory(0x1_0000_0000, 0x1000);
        var context = new CpuContext(memory, Generation.Gen5);
        context[CpuRegister.Rdi] = 0;
        context[CpuRegister.Rsi] = 0x4000;
        context[CpuRegister.Rdx] = 0x03;

        var result = KernelMemoryCompatExports.KernelMprotect(context);

        Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT, result);
    }

    [Fact]
    public void Mprotect_ZeroLengthReturnsInvalidArgument()
    {
        const ulong memoryBase = 0x1_0000_0000;
        var memory = new FakeCpuMemory(memoryBase, 0x1000);
        var context = new CpuContext(memory, Generation.Gen5);
        context[CpuRegister.Rdi] = memoryBase;
        context[CpuRegister.Rsi] = 0;
        context[CpuRegister.Rdx] = 0x03;

        var result = KernelMemoryCompatExports.KernelMprotect(context);

        Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT, result);
    }

    [Fact]
    public void Mprotect_UnmappedRangeReturnsNotFound()
    {
        // A plausible guest address that FakeCpuMemory does not back and that
        // has no host reservation. TryProtectHostRange calls VirtualProtect,
        // which fails on an unmapped range, yielding NOT_FOUND rather than
        // mutating protection or throwing.
        const ulong unmappedAddress = 0x2_0000_0000;
        var memory = new FakeCpuMemory(0x1_0000_0000, 0x1000);
        var context = new CpuContext(memory, Generation.Gen5);
        context[CpuRegister.Rdi] = unmappedAddress;
        context[CpuRegister.Rsi] = 0x4000;
        context[CpuRegister.Rdx] = 0x03;

        var result = KernelMemoryCompatExports.KernelMprotect(context);

        Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_ERROR_NOT_FOUND, result);
    }

    [Fact]
    public void Munmap_ZeroAddressReturnsInvalidArgument()
    {
        var memory = new FakeCpuMemory(0x1_0000_0000, 0x1000);
        var context = new CpuContext(memory, Generation.Gen5);
        context[CpuRegister.Rdi] = 0;
        context[CpuRegister.Rsi] = 0x4000;

        var result = KernelMemoryCompatExports.KernelMunmap(context);

        Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT, result);
    }

    [Fact]
    public void Munmap_OverflowRangeReturnsInvalidArgument()
    {
        // address + length would overflow; KernelMunmap guards this explicitly
        // before touching any region accounting.
        var memory = new FakeCpuMemory(0x1_0000_0000, 0x1000);
        var context = new CpuContext(memory, Generation.Gen5);
        context[CpuRegister.Rdi] = ulong.MaxValue - 0x10;
        context[CpuRegister.Rsi] = 0x20;

        var result = KernelMemoryCompatExports.KernelMunmap(context);

        Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT, result);
    }

    [Fact]
    public void Munmap_UnmappedRangeReturnsNotFound()
    {
        // No flexible region is registered at this address and FakeCpuMemory
        // does not back it, so both physicallyBacked and removedRegions are
        // empty and the export reports NOT_FOUND.
        const ulong unmappedAddress = 0x2_0000_0000;
        var memory = new FakeCpuMemory(0x1_0000_0000, 0x1000);
        var context = new CpuContext(memory, Generation.Gen5);
        context[CpuRegister.Rdi] = unmappedAddress;
        context[CpuRegister.Rsi] = 0x4000;

        var result = KernelMemoryCompatExports.KernelMunmap(context);

        Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_ERROR_NOT_FOUND, result);
    }
}
