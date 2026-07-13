// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;

namespace SharpEmu.Libs.Kernel;

public static partial class KernelMemoryCompatExports
{
    [SysAbiExport(
        Nid = "yDBwVAolDgg",
        ExportName = "sceKernelIsStack",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int KernelIsStack(CpuContext ctx)
    {
        var address = ctx[CpuRegister.Rdi];
        var startOut = ctx[CpuRegister.Rsi];
        var endOut = ctx[CpuRegister.Rdx];
        Span<byte> probe = stackalloc byte[1];

        if (address == 0 || !TryReadCompat(ctx, address, probe))
        {
            return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        }

        ulong stackStart = 0;
        ulong stackEnd = 0;
        if (ctx.Memory is IGuestStackMemory stackMemory)
        {
            _ = stackMemory.TryGetStackRange(address, out stackStart, out stackEnd);
        }

        if ((startOut != 0 && !TryWriteUInt64Compat(ctx, startOut, stackStart)) ||
            (endOut != 0 && !TryWriteUInt64Compat(ctx, endOut, stackEnd)))
        {
            return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }

        return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_OK);
    }
}
