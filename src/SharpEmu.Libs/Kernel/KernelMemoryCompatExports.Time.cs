// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;

namespace SharpEmu.Libs.Kernel;

public static partial class KernelMemoryCompatExports
{
    [SysAbiExport(
        Nid = "wLlFkwG9UcQ",
        ExportName = "time",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libc")]
    public static int Time(CpuContext ctx)
    {
        var seconds = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var destination = ctx[CpuRegister.Rdi];

        if (destination != 0 &&
            !TryWriteUInt64Compat(ctx, destination, unchecked((ulong)seconds)))
        {
            ctx[CpuRegister.Rax] = ulong.MaxValue;
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT;
        }

        ctx[CpuRegister.Rax] = unchecked((ulong)seconds);
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }
}
