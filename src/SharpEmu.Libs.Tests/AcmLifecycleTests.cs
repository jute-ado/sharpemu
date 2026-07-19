// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using SharpEmu.Libs.Audio;
using Xunit;

namespace SharpEmu.Libs.Tests;

public sealed class AcmLifecycleTests
{
    private const ulong FirstContextAddress = 0x1000;
    private const ulong SecondContextAddress = 0x2000;
    private const ulong BatchInfoArrayAddress = 0x3000;
    private const ulong BatchErrorAddress = 0x4000;
    private const ulong BatchIdAddress = 0x5000;
    private const int InvalidArgument =
        (int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT;
    private const int MemoryFault =
        (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT;

    [Fact]
    public void ContextCreateWritesUniqueThirtyTwoBitHandles()
    {
        var memory = new FakeGuestMemory();
        memory.AddRegion(
            FirstContextAddress,
            [0xA5, 0xA5, 0xA5, 0xA5, 0xCC, 0xCC, 0xCC, 0xCC]);
        memory.AddRegion(SecondContextAddress, new byte[sizeof(uint)]);
        var context = new CpuContext(memory, Generation.Gen5);

        var first = Create(context, FirstContextAddress);
        var second = Create(context, SecondContextAddress);

        try
        {
            Assert.NotEqual(0U, first);
            Assert.NotEqual(first, second);
            var canary = new byte[sizeof(uint)];
            Assert.True(memory.TryRead(
                FirstContextAddress + sizeof(uint),
                canary));
            Assert.Equal([0xCC, 0xCC, 0xCC, 0xCC], canary);
        }
        finally
        {
            Destroy(context, first);
            Destroy(context, second);
        }
    }

    [Fact]
    public void ContextCreateRejectsNullAndUnmappedOutput()
    {
        var context = new CpuContext(
            new FakeGuestMemory(),
            Generation.Gen5);

        context[CpuRegister.Rdi] = 0;
        AssertCall(InvalidArgument, context, AcmExports.AcmContextCreate);

        context[CpuRegister.Rdi] = FirstContextAddress;
        AssertCall(MemoryFault, context, AcmExports.AcmContextCreate);
    }

    [Fact]
    public void ContextDestroyInvalidatesHandle()
    {
        var memory = new FakeGuestMemory();
        memory.AddRegion(FirstContextAddress, new byte[sizeof(uint)]);
        var context = new CpuContext(memory, Generation.Gen5);
        var contextId = Create(context, FirstContextAddress);

        Destroy(context, contextId);

        context[CpuRegister.Rdi] = contextId;
        AssertCall(InvalidArgument, context, AcmExports.AcmContextDestroy);
        context[CpuRegister.Rdi] = 0;
        AssertCall(InvalidArgument, context, AcmExports.AcmContextDestroy);
    }

    [Fact]
    public void ContextExportsHaveExactGeneratedMetadata()
    {
        var exports = SharpEmu.Generated.SysAbiExportRegistry
            .CreateExports(Generation.Gen5);

        AssertExport(
            exports,
            "ZIXln2K3XMk",
            "sceAcmContextCreate");
        AssertExport(
            exports,
            "jBgBjAj02R8",
            "sceAcmContextDestroy");
        AssertExport(
            exports,
            "tW9W+CAG4FE",
            "sceAcmBatchStartBuffer");
        AssertExport(
            exports,
            "8fe55ktlNVo",
            "sceAcmBatchStartBuffers");
        AssertExport(
            exports,
            "RLN3gRlXJLE",
            "sceAcmBatchWait");
    }

    [Fact]
    public void BatchStartBuffersCreatesWaitableBatchAndClearsError()
    {
        var memory = new FakeGuestMemory();
        memory.AddRegion(FirstContextAddress, new byte[sizeof(uint)]);
        memory.AddRegion(BatchInfoArrayAddress, new byte[sizeof(ulong)]);
        var initialError = new byte[0x20];
        Array.Fill(initialError, (byte)0xA5);
        memory.AddRegion(BatchErrorAddress, initialError);
        memory.AddRegion(BatchIdAddress, new byte[sizeof(uint)]);
        var context = new CpuContext(memory, Generation.Gen5);
        var contextId = Create(context, FirstContextAddress);

        try
        {
            context[CpuRegister.Rdi] = contextId;
            context[CpuRegister.Rsi] = 1;
            context[CpuRegister.Rdx] = BatchInfoArrayAddress;
            context[CpuRegister.Rcx] = BatchErrorAddress;
            context[CpuRegister.R8] = BatchIdAddress;
            AssertCall(0, context, AcmExports.AcmBatchStartBuffers);
            Assert.True(context.TryReadUInt32(
                BatchIdAddress,
                out var batchId));
            Assert.NotEqual(0U, batchId);

            var error = new byte[0x20];
            Assert.True(memory.TryRead(BatchErrorAddress, error));
            Assert.Equal(new byte[0x20], error);

            context[CpuRegister.Rdi] = contextId;
            context[CpuRegister.Rsi] = batchId;
            context[CpuRegister.Rdx] = 0;
            AssertCall(0, context, AcmExports.AcmBatchWait);
        }
        finally
        {
            Destroy(context, contextId);
        }
    }

    [Fact]
    public void BatchSubmissionRequiresLiveContextAndOutput()
    {
        var context = new CpuContext(
            new FakeGuestMemory(),
            Generation.Gen5);
        context[CpuRegister.Rdi] = 1;
        context[CpuRegister.Rsi] = 0;
        context[CpuRegister.Rdx] = 0;
        context[CpuRegister.Rcx] = 0;
        context[CpuRegister.R8] = BatchIdAddress;
        AssertCall(
            InvalidArgument,
            context,
            AcmExports.AcmBatchStartBuffers);

        context[CpuRegister.Rdi] = 1;
        context[CpuRegister.Rsi] = 1;
        context[CpuRegister.Rdx] = 0;
        AssertCall(
            InvalidArgument,
            context,
            AcmExports.AcmBatchStartBuffers);
    }

    private static uint Create(CpuContext context, ulong outputAddress)
    {
        context[CpuRegister.Rdi] = outputAddress;
        AssertCall(0, context, AcmExports.AcmContextCreate);
        Assert.True(context.TryReadUInt32(outputAddress, out var contextId));
        return contextId;
    }

    private static void Destroy(CpuContext context, uint contextId)
    {
        context[CpuRegister.Rdi] = contextId;
        AssertCall(0, context, AcmExports.AcmContextDestroy);
    }

    private static void AssertCall(
        int expected,
        CpuContext context,
        Func<CpuContext, int> export)
    {
        Assert.Equal(expected, export(context));
        Assert.Equal(unchecked((ulong)expected), context[CpuRegister.Rax]);
    }

    private static void AssertExport(
        IReadOnlyList<ExportedFunction> exports,
        string nid,
        string name)
    {
        var export = Assert.Single(
            exports,
            candidate => candidate.Nid == nid);
        Assert.Equal(name, export.Name);
        Assert.Equal("libSceAcm", export.LibraryName);
    }
}
