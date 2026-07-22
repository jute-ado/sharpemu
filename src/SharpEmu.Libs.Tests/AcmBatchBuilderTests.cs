// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using SharpEmu.HLE;
using SharpEmu.Libs.Audio;
using Xunit;

namespace SharpEmu.Libs.Tests;

public sealed class AcmBatchBuilderTests
{
    private const ulong BatchInfoAddress = 0x1000;
    private const ulong BufferAddress = 0x2000;
    private const int MemoryFault =
        (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT;

    [Theory]
    [InlineData(0UL, 4096UL, 0UL)]
    [InlineData(BufferAddress, 0UL, 0UL)]
    public void SharedInputLeavesInactiveDescriptorUnchanged(
        ulong bufferAddress,
        ulong bufferSize,
        ulong expectedOffset)
    {
        var context = CreateContext(bufferAddress, expectedOffset, bufferSize);

        AssertCall(0, context, AcmExports.AcmConvReverbSharedInput);
        Assert.True(context.TryReadUInt64(BatchInfoAddress + 8, out var offset));
        Assert.Equal(expectedOffset, offset);
    }

    [Theory]
    [InlineData(0UL, 4096UL, 1024UL)]
    [InlineData(256UL, 4096UL, 1280UL)]
    [InlineData(3584UL, 4096UL, 4096UL)]
    [InlineData(8192UL, 4096UL, 4096UL)]
    [InlineData(ulong.MaxValue - 100UL, ulong.MaxValue, ulong.MaxValue)]
    public void SharedInputAdvancesOffsetByBoundedCommandSize(
        ulong initialOffset,
        ulong bufferSize,
        ulong expectedOffset)
    {
        var context = CreateContext(BufferAddress, initialOffset, bufferSize);

        AssertCall(0, context, AcmExports.AcmConvReverbSharedInput);
        Assert.True(context.TryReadUInt64(BatchInfoAddress + 8, out var offset));
        Assert.Equal(expectedOffset, offset);
    }

    [Fact]
    public void SharedInputAcceptsNullDescriptor()
    {
        var context = new CpuContext(new FakeGuestMemory(), Generation.Gen5);
        context[CpuRegister.Rdi] = 0;

        AssertCall(0, context, AcmExports.AcmConvReverbSharedInput);
    }

    [Fact]
    public void SharedInputRejectsUnreadableDescriptor()
    {
        var context = new CpuContext(new FakeGuestMemory(), Generation.Gen5);
        context[CpuRegister.Rdi] = BatchInfoAddress;

        AssertCall(MemoryFault, context, AcmExports.AcmConvReverbSharedInput);
    }

    [Fact]
    public void SharedInputExportHasExactMetadata()
    {
        var exports = SharpEmu.Generated.SysAbiExportRegistry
            .CreateExports(Generation.Gen5);
        var export = Assert.Single(exports, candidate => candidate.Nid == "u70oWo92SYQ");

        Assert.Equal("sceAcm_ConvReverb_SharedInput", export.Name);
        Assert.Equal("libSceAcm", export.LibraryName);
    }

    private static CpuContext CreateContext(
        ulong bufferAddress,
        ulong offset,
        ulong bufferSize)
    {
        var batchInfo = new byte[0x18];
        BinaryPrimitives.WriteUInt64LittleEndian(batchInfo.AsSpan(0x00), bufferAddress);
        BinaryPrimitives.WriteUInt64LittleEndian(batchInfo.AsSpan(0x08), offset);
        BinaryPrimitives.WriteUInt64LittleEndian(batchInfo.AsSpan(0x10), bufferSize);

        var memory = new FakeGuestMemory();
        memory.AddRegion(BatchInfoAddress, batchInfo);
        var context = new CpuContext(memory, Generation.Gen5);
        context[CpuRegister.Rdi] = BatchInfoAddress;
        return context;
    }

    private static void AssertCall(
        int expected,
        CpuContext context,
        Func<CpuContext, int> export)
    {
        Assert.Equal(expected, export(context));
        Assert.Equal(unchecked((ulong)expected), context[CpuRegister.Rax]);
    }
}
