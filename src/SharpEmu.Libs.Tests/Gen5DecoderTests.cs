// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using SharpEmu.Libs.Agc;
using Xunit;

namespace SharpEmu.Libs.Tests;

public sealed class Gen5DecoderTests
{
    private const ulong CodeAddress = 0x0000_1000_0000UL;

    // Encodings assembled with llvm-mc -triple=amdgcn-amd-amdhsa -mcpu=gfx1013
    // (LLVM 22.1.8) and verified against llvm-objdump. No console-derived data.
    private const uint VAddF32_V0_V1_V2 = 0x06000501; // v_add_f32_e32 v0, v1, v2
    private const uint SEndpgm = 0xBF810000;          // s_endpgm

    [Fact]
    public void DecodesVAddF32FollowedBySEndpgm()
    {
        var ctx = CreateContext([VAddF32_V0_V1_V2, SEndpgm]);

        var ok = Gen5ShaderTranslator.TryDecodeProgram(ctx, CodeAddress, out var program, out var error);

        Assert.True(ok, error);
        Assert.Equal(2, program.Instructions.Count);
        Assert.Equal("VAddF32", program.Instructions[0].Opcode);
        Assert.Equal("SEndpgm", program.Instructions[1].Opcode);
    }

    [Fact]
    public void FailsCleanlyOnUnmappedAddress()
    {
        var ctx = CreateContext([SEndpgm]);

        var ok = Gen5ShaderTranslator.TryDecodeProgram(ctx, CodeAddress + 0x1000, out _, out var error);

        Assert.False(ok);
        Assert.NotEmpty(error);
    }

    private static CpuContext CreateContext(uint[] instructionWords)
    {
        var bytes = new byte[instructionWords.Length * sizeof(uint)];
        for (var i = 0; i < instructionWords.Length; i++)
        {
            BitConverter.TryWriteBytes(bytes.AsSpan(i * sizeof(uint)), instructionWords[i]);
        }

        var memory = new FakeGuestMemory();
        memory.AddRegion(CodeAddress, bytes);
        return new CpuContext(memory, Generation.Gen5);
    }
}
