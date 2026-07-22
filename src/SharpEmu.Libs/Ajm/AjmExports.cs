// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Threading;
using SharpEmu.HLE;

namespace SharpEmu.Libs.Ajm;

public static class AjmExports
{
    private const int InvalidParameter = unchecked((int)0x806A0001);
    private const int InvalidContext = unchecked((int)0x80930002);
    private const int InvalidInstance = unchecked((int)0x80930003);
    private const int InvalidInstanceParameter = unchecked((int)0x80930005);
    private const int OutOfResources = unchecked((int)0x80930007);
    private const int CodecAlreadyRegistered = unchecked((int)0x80930009);
    private const int CodecNotRegistered = unchecked((int)0x8093000A);
    private const int WrongRevisionFlag = unchecked((int)0x8093000B);
    private const int JobCreationError = unchecked((int)0x80930012);
    private const int BatchInfoSize = 0x28;
    private const int StatisticsJobSize = 0x58;
    private const int StatisticsResultSize = 0x30;
    private const uint MaxCodecTypeExclusive = 25;
    private const int MaxInstanceIndex = 0x2FFF;
    private static readonly ConcurrentDictionary<uint, AjmContextState> Contexts = new();
    private static int _nextContextId;

    private sealed class AjmContextState
    {
        public object Gate { get; } = new();

        public HashSet<uint> RegisteredCodecs { get; } = new();

        public Dictionary<uint, uint> InstancesBySlot { get; } = new();

        public Dictionary<ulong, ulong> RegisteredMemoryPages { get; } = new();

        public int NextInstanceIndex { get; set; }
    }

    [SysAbiExport(
        Nid = "dl+4eHSzUu4",
        ExportName = "sceAjmInitialize",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceAjm")]
    public static int AjmInitialize(CpuContext ctx)
    {
        var reserved = ctx[CpuRegister.Rdi];
        var outputAddress = ctx[CpuRegister.Rsi];
        if (outputAddress == 0)
        {
            return InvalidParameter;
        }

        var contextId = unchecked((uint)Interlocked.Increment(ref _nextContextId));
        Span<byte> value = stackalloc byte[sizeof(uint)];
        BinaryPrimitives.WriteUInt32LittleEndian(value, contextId);
        if (!ctx.Memory.TryWrite(outputAddress, value))
        {
            return InvalidParameter;
        }

        Contexts[contextId] = new AjmContextState();
        if (string.Equals(Environment.GetEnvironmentVariable("SHARPEMU_LOG_AJM"), "1", StringComparison.Ordinal))
        {
            Console.Error.WriteLine(
                $"[LOADER][TRACE] ajm.initialize reserved={reserved} out=0x{outputAddress:X16} context={contextId}");
        }

        ctx[CpuRegister.Rax] = 0;
        return 0;
    }

    [SysAbiExport(
        Nid = "MHur6qCsUus",
        ExportName = "sceAjmFinalize",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceAjm")]
    public static int AjmFinalize(CpuContext ctx)
    {
        Contexts.TryRemove(unchecked((uint)ctx[CpuRegister.Rdi]), out _);
        ctx[CpuRegister.Rax] = 0;
        return 0;
    }

    [SysAbiExport(
        Nid = "Q3dyFuwGn64",
        ExportName = "sceAjmModuleRegister",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceAjm")]
    public static int AjmModuleRegister(CpuContext ctx)
    {
        var contextId = unchecked((uint)ctx[CpuRegister.Rdi]);
        var codecType = unchecked((uint)ctx[CpuRegister.Rsi]);
        var reserved = ctx[CpuRegister.Rdx];
        if (codecType >= MaxCodecTypeExclusive)
        {
            return ctx.SetReturn(InvalidInstanceParameter);
        }

        if (!Contexts.TryGetValue(contextId, out var state))
        {
            return ctx.SetReturn(InvalidContext);
        }

        lock (state.Gate)
        {
            if (!state.RegisteredCodecs.Add(codecType))
            {
                return ctx.SetReturn(CodecAlreadyRegistered);
            }
        }

        if (string.Equals(Environment.GetEnvironmentVariable("SHARPEMU_LOG_AJM"), "1", StringComparison.Ordinal))
        {
            Console.Error.WriteLine(
                $"[LOADER][TRACE] ajm.module_register context={contextId} codec={codecType} reserved={reserved}");
        }

        ctx[CpuRegister.Rax] = 0;
        return 0;
    }

    [SysAbiExport(
        Nid = "AxoDrINp4J8",
        ExportName = "sceAjmInstanceCreate",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceAjm")]
    public static int AjmInstanceCreate(CpuContext ctx)
    {
        var contextId = unchecked((uint)ctx[CpuRegister.Rdi]);
        var codecType = unchecked((uint)ctx[CpuRegister.Rsi]);
        var flags = ctx[CpuRegister.Rdx];
        var outputAddress = ctx[CpuRegister.Rcx];
        if (!Contexts.TryGetValue(contextId, out var state))
        {
            return ctx.SetReturn(InvalidContext);
        }

        if (codecType >= MaxCodecTypeExclusive || outputAddress == 0)
        {
            return ctx.SetReturn(InvalidInstanceParameter);
        }

        if ((flags & 0x7) == 0)
        {
            return ctx.SetReturn(WrongRevisionFlag);
        }

        uint instanceId;
        lock (state.Gate)
        {
            if (!state.RegisteredCodecs.Contains(codecType))
            {
                return ctx.SetReturn(CodecNotRegistered);
            }

            if (state.InstancesBySlot.Count >= MaxInstanceIndex)
            {
                return ctx.SetReturn(OutOfResources);
            }

            var nextInstanceIndex = state.NextInstanceIndex;
            uint instanceSlot;
            do
            {
                nextInstanceIndex = nextInstanceIndex % MaxInstanceIndex + 1;
                instanceSlot = unchecked((uint)nextInstanceIndex);
            }
            while (state.InstancesBySlot.ContainsKey(instanceSlot));

            instanceId = (codecType << 14) | instanceSlot;
            Span<byte> value = stackalloc byte[sizeof(uint)];
            BinaryPrimitives.WriteUInt32LittleEndian(value, instanceId);
            if (!ctx.Memory.TryWrite(outputAddress, value))
            {
                return ctx.SetReturn(InvalidInstanceParameter);
            }

            state.NextInstanceIndex = nextInstanceIndex;
            state.InstancesBySlot.Add(instanceSlot, instanceId);
        }

        Trace($"instance_create context={contextId} codec={codecType} flags=0x{flags:X} instance=0x{instanceId:X8}");
        return ctx.SetReturn(0);
    }

    [SysAbiExport(
        Nid = "RbLbuKv8zho",
        ExportName = "sceAjmInstanceDestroy",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceAjm")]
    public static int AjmInstanceDestroy(CpuContext ctx)
    {
        var contextId = unchecked((uint)ctx[CpuRegister.Rdi]);
        var instanceId = unchecked((uint)ctx[CpuRegister.Rsi]);
        if (!Contexts.TryGetValue(contextId, out var state))
        {
            return ctx.SetReturn(InvalidContext);
        }

        var instanceSlot = instanceId & 0x3FFF;
        lock (state.Gate)
        {
            if (instanceSlot == 0 || !state.InstancesBySlot.Remove(instanceSlot))
            {
                return ctx.SetReturn(InvalidInstance);
            }
        }

        Trace($"instance_destroy context={contextId} instance=0x{instanceId:X8}");
        return ctx.SetReturn(0);
    }

    [SysAbiExport(
        Nid = "bkRHEYG6lEM",
        ExportName = "sceAjmMemoryRegister",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceAjm")]
    public static int AjmMemoryRegister(CpuContext ctx)
    {
        var contextId = unchecked((uint)ctx[CpuRegister.Rdi]);
        var address = ctx[CpuRegister.Rsi];
        var pageCount = ctx[CpuRegister.Rdx];
        if (!Contexts.TryGetValue(contextId, out var state))
        {
            return ctx.SetReturn(InvalidContext);
        }
        if (address == 0 || pageCount == 0)
        {
            return ctx.SetReturn(InvalidParameter);
        }

        // Guest memory is already shared with the software AJM path. Retain
        // the registration lifetime so the ABI remains coherent without
        // creating a second copy or allocation route.
        lock (state.Gate)
        {
            state.RegisteredMemoryPages[address] = pageCount;
        }

        Trace(
            $"memory_register context={contextId} " +
            $"address=0x{address:X16} pages={pageCount}");
        return ctx.SetReturn(0);
    }

    [SysAbiExport(
        Nid = "pIpGiaYkHkM",
        ExportName = "sceAjmMemoryUnregister",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceAjm")]
    public static int AjmMemoryUnregister(CpuContext ctx)
    {
        var contextId = unchecked((uint)ctx[CpuRegister.Rdi]);
        var address = ctx[CpuRegister.Rsi];
        if (!Contexts.TryGetValue(contextId, out var state))
        {
            return ctx.SetReturn(InvalidContext);
        }
        if (address == 0)
        {
            return ctx.SetReturn(InvalidParameter);
        }

        lock (state.Gate)
        {
            state.RegisteredMemoryPages.Remove(address);
        }

        Trace(
            $"memory_unregister context={contextId} " +
            $"address=0x{address:X16}");
        return ctx.SetReturn(0);
    }

    [SysAbiExport(
        Nid = "Wi7DtlLV+KI",
        ExportName = "sceAjmModuleUnregister",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceAjm")]
    public static int AjmModuleUnregister(CpuContext ctx)
    {
        ctx[CpuRegister.Rax] = 0;
        return 0;
    }

    [SysAbiExport(
        Nid = "MmpF1XsQiHw",
        ExportName = "sceAjmBatchInitialize",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceAjm")]
    public static int AjmBatchInitialize(CpuContext ctx)
    {
        var bufferAddress = ctx[CpuRegister.Rdi];
        var bufferSize = ctx[CpuRegister.Rsi];
        var infoAddress = ctx[CpuRegister.Rdx];
        if (bufferAddress == 0 || bufferSize == 0 || infoAddress == 0 ||
            !GuestAddress.IsRangeValid(bufferAddress, bufferSize))
        {
            return ctx.SetReturn(InvalidInstanceParameter);
        }

        Span<byte> info = stackalloc byte[BatchInfoSize];
        info.Clear();
        BinaryPrimitives.WriteUInt64LittleEndian(info[0x00..], bufferAddress);
        BinaryPrimitives.WriteUInt64LittleEndian(info[0x10..], bufferSize);
        return ctx.Memory.TryWrite(infoAddress, info)
            ? ctx.SetReturn(0)
            : ctx.SetReturn((int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
    }

    [SysAbiExport(
        Nid = "3cAg7xN995U",
        ExportName = "sceAjmBatchJobGetStatistics",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceAjm")]
    public static int AjmBatchJobGetStatistics(CpuContext ctx)
    {
        var infoAddress = ctx[CpuRegister.Rdi];
        var resultAddress = ctx[CpuRegister.Rsi];
        if (infoAddress == 0)
        {
            return ctx.SetReturn(InvalidInstanceParameter);
        }

        Span<byte> info = stackalloc byte[BatchInfoSize];
        if (!ctx.Memory.TryRead(infoAddress, info))
        {
            return ctx.SetReturn((int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }

        var bufferAddress = BinaryPrimitives.ReadUInt64LittleEndian(info[0x00..]);
        var offset = BinaryPrimitives.ReadUInt64LittleEndian(info[0x08..]);
        var bufferSize = BinaryPrimitives.ReadUInt64LittleEndian(info[0x10..]);

        Span<byte> result = stackalloc byte[StatisticsResultSize];
        result.Clear();
        if (resultAddress != 0 && !ctx.Memory.TryWrite(resultAddress, result))
        {
            return ctx.SetReturn((int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }

        if (bufferAddress == 0 || offset > bufferSize ||
            (ulong)StatisticsJobSize > bufferSize - offset ||
            !GuestAddress.TryAdd(bufferAddress, offset, out var jobAddress) ||
            !GuestAddress.IsRangeValid(jobAddress, StatisticsJobSize))
        {
            return ctx.SetReturn(JobCreationError);
        }

        Span<byte> job = stackalloc byte[StatisticsJobSize];
        job.Clear();
        if (!ctx.Memory.TryWrite(jobAddress, job))
        {
            return ctx.SetReturn((int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }

        BinaryPrimitives.WriteUInt64LittleEndian(
            info[0x08..],
            offset + StatisticsJobSize);
        BinaryPrimitives.WriteUInt64LittleEndian(info[0x18..], jobAddress);
        BinaryPrimitives.WriteUInt64LittleEndian(info[0x20..], 0);
        if (!ctx.Memory.TryWrite(infoAddress, info))
        {
            return ctx.SetReturn((int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }

        ctx.GetXmmRegister(0, out var intervalBits, out _);
        Trace(
            $"batch_job_get_statistics info=0x{infoAddress:X16} " +
            $"interval={BitConverter.UInt32BitsToSingle(unchecked((uint)intervalBits)):R} " +
            $"result=0x{resultAddress:X16} job=0x{jobAddress:X16}");
        return ctx.SetReturn(0);
    }

    internal static void ResetRuntimeState()
    {
        Contexts.Clear();
        Interlocked.Exchange(ref _nextContextId, 0);
    }

    private static void Trace(string message)
    {
        if (string.Equals(
                Environment.GetEnvironmentVariable("SHARPEMU_LOG_AJM"),
                "1",
                StringComparison.Ordinal))
        {
            Console.Error.WriteLine($"[LOADER][TRACE] ajm.{message}");
        }
    }
}
