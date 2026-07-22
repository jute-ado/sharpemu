// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using SharpEmu.Libs.Ajm;
using Xunit;

namespace SharpEmu.Libs.Tests;

public sealed class AjmBatchStartTests
{
    private const string StartNid = "5tOfnaClcqM";
    private const string WaitNid = "-qLsfDAywIY";
    private const int InvalidContext = unchecked((int)0x80930002);
    private const int InvalidBatch = unchecked((int)0x80930004);
    private const int InvalidParameter = unchecked((int)0x80930005);
    private const int MemoryFault =
        (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT;
    private const ulong ContextOutputAddress = 0x1000;
    private const ulong BatchBufferAddress = 0x2000;
    private const ulong BatchInfoAllocationAddress = 0x3000;
    private const ulong BatchInfoAddress = BatchInfoAllocationAddress + 8;
    private const ulong ErrorAllocationAddress = 0x4000;
    private const ulong ErrorAddress = ErrorAllocationAddress + 8;
    private const ulong BatchIdAllocationAddress = 0x5000;
    private const ulong BatchIdAddress = BatchIdAllocationAddress + 8;
    private const ulong SecondBatchIdAllocationAddress = 0x6000;
    private const ulong SecondBatchIdAddress = SecondBatchIdAllocationAddress + 8;
    private const int BatchInfoSize = 0x28;
    private const int BatchErrorSize = 0x20;
    private const byte Canary = 0xA5;

    [Fact]
    public void StartHasExactGen5Metadata()
    {
        var export = Assert.Single(
            SharpEmu.Generated.SysAbiExportRegistry.CreateExports(Generation.Gen5),
            candidate => candidate.Nid == StartNid);

        Assert.Equal("sceAjmBatchStart", export.Name);
        Assert.Equal("libSceAjm", export.LibraryName);
        Assert.Equal(Generation.Gen5, export.Target);
    }

    [Fact]
    public void StartIsNotExposedToGen4()
    {
        Assert.DoesNotContain(
            SharpEmu.Generated.SysAbiExportRegistry.CreateExports(Generation.Gen4),
            candidate => candidate.Nid == StartNid);
    }

    [Theory]
    [InlineData(Generation.Gen4)]
    [InlineData(Generation.Gen5)]
    public void WaitHasExactSharedMetadata(Generation generation)
    {
        var export = Assert.Single(
            SharpEmu.Generated.SysAbiExportRegistry.CreateExports(generation),
            candidate => candidate.Nid == WaitNid);

        Assert.Equal("sceAjmBatchWait", export.Name);
        Assert.Equal("libSceAjm", export.LibraryName);
        Assert.Equal(Generation.Gen4 | Generation.Gen5, export.Target);
    }

    [Fact]
    public void StartClearsExactErrorAndWritesUniqueBatchIds()
    {
        var fixture = CreateFixture();
        try
        {
            var infoBefore = fixture.InfoAllocation.ToArray();
            ConfigureStart(
                fixture.Context,
                fixture.ContextId,
                BatchInfoAddress,
                errorAddress: ErrorAddress,
                outputAddress: BatchIdAddress);

            AssertCall(0, fixture.Context, GetStart().Function);

            Assert.True(fixture.Context.TryReadUInt32(BatchIdAddress, out var firstBatchId));
            Assert.NotEqual(0U, firstBatchId);
            Assert.Equal(infoBefore, fixture.InfoAllocation);
            Assert.All(
                fixture.ErrorAllocation.AsSpan(8, BatchErrorSize).ToArray(),
                value => Assert.Equal(0, value));
            AssertCanaries(fixture.ErrorAllocation, BatchErrorSize);
            AssertCanaries(fixture.BatchIdAllocation, sizeof(uint));

            ConfigureStart(
                fixture.Context,
                fixture.ContextId,
                BatchInfoAddress,
                errorAddress: 0,
                outputAddress: SecondBatchIdAddress);
            AssertCall(0, fixture.Context, GetStart().Function);
            Assert.True(fixture.Context.TryReadUInt32(
                SecondBatchIdAddress,
                out var secondBatchId));
            Assert.NotEqual(0U, secondBatchId);
            Assert.NotEqual(firstBatchId, secondBatchId);
            AssertCanaries(fixture.SecondBatchIdAllocation, sizeof(uint));
        }
        finally
        {
            Finalize(fixture.Context, fixture.ContextId);
        }
    }

    [Fact]
    public void StartRejectsInvalidContextWithoutTouchingGuestOutputs()
    {
        var fixture = CreateFixture();
        try
        {
            ConfigureStart(
                fixture.Context,
                contextId: fixture.ContextId + 0x1000,
                BatchInfoAddress,
                ErrorAddress,
                BatchIdAddress);

            AssertCall(InvalidContext, fixture.Context, GetStart().Function);

            Assert.All(fixture.ErrorAllocation, value => Assert.Equal(Canary, value));
            Assert.All(fixture.BatchIdAllocation, value => Assert.Equal(Canary, value));
        }
        finally
        {
            Finalize(fixture.Context, fixture.ContextId);
        }
    }

    [Fact]
    public void StartRejectsNullRequiredPointersWithoutTouchingGuestOutputs()
    {
        var fixture = CreateFixture();
        try
        {
            ConfigureStart(
                fixture.Context,
                fixture.ContextId,
                infoAddress: 0,
                ErrorAddress,
                BatchIdAddress);
            AssertCall(InvalidParameter, fixture.Context, GetStart().Function);

            ConfigureStart(
                fixture.Context,
                fixture.ContextId,
                BatchInfoAddress,
                ErrorAddress,
                outputAddress: 0);
            AssertCall(InvalidParameter, fixture.Context, GetStart().Function);

            Assert.All(fixture.ErrorAllocation, value => Assert.Equal(Canary, value));
            Assert.All(fixture.BatchIdAllocation, value => Assert.Equal(Canary, value));
        }
        finally
        {
            Finalize(fixture.Context, fixture.ContextId);
        }
    }

    [Fact]
    public void StartReportsGuestMemoryFailuresTransactionally()
    {
        var fixture = CreateFixture();
        try
        {
            ConfigureStart(
                fixture.Context,
                fixture.ContextId,
                infoAddress: 0x9000,
                ErrorAddress,
                BatchIdAddress);
            AssertCall(MemoryFault, fixture.Context, GetStart().Function);
            Assert.All(fixture.ErrorAllocation, value => Assert.Equal(Canary, value));
            Assert.All(fixture.BatchIdAllocation, value => Assert.Equal(Canary, value));

            ConfigureStart(
                fixture.Context,
                fixture.ContextId,
                BatchInfoAddress,
                errorAddress: 0x9000,
                BatchIdAddress);
            AssertCall(MemoryFault, fixture.Context, GetStart().Function);
            Assert.All(fixture.BatchIdAllocation, value => Assert.Equal(Canary, value));

            ConfigureStart(
                fixture.Context,
                fixture.ContextId,
                BatchInfoAddress,
                ErrorAddress,
                outputAddress: 0x9000);
            AssertCall(MemoryFault, fixture.Context, GetStart().Function);
            Assert.All(
                fixture.ErrorAllocation.AsSpan(8, BatchErrorSize).ToArray(),
                value => Assert.Equal(0, value));
            AssertCanaries(fixture.ErrorAllocation, BatchErrorSize);
        }
        finally
        {
            Finalize(fixture.Context, fixture.ContextId);
        }
    }

    [Fact]
    public void WaitClearsExactErrorAndConsumesCompletedBatch()
    {
        var fixture = CreateFixture();
        try
        {
            var batchId = StartBatch(fixture);
            ConfigureWait(
                fixture.Context,
                fixture.ContextId,
                batchId,
                timeout: 0,
                ErrorAddress);

            AssertCall(0, fixture.Context, GetWait().Function);

            Assert.All(
                fixture.ErrorAllocation.AsSpan(8, BatchErrorSize).ToArray(),
                value => Assert.Equal(0, value));
            AssertCanaries(fixture.ErrorAllocation, BatchErrorSize);

            ConfigureWait(
                fixture.Context,
                fixture.ContextId,
                batchId,
                timeout: uint.MaxValue,
                errorAddress: 0);
            AssertCall(InvalidBatch, fixture.Context, GetWait().Function);
        }
        finally
        {
            Finalize(fixture.Context, fixture.ContextId);
        }
    }

    [Fact]
    public void WaitRejectsInvalidContextAndBatchWithoutTouchingError()
    {
        var fixture = CreateFixture();
        try
        {
            var batchId = StartBatch(fixture);
            ConfigureWait(
                fixture.Context,
                fixture.ContextId + 0x1000,
                batchId,
                timeout: 0,
                ErrorAddress);
            AssertCall(InvalidContext, fixture.Context, GetWait().Function);

            ConfigureWait(
                fixture.Context,
                fixture.ContextId,
                batchId + 0x1000,
                timeout: 0,
                ErrorAddress);
            AssertCall(InvalidBatch, fixture.Context, GetWait().Function);

            Assert.All(fixture.ErrorAllocation, value => Assert.Equal(Canary, value));
        }
        finally
        {
            Finalize(fixture.Context, fixture.ContextId);
        }
    }

    [Fact]
    public void WaitMemoryFaultKeepsBatchAvailableForRetry()
    {
        var fixture = CreateFixture();
        try
        {
            var batchId = StartBatch(fixture);
            ConfigureWait(
                fixture.Context,
                fixture.ContextId,
                batchId,
                timeout: 0,
                errorAddress: 0x9000);
            AssertCall(MemoryFault, fixture.Context, GetWait().Function);

            ConfigureWait(
                fixture.Context,
                fixture.ContextId,
                batchId,
                timeout: 0,
                ErrorAddress);
            AssertCall(0, fixture.Context, GetWait().Function);
            Assert.All(
                fixture.ErrorAllocation.AsSpan(8, BatchErrorSize).ToArray(),
                value => Assert.Equal(0, value));
            AssertCanaries(fixture.ErrorAllocation, BatchErrorSize);
        }
        finally
        {
            Finalize(fixture.Context, fixture.ContextId);
        }
    }

    private static Fixture CreateFixture()
    {
        var memory = new FakeGuestMemory();
        var infoAllocation = FilledBuffer(BatchInfoSize + 16);
        var errorAllocation = FilledBuffer(BatchErrorSize + 16);
        var batchIdAllocation = FilledBuffer(sizeof(uint) + 16);
        var secondBatchIdAllocation = FilledBuffer(sizeof(uint) + 16);
        memory.AddRegion(ContextOutputAddress, new byte[sizeof(uint)]);
        memory.AddRegion(BatchBufferAddress, new byte[0x100]);
        memory.AddRegion(BatchInfoAllocationAddress, infoAllocation);
        memory.AddRegion(ErrorAllocationAddress, errorAllocation);
        memory.AddRegion(BatchIdAllocationAddress, batchIdAllocation);
        memory.AddRegion(SecondBatchIdAllocationAddress, secondBatchIdAllocation);
        var context = new CpuContext(memory, Generation.Gen5);

        context[CpuRegister.Rdi] = 0;
        context[CpuRegister.Rsi] = ContextOutputAddress;
        AssertCall(0, context, AjmExports.AjmInitialize);
        Assert.True(context.TryReadUInt32(ContextOutputAddress, out var contextId));

        context[CpuRegister.Rdi] = BatchBufferAddress;
        context[CpuRegister.Rsi] = 0x100;
        context[CpuRegister.Rdx] = BatchInfoAddress;
        AssertCall(0, context, AjmExports.AjmBatchInitialize);

        return new Fixture(
            context,
            contextId,
            infoAllocation,
            errorAllocation,
            batchIdAllocation,
            secondBatchIdAllocation);
    }

    private static void ConfigureStart(
        CpuContext context,
        uint contextId,
        ulong infoAddress,
        ulong errorAddress,
        ulong outputAddress)
    {
        context[CpuRegister.Rdi] = contextId;
        context[CpuRegister.Rsi] = infoAddress;
        context[CpuRegister.Rdx] = 0x28;
        context[CpuRegister.Rcx] = errorAddress;
        context[CpuRegister.R8] = outputAddress;
    }

    private static uint StartBatch(Fixture fixture)
    {
        ConfigureStart(
            fixture.Context,
            fixture.ContextId,
            BatchInfoAddress,
            errorAddress: 0,
            BatchIdAddress);
        AssertCall(0, fixture.Context, GetStart().Function);
        Assert.True(fixture.Context.TryReadUInt32(BatchIdAddress, out var batchId));
        Assert.NotEqual(0U, batchId);
        return batchId;
    }

    private static void ConfigureWait(
        CpuContext context,
        uint contextId,
        uint batchId,
        uint timeout,
        ulong errorAddress)
    {
        context[CpuRegister.Rdi] = contextId;
        context[CpuRegister.Rsi] = batchId;
        context[CpuRegister.Rdx] = timeout;
        context[CpuRegister.Rcx] = errorAddress;
    }

    private static ExportedFunction GetStart()
        => Assert.Single(
            SharpEmu.Generated.SysAbiExportRegistry.CreateExports(Generation.Gen5),
            candidate => candidate.Nid == StartNid);

    private static ExportedFunction GetWait()
        => Assert.Single(
            SharpEmu.Generated.SysAbiExportRegistry.CreateExports(Generation.Gen5),
            candidate => candidate.Nid == WaitNid);

    private static void Finalize(CpuContext context, uint contextId)
    {
        context[CpuRegister.Rdi] = contextId;
        AssertCall(0, context, AjmExports.AjmFinalize);
    }

    private static byte[] FilledBuffer(int length)
    {
        var buffer = new byte[length];
        Array.Fill(buffer, Canary);
        return buffer;
    }

    private static void AssertCanaries(byte[] allocation, int payloadSize)
    {
        Assert.All(allocation.AsSpan(0, 8).ToArray(), value => Assert.Equal(Canary, value));
        Assert.All(
            allocation.AsSpan(8 + payloadSize).ToArray(),
            value => Assert.Equal(Canary, value));
    }

    private static void AssertCall(
        int expected,
        CpuContext context,
        SysAbiFunction export)
    {
        Assert.Equal(expected, export(context));
        Assert.Equal(unchecked((ulong)expected), context[CpuRegister.Rax]);
    }

    private sealed record Fixture(
        CpuContext Context,
        uint ContextId,
        byte[] InfoAllocation,
        byte[] ErrorAllocation,
        byte[] BatchIdAllocation,
        byte[] SecondBatchIdAllocation);
}
