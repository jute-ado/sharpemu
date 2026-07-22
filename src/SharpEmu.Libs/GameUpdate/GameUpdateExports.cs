// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using SharpEmu.HLE;

namespace SharpEmu.Libs.GameUpdate;

public static class GameUpdateExports
{
    private const int RequestLimit = 64;
    private const ulong CheckRecordSize = 48;
    private static readonly object _gate = new();
    private static readonly bool[] _activeRequests = new bool[RequestLimit];
    private static bool _initialized;

    internal static void ResetRuntimeState()
    {
        lock (_gate)
        {
            _initialized = false;
            Array.Clear(_activeRequests);
        }
    }

    [SysAbiExport(
        Nid = "YJtKLttI9fM",
        ExportName = "sceGameUpdateInitialize",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceGameUpdate")]
    public static int GameUpdateInitialize(CpuContext ctx)
    {
        lock (_gate)
        {
            _initialized = true;
        }

        return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_OK);
    }

    [SysAbiExport(
        Nid = "UvcvKaFvupA",
        ExportName = "sceGameUpdateCreateRequest",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceGameUpdate")]
    public static int GameUpdateCreateRequest(CpuContext ctx)
    {
        var parameterAddress = ctx[CpuRegister.Rdi];
        Span<byte> probe = stackalloc byte[1];
        if (parameterAddress == 0 ||
            !ctx.Memory.TryRead(parameterAddress, probe))
        {
            return ctx.SetReturn(
                OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        }

        lock (_gate)
        {
            if (!_initialized)
            {
                return ctx.SetReturn(
                    OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
            }

            for (var slot = 0; slot < _activeRequests.Length; slot++)
            {
                if (_activeRequests[slot])
                {
                    continue;
                }

                _activeRequests[slot] = true;
                return ctx.SetReturn(slot + 1);
            }
        }

        return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_BUSY);
    }

    [SysAbiExport(
        Nid = "LYVV9z8+owM",
        ExportName = "sceGameUpdateCheck",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceGameUpdate")]
    public static int GameUpdateCheck(CpuContext ctx)
    {
        var requestId = unchecked((int)ctx[CpuRegister.Rdi]);
        var parameterAddress = ctx[CpuRegister.Rsi];
        var resultAddress = ctx[CpuRegister.Rdx];
        Span<byte> parameterSize = stackalloc byte[sizeof(ulong)];
        Span<byte> resultSize = stackalloc byte[sizeof(ulong)];
        if (parameterAddress == 0 || resultAddress == 0 ||
            !ctx.Memory.TryRead(parameterAddress, parameterSize) ||
            !ctx.Memory.TryRead(resultAddress, resultSize) ||
            BinaryPrimitives.ReadUInt64LittleEndian(parameterSize) != CheckRecordSize ||
            BinaryPrimitives.ReadUInt64LittleEndian(resultSize) != CheckRecordSize)
        {
            return ctx.SetReturn(
                OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        }

        lock (_gate)
        {
            if (!_initialized || requestId <= 0 ||
                requestId > _activeRequests.Length ||
                !_activeRequests[requestId - 1])
            {
                return ctx.SetReturn(
                    OrbisGen2Result.ORBIS_GEN2_ERROR_DELETED);
            }
        }

        Span<byte> result = stackalloc byte[(int)CheckRecordSize];
        BinaryPrimitives.WriteUInt64LittleEndian(result, CheckRecordSize);
        return ctx.Memory.TryWrite(resultAddress, result)
            ? ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_OK)
            : ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
    }

    [SysAbiExport(
        Nid = "bcCyjHN5sn0",
        ExportName = "sceGameUpdateDeleteRequest",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceGameUpdate")]
    public static int GameUpdateDeleteRequest(CpuContext ctx)
    {
        var requestId = unchecked((int)ctx[CpuRegister.Rdi]);
        lock (_gate)
        {
            if (requestId <= 0 || requestId > _activeRequests.Length ||
                !_activeRequests[requestId - 1])
            {
                return ctx.SetReturn(
                    OrbisGen2Result.ORBIS_GEN2_ERROR_DELETED);
            }

            _activeRequests[requestId - 1] = false;
        }

        return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_OK);
    }

    [SysAbiExport(
        Nid = "NSH-C-OmoNI",
        ExportName = "sceGameUpdateTerminate",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceGameUpdate")]
    public static int GameUpdateTerminate(CpuContext ctx)
    {
        ResetRuntimeState();
        return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_OK);
    }
}
