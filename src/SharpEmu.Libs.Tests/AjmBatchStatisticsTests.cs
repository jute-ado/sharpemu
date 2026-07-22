// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using SharpEmu.HLE;
using SharpEmu.Libs.Ajm;
using Xunit;

namespace SharpEmu.Libs.Tests;

public sealed class AjmBatchStatisticsTests
{
    private const string StatisticsNid = "3cAg7xN995U";
    private const int InvalidParameter = unchecked((int)0x80930005);
    private const int JobCreationError = unchecked((int)0x80930012);
    private const ulong BatchBufferAddress = 0x1000;
    private const ulong BatchInfoAllocationAddress = 0x3000;
    private const ulong BatchInfoAddress = BatchInfoAllocationAddress + 8;
    private const ulong ResultAllocationAddress = 0x4000;
    private const ulong ResultAddress = ResultAllocationAddress + 8;
    private const int BatchInfoSize = 0x28;
    private const int StatisticsJobSize = 0x58;
    private const int StatisticsResultSize = 0x30;
    private const byte Canary = 0xA5;

    [Fact]
    public void BatchInitializeWritesExactDescriptorWithoutTouchingCanaries()
    {
        var memory = new FakeGuestMemory();
        var infoAllocation = FilledBuffer(BatchInfoSize + 16);
        memory.AddRegion(BatchInfoAllocationAddress, infoAllocation);
        var context = CreateContext(memory);
        ConfigureBatchInitialize(context, size: 0x200, BatchInfoAddress);

        AssertCall(0, context, AjmExports.AjmBatchInitialize);

        var info = infoAllocation.AsSpan(8, BatchInfoSize);
        Assert.Equal(BatchBufferAddress, ReadUInt64(info, 0x00));
        Assert.Equal(0UL, ReadUInt64(info, 0x08));
        Assert.Equal(0x200UL, ReadUInt64(info, 0x10));
        Assert.Equal(0UL, ReadUInt64(info, 0x18));
        Assert.Equal(0UL, ReadUInt64(info, 0x20));
        Assert.All(infoAllocation.AsSpan(0, 8).ToArray(), value => Assert.Equal(Canary, value));
        Assert.All(infoAllocation.AsSpan(0x30).ToArray(), value => Assert.Equal(Canary, value));
    }

    [Fact]
    public void BatchInitializeRejectsInvalidAndUnwritableArguments()
    {
        var memory = new FakeGuestMemory();
        var infoAllocation = FilledBuffer(BatchInfoSize + 16);
        memory.AddRegion(BatchInfoAllocationAddress, infoAllocation);
        var context = CreateContext(memory);

        ConfigureBatchInitialize(context, size: 0x200, BatchInfoAddress);
        context[CpuRegister.Rdi] = 0;
        AssertCall(InvalidParameter, context, AjmExports.AjmBatchInitialize);

        ConfigureBatchInitialize(context, size: 0, BatchInfoAddress);
        AssertCall(InvalidParameter, context, AjmExports.AjmBatchInitialize);

        ConfigureBatchInitialize(context, size: 0x200, infoAddress: 0);
        AssertCall(InvalidParameter, context, AjmExports.AjmBatchInitialize);

        ConfigureBatchInitialize(context, size: 0x200, infoAddress: 0x9000);
        AssertCall(
            (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT,
            context,
            AjmExports.AjmBatchInitialize);
        Assert.All(infoAllocation, value => Assert.Equal(Canary, value));
    }

    [Theory]
    [InlineData(Generation.Gen4)]
    [InlineData(Generation.Gen5)]
    public void StatisticsHasExactMetadata(Generation generation)
    {
        var export = Assert.Single(
            SharpEmu.Generated.SysAbiExportRegistry.CreateExports(generation),
            candidate => candidate.Nid == StatisticsNid);

        Assert.Equal("sceAjmBatchJobGetStatistics", export.Name);
        Assert.Equal("libSceAjm", export.LibraryName);
        Assert.Equal(Generation.Gen4 | Generation.Gen5, export.Target);
    }

    [Fact]
    public void StatisticsWritesBoundedResultAndAppendsExactJob()
    {
        var (memory, context, buffer, infoAllocation) = CreateInitializedBatch(size: 0x200);
        var resultAllocation = FilledBuffer(StatisticsResultSize + 16);
        memory.AddRegion(ResultAllocationAddress, resultAllocation);
        ConfigureStatistics(context, ResultAddress);

        AssertCall(0, context, GetStatistics().Function);

        Assert.All(buffer.AsSpan(0, StatisticsJobSize).ToArray(), value => Assert.Equal(0, value));
        Assert.All(buffer.AsSpan(StatisticsJobSize).ToArray(), value => Assert.Equal(Canary, value));
        Assert.All(
            resultAllocation.AsSpan(8, StatisticsResultSize).ToArray(),
            value => Assert.Equal(0, value));
        Assert.All(resultAllocation.AsSpan(0, 8).ToArray(), value => Assert.Equal(Canary, value));
        Assert.All(resultAllocation.AsSpan(0x38).ToArray(), value => Assert.Equal(Canary, value));

        var info = infoAllocation.AsSpan(8, BatchInfoSize);
        Assert.Equal(BatchBufferAddress, ReadUInt64(info, 0x00));
        Assert.Equal((ulong)StatisticsJobSize, ReadUInt64(info, 0x08));
        Assert.Equal(0x200UL, ReadUInt64(info, 0x10));
        Assert.Equal(BatchBufferAddress, ReadUInt64(info, 0x18));
        Assert.Equal(0UL, ReadUInt64(info, 0x20));

        ConfigureStatistics(context, resultAddress: 0);
        AssertCall(0, context, GetStatistics().Function);
        Assert.All(
            buffer.AsSpan(0, StatisticsJobSize * 2).ToArray(),
            value => Assert.Equal(0, value));
        Assert.All(
            buffer.AsSpan(StatisticsJobSize * 2).ToArray(),
            value => Assert.Equal(Canary, value));
        Assert.Equal((ulong)(StatisticsJobSize * 2), ReadUInt64(info, 0x08));
        Assert.Equal(
            BatchBufferAddress + StatisticsJobSize,
            ReadUInt64(info, 0x18));
    }

    [Fact]
    public void StatisticsAllowsNullOptionalResult()
    {
        var (_, context, buffer, infoAllocation) = CreateInitializedBatch(size: 0x200);
        ConfigureStatistics(context, resultAddress: 0);

        AssertCall(0, context, GetStatistics().Function);

        Assert.All(buffer.AsSpan(0, StatisticsJobSize).ToArray(), value => Assert.Equal(0, value));
        Assert.Equal(
            (ulong)StatisticsJobSize,
            ReadUInt64(infoAllocation.AsSpan(8, BatchInfoSize), 0x08));
    }

    [Fact]
    public void StatisticsReportsCapacityAndMemoryFailuresWithoutAppending()
    {
        var (memory, context, buffer, infoAllocation) =
            CreateInitializedBatch(size: StatisticsJobSize - 1);
        var resultAllocation = FilledBuffer(StatisticsResultSize + 16);
        memory.AddRegion(ResultAllocationAddress, resultAllocation);
        ConfigureStatistics(context, ResultAddress);

        AssertCall(JobCreationError, context, GetStatistics().Function);
        Assert.All(
            resultAllocation.AsSpan(8, StatisticsResultSize).ToArray(),
            value => Assert.Equal(0, value));
        Assert.All(buffer, value => Assert.Equal(Canary, value));
        Assert.Equal(0UL, ReadUInt64(infoAllocation.AsSpan(8, BatchInfoSize), 0x08));

        ConfigureBatchInitialize(context, size: 0x200, BatchInfoAddress);
        AssertCall(0, context, AjmExports.AjmBatchInitialize);
        ConfigureStatistics(context, resultAddress: 0x9000);
        AssertCall(
            (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT,
            context,
            GetStatistics().Function);
        Assert.All(buffer, value => Assert.Equal(Canary, value));
        Assert.Equal(0UL, ReadUInt64(infoAllocation.AsSpan(8, BatchInfoSize), 0x08));

        context[CpuRegister.Rdi] = 0xA000;
        context[CpuRegister.Rsi] = 0;
        AssertCall(
            (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT,
            context,
            GetStatistics().Function);
    }

    [Fact]
    public void StatisticsRejectsNullBatchInfo()
    {
        var context = CreateContext(new FakeGuestMemory());
        context[CpuRegister.Rdi] = 0;
        context[CpuRegister.Rsi] = 0;

        AssertCall(InvalidParameter, context, GetStatistics().Function);
    }

    private static (
        FakeGuestMemory Memory,
        CpuContext Context,
        byte[] Buffer,
        byte[] InfoAllocation) CreateInitializedBatch(ulong size)
    {
        var memory = new FakeGuestMemory();
        var buffer = FilledBuffer(0x200);
        var infoAllocation = FilledBuffer(BatchInfoSize + 16);
        memory.AddRegion(BatchBufferAddress, buffer);
        memory.AddRegion(BatchInfoAllocationAddress, infoAllocation);
        var context = CreateContext(memory);
        ConfigureBatchInitialize(context, size, BatchInfoAddress);
        AssertCall(0, context, AjmExports.AjmBatchInitialize);
        return (memory, context, buffer, infoAllocation);
    }

    private static CpuContext CreateContext(FakeGuestMemory memory)
        => new(memory, Generation.Gen5);

    private static void ConfigureBatchInitialize(
        CpuContext context,
        ulong size,
        ulong infoAddress)
    {
        context[CpuRegister.Rdi] = BatchBufferAddress;
        context[CpuRegister.Rsi] = size;
        context[CpuRegister.Rdx] = infoAddress;
    }

    private static void ConfigureStatistics(CpuContext context, ulong resultAddress)
    {
        context[CpuRegister.Rdi] = BatchInfoAddress;
        context[CpuRegister.Rsi] = resultAddress;
        context[CpuRegister.Rdx] = 0xDEAD_BEEF;
        context.SetXmmRegister(0, BitConverter.SingleToUInt32Bits(0.25F), 0);
    }

    private static ExportedFunction GetStatistics()
        => Assert.Single(
            SharpEmu.Generated.SysAbiExportRegistry.CreateExports(Generation.Gen5),
            candidate => candidate.Nid == StatisticsNid);

    private static ulong ReadUInt64(ReadOnlySpan<byte> source, int offset)
        => BinaryPrimitives.ReadUInt64LittleEndian(source[offset..]);

    private static byte[] FilledBuffer(int length)
    {
        var buffer = new byte[length];
        Array.Fill(buffer, Canary);
        return buffer;
    }

    private static void AssertCall(
        int expected,
        CpuContext context,
        SysAbiFunction export)
    {
        Assert.Equal(expected, export(context));
        Assert.Equal(unchecked((ulong)expected), context[CpuRegister.Rax]);
    }
}
