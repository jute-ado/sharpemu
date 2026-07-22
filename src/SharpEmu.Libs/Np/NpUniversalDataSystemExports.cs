// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using SharpEmu.Libs.Kernel;
using System.Buffers.Binary;

namespace SharpEmu.Libs.Np;

public static class NpUniversalDataSystemExports
{
    private const int NpUniversalDataSystemErrorInvalidArgument = unchecked((int)0x80553102);
    private const ulong OpaqueObjectSize = 0x10;
    private static int _nextHandle = 1;

    [SysAbiExport(
        Nid = "sjaobBgqeB4",
        ExportName = "sceNpUniversalDataSystemInitialize",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceNpUniversalDataSystem")]
    public static int NpUniversalDataSystemInitialize(CpuContext ctx)
    {
        var parameterAddress = ctx[CpuRegister.Rdi];
        if (parameterAddress == 0)
        {
            return ctx.SetReturn(NpUniversalDataSystemErrorInvalidArgument, typeof(long));
        }

        Span<byte> parameters = stackalloc byte[16];
        return ctx.Memory.TryRead(parameterAddress, parameters)
            ? ctx.SetReturn(0, typeof(long))
            : ctx.SetReturn((int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT, typeof(long));
    }

    [SysAbiExport(
        Nid = "5zBnau1uIEo",
        ExportName = "sceNpUniversalDataSystemCreateContext",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceNpUniversalDataSystem")]
    public static int NpUniversalDataSystemCreateContext(CpuContext ctx)
    {
        var contextAddress = ctx[CpuRegister.Rdi];
        if (contextAddress == 0)
        {
            return ctx.SetReturn(0, typeof(long));
        }

        Span<byte> context = stackalloc byte[sizeof(int)];
        BinaryPrimitives.WriteInt32LittleEndian(context, 1);
        return ctx.Memory.TryWrite(contextAddress, context)
            ? ctx.SetReturn(0, typeof(long))
            : ctx.SetReturn((int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT, typeof(long));
    }

    [SysAbiExport(
        Nid = "hT0IAEvN+M0",
        ExportName = "sceNpUniversalDataSystemCreateHandle",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceNpUniversalDataSystem")]
    public static int NpUniversalDataSystemCreateHandle(CpuContext ctx)
    {
        var outputAddress = ctx[CpuRegister.Rdi];
        if (outputAddress == 0)
        {
            return ctx.SetReturn(
                NpUniversalDataSystemErrorInvalidArgument,
                typeof(long));
        }

        var handle = Interlocked.Increment(ref _nextHandle);
        return ctx.TryWriteInt32(outputAddress, handle)
            ? ctx.SetReturn(0, typeof(long))
            : ctx.SetReturn(
                (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT,
                typeof(long));
    }

    [SysAbiExport(
        Nid = "p+GcLqwpL9M",
        ExportName = "sceNpUniversalDataSystemCreateEvent",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceNpUniversalDataSystem")]
    public static int NpUniversalDataSystemCreateEvent(CpuContext ctx)
    {
        var eventNameAddress = ctx[CpuRegister.Rdi];
        var suppliedPropertyAddress = ctx[CpuRegister.Rsi];
        var eventOutAddress = ctx[CpuRegister.Rdx];
        var propertyOutAddress = ctx[CpuRegister.Rcx];
        if (eventNameAddress == 0 || eventOutAddress == 0)
        {
            return ctx.SetReturn(NpUniversalDataSystemErrorInvalidArgument, typeof(long));
        }

        if (!KernelMemoryCompatExports.TryReadCString(
                ctx,
                eventNameAddress,
                4096,
                out _) ||
            !TryAllocateOpaqueObject(ctx, out var eventAddress))
        {
            return ctx.SetReturn(
                (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT,
                typeof(long));
        }

        if (!ctx.TryWriteUInt64(eventOutAddress, eventAddress))
        {
            return ctx.SetReturn(
                (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT,
                typeof(long));
        }

        if (propertyOutAddress != 0)
        {
            var propertyAddress = suppliedPropertyAddress;
            if (propertyAddress == 0 &&
                !TryAllocateOpaqueObject(ctx, out propertyAddress))
            {
                return ctx.SetReturn(
                    (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT,
                    typeof(long));
            }

            if (!ctx.TryWriteUInt64(propertyOutAddress, propertyAddress))
            {
                return ctx.SetReturn(
                    (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT,
                    typeof(long));
            }
        }

        return ctx.SetReturn(0, typeof(long));
    }

    [SysAbiExport(
        Nid = "+s14jq-KGYw",
        ExportName = "sceNpUniversalDataSystemEventEstimateSize",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceNpUniversalDataSystem")]
    public static int NpUniversalDataSystemEventEstimateSize(CpuContext ctx)
    {
        var eventAddress = ctx[CpuRegister.Rdi];
        var sizeAddress = ctx[CpuRegister.Rsi];
        if (eventAddress == 0 || sizeAddress == 0)
        {
            return ctx.SetReturn(NpUniversalDataSystemErrorInvalidArgument, typeof(long));
        }

        Span<byte> eventProbe = stackalloc byte[1];
        return ctx.Memory.TryRead(eventAddress, eventProbe) &&
               ctx.TryWriteUInt64(sizeAddress, 3)
            ? ctx.SetReturn(0, typeof(long))
            : ctx.SetReturn(
                (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT,
                typeof(long));
    }

    [SysAbiExport(
        Nid = "vj6CQGWtEBg",
        ExportName = "sceNpUniversalDataSystemEventToString",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceNpUniversalDataSystem")]
    public static int NpUniversalDataSystemEventToString(CpuContext ctx)
    {
        var eventAddress = ctx[CpuRegister.Rdi];
        var bufferAddress = ctx[CpuRegister.Rsi];
        var bufferSize = ctx[CpuRegister.Rdx];
        var stringSizeAddress = ctx[CpuRegister.Rcx];
        if (eventAddress == 0)
        {
            return ctx.SetReturn(NpUniversalDataSystemErrorInvalidArgument, typeof(long));
        }

        Span<byte> eventProbe = stackalloc byte[1];
        if (!ctx.Memory.TryRead(eventAddress, eventProbe) ||
            (stringSizeAddress != 0 &&
             !ctx.TryWriteUInt64(stringSizeAddress, 3)))
        {
            return ctx.SetReturn(
                (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT,
                typeof(long));
        }

        if (bufferAddress != 0 && bufferSize != 0)
        {
            Span<byte> serialized = stackalloc byte[] { (byte)'{', (byte)'}', 0 };
            var writeLength = (int)Math.Min(bufferSize, (ulong)serialized.Length);
            serialized[writeLength - 1] = 0;
            if (!ctx.Memory.TryWrite(bufferAddress, serialized[..writeLength]))
            {
                return ctx.SetReturn(
                    (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT,
                    typeof(long));
            }
        }

        return ctx.SetReturn(0, typeof(long));
    }

    [SysAbiExport(
        Nid = "wG+84pnNIuo",
        ExportName = "sceNpUniversalDataSystemDestroyEvent",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceNpUniversalDataSystem")]
    public static int NpUniversalDataSystemDestroyEvent(CpuContext ctx)
    {
        return ctx.SetReturn(0, typeof(long));
    }

    [SysAbiExport(
        Nid = "MfDb+4Nln64",
        ExportName = "sceNpUniversalDataSystemEventPropertyObjectSetString",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceNpUniversalDataSystem")]
    public static int NpUniversalDataSystemEventPropertyObjectSetString(CpuContext ctx)
    {
        var propertyObjectAddress = ctx[CpuRegister.Rdi];
        var keyAddress = ctx[CpuRegister.Rsi];
        var valueAddress = ctx[CpuRegister.Rdx];
        if (propertyObjectAddress == 0 ||
            keyAddress == 0 ||
            valueAddress == 0)
        {
            return ctx.SetReturn(NpUniversalDataSystemErrorInvalidArgument, typeof(long));
        }

        Span<byte> probe = stackalloc byte[1];
        return ctx.Memory.TryRead(propertyObjectAddress, probe) &&
               KernelMemoryCompatExports.TryReadCString(
                   ctx,
                   keyAddress,
                   4096,
                   out _) &&
               KernelMemoryCompatExports.TryReadCString(
                   ctx,
                   valueAddress,
                   4096,
                   out _)
            ? ctx.SetReturn(0, typeof(long))
            : ctx.SetReturn((int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT, typeof(long));
    }

    [SysAbiExport(
        Nid = "Wxbg5x3pTXA",
        ExportName = "sceNpUniversalDataSystemEventPropertyObjectSetArray",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceNpUniversalDataSystem")]
    public static int NpUniversalDataSystemEventPropertyObjectSetArray(CpuContext ctx)
    {
        var propertyObjectAddress = ctx[CpuRegister.Rdi];
        var keyAddress = ctx[CpuRegister.Rsi];
        var valueAddress = ctx[CpuRegister.Rdx];
        var valueOutAddress = ctx[CpuRegister.Rcx];
        if (propertyObjectAddress == 0 || keyAddress == 0)
        {
            return ctx.SetReturn(NpUniversalDataSystemErrorInvalidArgument, typeof(long));
        }

        Span<byte> probe = stackalloc byte[1];
        if (!ctx.Memory.TryRead(propertyObjectAddress, probe) ||
            !KernelMemoryCompatExports.TryReadCString(
                ctx,
                keyAddress,
                4096,
                out _))
        {
            return ctx.SetReturn((int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT, typeof(long));
        }

        if (valueAddress != 0 && !ctx.Memory.TryRead(valueAddress, probe))
        {
            return ctx.SetReturn((int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT, typeof(long));
        }

        if (valueOutAddress != 0)
        {
            if (valueAddress == 0 &&
                !TryAllocateOpaqueObject(ctx, out valueAddress))
            {
                return ctx.SetReturn(
                    (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT,
                    typeof(long));
            }

            if (!ctx.TryWriteUInt64(valueOutAddress, valueAddress))
            {
                return ctx.SetReturn(
                    (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT,
                    typeof(long));
            }
        }

        return ctx.SetReturn(0, typeof(long));
    }

    [SysAbiExport(
        Nid = "Hm7qubT3b70",
        ExportName = "sceNpUniversalDataSystemCreateEventPropertyArray",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceNpUniversalDataSystem")]
    public static int NpUniversalDataSystemCreateEventPropertyArray(
        CpuContext ctx)
    {
        var outputAddress = ctx[CpuRegister.Rdi];
        if (outputAddress == 0)
        {
            return ctx.SetReturn(
                NpUniversalDataSystemErrorInvalidArgument,
                typeof(long));
        }

        return TryAllocateOpaqueObject(ctx, out var arrayAddress) &&
               ctx.TryWriteUInt64(outputAddress, arrayAddress)
            ? ctx.SetReturn(0, typeof(long))
            : ctx.SetReturn(
                (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT,
                typeof(long));
    }

    [SysAbiExport(
        Nid = "s6W4Zl4Slgk",
        ExportName = "sceNpUniversalDataSystemCreateEventPropertyObject",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceNpUniversalDataSystem")]
    public static int NpUniversalDataSystemCreateEventPropertyObject(
        CpuContext ctx)
    {
        var outputAddress = ctx[CpuRegister.Rdi];
        if (outputAddress == 0)
        {
            return ctx.SetReturn(
                NpUniversalDataSystemErrorInvalidArgument,
                typeof(long));
        }

        return TryAllocateOpaqueObject(ctx, out var objectAddress) &&
               ctx.TryWriteUInt64(outputAddress, objectAddress)
            ? ctx.SetReturn(0, typeof(long))
            : ctx.SetReturn(
                (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT,
                typeof(long));
    }

    [SysAbiExport(
        Nid = "4llLk7YJRTE",
        ExportName = "sceNpUniversalDataSystemEventPropertyArraySetString",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceNpUniversalDataSystem")]
    public static int NpUniversalDataSystemEventPropertyArraySetString(CpuContext ctx)
    {
        var arrayAddress = ctx[CpuRegister.Rdi];
        var valueAddress = ctx[CpuRegister.Rsi];
        if (arrayAddress == 0 || valueAddress == 0)
        {
            return ctx.SetReturn(NpUniversalDataSystemErrorInvalidArgument, typeof(long));
        }

        Span<byte> arrayProbe = stackalloc byte[1];
        if (!ctx.Memory.TryRead(arrayAddress, arrayProbe) ||
            !KernelMemoryCompatExports.TryReadCString(
                ctx,
                valueAddress,
                4096,
                out _))
        {
            return ctx.SetReturn(
                (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT,
                typeof(long));
        }

        return ctx.SetReturn(0, typeof(long));
    }

    [SysAbiExport(
        Nid = "CzkKf7ahIyU",
        ExportName = "sceNpUniversalDataSystemPostEvent",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceNpUniversalDataSystem")]
    public static int NpUniversalDataSystemPostEvent(CpuContext ctx)
    {
        return ctx.SetReturn(0, typeof(long));
    }

    [SysAbiExport(
        Nid = "tpFJ8LIKvPw",
        ExportName = "sceNpUniversalDataSystemRegisterContext",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceNpUniversalDataSystem")]
    public static int NpUniversalDataSystemRegisterContext(CpuContext ctx)
    {
        return ctx.SetReturn(0, typeof(long));
    }

    [SysAbiExport(
        Nid = "AUIHb7jUX3I",
        ExportName = "sceNpUniversalDataSystemDestroyHandle",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceNpUniversalDataSystem")]
    public static int NpUniversalDataSystemDestroyHandle(CpuContext ctx)
    {
        return ctx.SetReturn(0, typeof(long));
    }

    private static bool TryAllocateOpaqueObject(
        CpuContext ctx,
        out ulong address)
        => KernelMemoryCompatExports.TryAllocateHleData(
            ctx,
            OpaqueObjectSize,
            OpaqueObjectSize,
            out address);
}
