// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;

namespace SharpEmu.Libs.LibcMath;

public static class LibcMathExports
{
    [SysAbiExport(
        Nid = "-P6FNMzk2Kc",
        ExportName = "cosf",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libc")]
    public static int Cosf(CpuContext ctx)
    {
        // SysV passes and returns scalar floats in XMM0. The remaining lanes are
        // caller state and must not be replaced with integer-register contents.
        ctx.GetXmmRegister(0, out var low, out var high);
        var input = BitConverter.UInt32BitsToSingle(unchecked((uint)low));
        var result = BitConverter.SingleToUInt32Bits(MathF.Cos(input));
        ctx.SetXmmRegister(0, (low & 0xFFFFFFFF00000000UL) | result, high);
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }
}
