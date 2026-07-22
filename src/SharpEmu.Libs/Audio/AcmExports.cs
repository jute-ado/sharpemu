// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using System.Collections.Concurrent;
using SharpEmu.HLE;

namespace SharpEmu.Libs.Audio;

public static class AcmExports
{
    private const int BatchErrorSize = 0x20;
    private static readonly ConcurrentDictionary<uint, ContextState> Contexts = new();
    private static int _nextContextId;
    private static int _nextBatchId;

    internal static void ResetRuntimeState()
    {
        Contexts.Clear();
        Interlocked.Exchange(ref _nextContextId, 0);
        Interlocked.Exchange(ref _nextBatchId, 0);
    }

    private sealed class ContextState
    {
        public ConcurrentDictionary<uint, byte> Batches { get; } = new();
    }

    [SysAbiExport(
        Nid = "ZIXln2K3XMk",
        ExportName = "sceAcmContextCreate",
        Target = Generation.Gen5,
        LibraryName = "libSceAcm")]
    public static int AcmContextCreate(CpuContext ctx)
    {
        var outputAddress = ctx[CpuRegister.Rdi];
        if (outputAddress == 0)
        {
            return ctx.SetReturn(
                (int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        }

        uint contextId;
        do
        {
            contextId = unchecked((uint)Interlocked.Increment(
                ref _nextContextId));
        }
        while (contextId == 0 ||
               !Contexts.TryAdd(contextId, new ContextState()));

        if (!ctx.TryWriteUInt32(outputAddress, contextId))
        {
            Contexts.TryRemove(contextId, out _);
            return ctx.SetReturn(
                (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }

        return ctx.SetReturn(0);
    }

    [SysAbiExport(
        Nid = "jBgBjAj02R8",
        ExportName = "sceAcmContextDestroy",
        Target = Generation.Gen5,
        LibraryName = "libSceAcm")]
    public static int AcmContextDestroy(CpuContext ctx)
    {
        var contextId = unchecked((uint)ctx[CpuRegister.Rdi]);
        return contextId != 0 && Contexts.TryRemove(contextId, out _)
            ? ctx.SetReturn(0)
            : ctx.SetReturn(
                (int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
    }

    [SysAbiExport(
        Nid = "tW9W+CAG4FE",
        ExportName = "sceAcmBatchStartBuffer",
        Target = Generation.Gen5,
        LibraryName = "libSceAcm")]
    public static int AcmBatchStartBuffer(CpuContext ctx)
    {
        var contextId = unchecked((uint)ctx[CpuRegister.Rdi]);
        var commandsAddress = ctx[CpuRegister.Rsi];
        var commandsSize = ctx[CpuRegister.Rdx];
        var errorAddress = ctx[CpuRegister.Rcx];
        var outputBatchAddress = ctx[CpuRegister.R8];
        if (commandsAddress == 0 || commandsSize == 0)
        {
            return ctx.SetReturn(
                (int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        }

        return StartBatch(
            ctx,
            contextId,
            errorAddress,
            outputBatchAddress);
    }

    [SysAbiExport(
        Nid = "8fe55ktlNVo",
        ExportName = "sceAcmBatchStartBuffers",
        Target = Generation.Gen5,
        LibraryName = "libSceAcm")]
    public static int AcmBatchStartBuffers(CpuContext ctx)
    {
        var contextId = unchecked((uint)ctx[CpuRegister.Rdi]);
        var batchInfoCount = unchecked((uint)ctx[CpuRegister.Rsi]);
        var batchInfoArrayAddress = ctx[CpuRegister.Rdx];
        var errorAddress = ctx[CpuRegister.Rcx];
        var outputBatchAddress = ctx[CpuRegister.R8];
        if (batchInfoCount != 0 && batchInfoArrayAddress == 0)
        {
            return ctx.SetReturn(
                (int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        }

        return StartBatch(
            ctx,
            contextId,
            errorAddress,
            outputBatchAddress);
    }

    [SysAbiExport(
        Nid = "RLN3gRlXJLE",
        ExportName = "sceAcmBatchWait",
        Target = Generation.Gen5,
        LibraryName = "libSceAcm")]
    public static int AcmBatchWait(CpuContext ctx)
    {
        var contextId = unchecked((uint)ctx[CpuRegister.Rdi]);
        var batchId = unchecked((uint)ctx[CpuRegister.Rsi]);
        if (!Contexts.TryGetValue(contextId, out var state) ||
            batchId == 0 ||
            !state.Batches.ContainsKey(batchId))
        {
            return ctx.SetReturn(
                (int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        }

        return ctx.SetReturn(0);
    }

    [SysAbiExport(
        Nid = "u70oWo92SYQ",
        ExportName = "sceAcm_ConvReverb_SharedInput",
        Target = Generation.Gen5,
        LibraryName = "libSceAcm")]
    public static int AcmConvReverbSharedInput(CpuContext ctx)
    {
        var batchInfoAddress = ctx[CpuRegister.Rdi];
        if (batchInfoAddress == 0)
        {
            return ctx.SetReturn(0);
        }

        Span<byte> batchInfo = stackalloc byte[0x18];
        if (!ctx.Memory.TryRead(batchInfoAddress, batchInfo))
        {
            return ctx.SetReturn(
                (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }

        var bufferAddress = BinaryPrimitives.ReadUInt64LittleEndian(
            batchInfo[0x00..]);
        var bufferSize = BinaryPrimitives.ReadUInt64LittleEndian(
            batchInfo[0x10..]);
        if (bufferAddress == 0 || bufferSize == 0)
        {
            return ctx.SetReturn(0);
        }

        var offset = BinaryPrimitives.ReadUInt64LittleEndian(batchInfo[0x08..]);
        var advancedOffset = offset >= bufferSize || bufferSize - offset <= 1024
            ? bufferSize
            : offset + 1024;
        return ctx.TryWriteUInt64(batchInfoAddress + 0x08, advancedOffset)
            ? ctx.SetReturn(0)
            : ctx.SetReturn(
                (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
    }

    private static int StartBatch(
        CpuContext ctx,
        uint contextId,
        ulong errorAddress,
        ulong outputBatchAddress)
    {
        if (!Contexts.TryGetValue(contextId, out var state) ||
            outputBatchAddress == 0)
        {
            return ctx.SetReturn(
                (int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        }

        Span<byte> emptyError = stackalloc byte[BatchErrorSize];
        emptyError.Clear();
        if (errorAddress != 0 &&
            !ctx.Memory.TryWrite(errorAddress, emptyError))
        {
            return ctx.SetReturn(
                (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }

        uint batchId;
        do
        {
            batchId = unchecked((uint)Interlocked.Increment(
                ref _nextBatchId));
        }
        while (batchId == 0 || !state.Batches.TryAdd(batchId, 0));

        if (!ctx.TryWriteUInt32(outputBatchAddress, batchId))
        {
            state.Batches.TryRemove(batchId, out _);
            return ctx.SetReturn(
                (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }

        return ctx.SetReturn(0);
    }
}
