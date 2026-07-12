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
    private const uint VSubF32_V0_V1_V2 = 0x08000501; // v_sub_f32_e32 v0, v1, v2
    private const uint VReadlaneB32_S0_V1_S2 = 0xD7600000;
    private const uint VReadlaneB32_S0_V1_S2_Extra = 0x00000501;
    private const uint VWritelaneB32_V0_S1_S2 = 0xD7610000;
    private const uint VWritelaneB32_V0_S1_S2_Extra = 0x00000401;
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

    [Fact]
    public void InvalidatesCachedProgramWhenShaderMemoryChanges()
    {
        var ctx = CreateContext([VAddF32_V0_V1_V2, SEndpgm]);
        var registers = new Dictionary<uint, uint>();

        Assert.True(Gen5ShaderTranslator.TryCreateState(
            ctx,
            CodeAddress,
            shaderHeaderAddress: 0,
            registers,
            userDataBaseRegister: 0,
            out var firstState,
            out var firstError), firstError);
        Assert.Equal("VAddF32", firstState.Program.Instructions[0].Opcode);

        Span<byte> replacement = stackalloc byte[sizeof(uint)];
        BitConverter.TryWriteBytes(replacement, VSubF32_V0_V1_V2);
        Assert.True(ctx.Memory.TryWrite(CodeAddress, replacement));

        Assert.True(Gen5ShaderTranslator.TryCreateState(
            ctx,
            CodeAddress,
            shaderHeaderAddress: 0,
            registers,
            userDataBaseRegister: 0,
            out var secondState,
            out var secondError), secondError);
        Assert.Equal("VSubF32", secondState.Program.Instructions[0].Opcode);
        Assert.NotSame(firstState.Program, secondState.Program);
    }

    [Fact]
    public void ReusesCachedProgramWhileShaderMemoryIsUnchanged()
    {
        var ctx = CreateContext([VAddF32_V0_V1_V2, SEndpgm]);
        var registers = new Dictionary<uint, uint>();

        Assert.True(Gen5ShaderTranslator.TryCreateState(
            ctx,
            CodeAddress,
            shaderHeaderAddress: 0,
            registers,
            userDataBaseRegister: 0,
            out var firstState,
            out var firstError), firstError);
        Assert.True(Gen5ShaderTranslator.TryCreateState(
            ctx,
            CodeAddress,
            shaderHeaderAddress: 0,
            registers,
            userDataBaseRegister: 0,
            out var secondState,
            out var secondError), secondError);

        Assert.Same(firstState.Program, secondState.Program);
    }

    [Fact]
    public void DecodesLaneOperationRegisterClasses()
    {
        var ctx = CreateContext(
        [
            VWritelaneB32_V0_S1_S2,
            VWritelaneB32_V0_S1_S2_Extra,
            VReadlaneB32_S0_V1_S2,
            VReadlaneB32_S0_V1_S2_Extra,
            SEndpgm,
        ]);

        Assert.True(
            Gen5ShaderTranslator.TryDecodeProgram(
                ctx,
                CodeAddress,
                out var program,
                out var error),
            error);

        var write = program.Instructions[0];
        Assert.Equal("VWritelaneB32", write.Opcode);
        Assert.Collection(
            write.Sources,
            source => Assert.Equal(Gen5Operand.Scalar(1), source),
            source => Assert.Equal(Gen5Operand.Scalar(2), source));
        Assert.Equal(Gen5Operand.Vector(0), Assert.Single(write.Destinations));

        var read = program.Instructions[1];
        Assert.Equal("VReadlaneB32", read.Opcode);
        Assert.Collection(
            read.Sources,
            source => Assert.Equal(Gen5Operand.Vector(1), source),
            source => Assert.Equal(Gen5Operand.Scalar(2), source));
        Assert.Equal(Gen5Operand.Scalar(0), Assert.Single(read.Destinations));
    }

    [Fact]
    public void CompilesLaneOperationsToSubgroupSpirv()
    {
        var ctx = CreateContext(
        [
            VWritelaneB32_V0_S1_S2,
            VWritelaneB32_V0_S1_S2_Extra,
            VReadlaneB32_S0_V1_S2,
            VReadlaneB32_S0_V1_S2_Extra,
            SEndpgm,
        ]);
        Assert.True(
            Gen5ShaderTranslator.TryDecodeProgram(
                ctx,
                CodeAddress,
                out var program,
                out var decodeError),
            decodeError);
        var state = new Gen5ShaderState(
            program,
            [0, 0x1234_5678, 7],
            Metadata: null);
        Assert.True(
            Gen5ShaderScalarEvaluator.TryEvaluate(
                ctx,
                state,
                out var evaluation,
                out var evaluationError),
            evaluationError);

        Assert.True(
            Gen5SpirvTranslator.TryCompileComputeShader(
                state,
                evaluation,
                localSizeX: 32,
                localSizeY: 1,
                localSizeZ: 1,
            out var shader,
            out var compileError),
            compileError);
        Assert.True(ContainsSpirvOpcode(shader.Spirv, 345));
    }

    private static bool ContainsSpirvOpcode(byte[] spirv, ushort opcode)
    {
        for (var offset = 5 * sizeof(uint); offset < spirv.Length;)
        {
            var instruction = BitConverter.ToUInt32(spirv, offset);
            if ((ushort)instruction == opcode)
            {
                return true;
            }

            var wordCount = instruction >> 16;
            if (wordCount == 0)
            {
                return false;
            }

            offset += checked((int)wordCount * sizeof(uint));
        }

        return false;
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
