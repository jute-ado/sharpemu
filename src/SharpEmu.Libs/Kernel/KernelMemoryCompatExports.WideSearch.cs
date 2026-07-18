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

        if (haystackAddress == 0 ||
            !TryReadWideCString(ctx, needleAddress, 1_048_576, out var needle))
        {
            ctx[CpuRegister.Rax] = 0;
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT;
        }

        if (needle.Length == 0)
        {
            ctx[CpuRegister.Rax] = haystackAddress;
            return (int)OrbisGen2Result.ORBIS_GEN2_OK;
        }

        if (!TryFindWideCString(
                ctx,
                haystackAddress,
                needle,
                1_048_576,
                out var matchAddress))
        {
            ctx[CpuRegister.Rax] = 0;
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT;
        }

        ctx[CpuRegister.Rax] = matchAddress;
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }
}
