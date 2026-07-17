// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Security.Cryptography;
using SharpEmu.HLE;

namespace SharpEmu.Libs.Random;

public static class RandomExports
{
    private const ulong MaximumRandomBytes = 64;
    private const int RandomErrorInvalid = unchecked((int)0x817C0016);

    [SysAbiExport(
        Nid = "PI7jIZj4pcE",
        ExportName = "sceRandomGetRandomNumber",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceRandom")]
    public static int RandomGetRandomNumber(CpuContext ctx)
    {
        var outputAddress = ctx[CpuRegister.Rdi];
        var size = ctx[CpuRegister.Rsi];
        if (size > MaximumRandomBytes || (outputAddress == 0 && size != 0))
        {
            return SetResult(ctx, RandomErrorInvalid);
        }

        if (size == 0)
        {
            return SetResult(ctx, (int)OrbisGen2Result.ORBIS_GEN2_OK);
        }

        Span<byte> randomBytes = stackalloc byte[checked((int)size)];
        RandomNumberGenerator.Fill(randomBytes);
        if (!ctx.Memory.TryWrite(outputAddress, randomBytes))
        {
            return SetResult(ctx, (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }

        return SetResult(ctx, (int)OrbisGen2Result.ORBIS_GEN2_OK);
    }

    private static int SetResult(CpuContext ctx, int result)
    {
        ctx[CpuRegister.Rax] = unchecked((ulong)result);
        return result;
    }
}
