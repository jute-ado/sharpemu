// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;

namespace SharpEmu.Libs.Kernel;

public static partial class KernelMemoryCompatExports
{
    [SysAbiExport(
        Nid = "WDpobjImAb4",
        ExportName = "wcsstr",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libc")]
    public static int Wcsstr(CpuContext ctx)
    {
        var haystackAddress = ctx[CpuRegister.Rdi];
        var needleAddress = ctx[CpuRegister.Rsi];

        if (!TryReadWideCString(ctx, haystackAddress, 1_048_576, out var haystack) ||
            !TryReadWideCString(ctx, needleAddress, 1_048_576, out var needle))
        {
            ctx[CpuRegister.Rax] = 0;
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT;
        }

        var index = haystack.AsSpan().IndexOf(needle);
        ctx[CpuRegister.Rax] = index < 0
            ? 0
            : haystackAddress + (unchecked((ulong)index) * WideCharSize);
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }
}
