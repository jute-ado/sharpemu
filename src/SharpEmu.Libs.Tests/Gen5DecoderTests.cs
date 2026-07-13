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
    private const uint VPkMadI16_V1_V2_V3_V4 = 0xCC004001;
    private const uint VPkMadI16_V1_V2_V3_V4_Extra = 0x1C120702;
    private const uint VPkMulF16_V21_V22_V23_Modified = 0xCC104815;
    private const uint VPkMulF16_V21_V22_V23_Modified_Extra = 0x10022F16;
    private const uint VPkMinF16_V24_V25_V26_Modified = 0xCC11C218;
    private const uint VPkMinF16_V24_V25_V26_Modified_Extra = 0x38023519;
    private const uint VPkAddF16_V30_Literal_V31 = 0xCC0F401E;
    private const uint VPkAddF16_V30_Literal_V31_Extra = 0x18023EFF;
    private const uint Half2OneLiteral = 0x3C003C00;
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

    [Fact]
    public void DecodesVop3pOperandsSelectorsAndModifiers()
    {
        var ctx = CreateContext(
        [
            VPkMadI16_V1_V2_V3_V4,
            VPkMadI16_V1_V2_V3_V4_Extra,
            VPkMulF16_V21_V22_V23_Modified,
            VPkMulF16_V21_V22_V23_Modified_Extra,
            VPkMinF16_V24_V25_V26_Modified,
            VPkMinF16_V24_V25_V26_Modified_Extra,
            SEndpgm,
        ]);

        Assert.True(
            Gen5ShaderTranslator.TryDecodeProgram(
                ctx,
                CodeAddress,
                out var program,
                out var error),
            error);

        var mad = program.Instructions[0];
        Assert.Equal(Gen5ShaderEncoding.Vop3p, mad.Encoding);
        Assert.Equal("VPkMadI16", mad.Opcode);
        Assert.Collection(
            mad.Sources,
            source => Assert.Equal(Gen5Operand.Vector(2), source),
            source => Assert.Equal(Gen5Operand.Vector(3), source),
            source => Assert.Equal(Gen5Operand.Vector(4), source));
        Assert.Equal(Gen5Operand.Vector(1), Assert.Single(mad.Destinations));
        var madControl = Assert.IsType<Gen5Vop3pControl>(mad.Control);
        Assert.Equal(0u, madControl.OpSelectMask);
        Assert.Equal(7u, madControl.OpSelectHighMask);

        var multiplyControl = Assert.IsType<Gen5Vop3pControl>(program.Instructions[1].Control);
        Assert.Equal(1u, multiplyControl.OpSelectMask);
        Assert.Equal(6u, multiplyControl.OpSelectHighMask);
        Assert.Equal(0u, multiplyControl.NegateLowMask);
        Assert.Equal(0u, multiplyControl.NegateHighMask);
        Assert.False(multiplyControl.Clamp);

        var minControl = Assert.IsType<Gen5Vop3pControl>(program.Instructions[2].Control);
        Assert.Equal(1u, minControl.NegateLowMask);
        Assert.Equal(2u, minControl.NegateHighMask);
        Assert.True(minControl.Clamp);
    }

    [Fact]
    public void DecodesVop3pLiteralAsThirdDword()
    {
        var ctx = CreateContext(
        [
            VPkAddF16_V30_Literal_V31,
            VPkAddF16_V30_Literal_V31_Extra,
            Half2OneLiteral,
            SEndpgm,
        ]);

        Assert.True(
            Gen5ShaderTranslator.TryDecodeProgram(
                ctx,
                CodeAddress,
                out var program,
                out var error),
            error);

        var instruction = program.Instructions[0];
        Assert.Equal("VPkAddF16", instruction.Opcode);
        Assert.Equal(3, instruction.Words.Count);
        Assert.Equal(
            new Gen5Operand(Gen5OperandKind.LiteralConstant, Half2OneLiteral),
            instruction.Sources[0]);
        Assert.Equal(Gen5Operand.Vector(31), instruction.Sources[1]);
        Assert.Equal(Gen5Operand.Vector(30), Assert.Single(instruction.Destinations));
        Assert.Equal(12u, program.Instructions[1].Pc);
    }

    [Fact]
    public void CompilesPackedArithmeticMixAndDotProductsToSpirv()
    {
        // Additional encodings assembled with LLVM 18 llvm-mc for gfx1030 and
        // independently disassembled with llvm-objdump.
        var ctx = CreateContext(
        [
            VPkMadI16_V1_V2_V3_V4,
            VPkMadI16_V1_V2_V3_V4_Extra,
            0xCC0E400Eu, 0x1C46210Fu, // v_pk_fma_f16 v14, v15, v16, v17
            VPkMulF16_V21_V22_V23_Modified,
            VPkMulF16_V21_V22_V23_Modified_Extra,
            VPkMinF16_V24_V25_V26_Modified,
            VPkMinF16_V24_V25_V26_Modified_Extra,
            VPkAddF16_V30_Literal_V31,
            VPkAddF16_V30_Literal_V31_Extra,
            Half2OneLiteral,
            0xCC134001u, 0x1C120702u, // v_dot2_f32_f16 v1, v2, v3, v4
            0xCC144005u, 0x1C220F06u, // v_dot2_i32_i16 v5, v6, v7, v8
            0xCC15C009u, 0x1C32170Au, // v_dot2_u32_u16 v9, v10, v11, v12 clamp
            0xCC16400Du, 0x1C421F0Eu, // v_dot4_i32_i8 v13, v14, v15, v16
            0xCC174011u, 0x1C522712u, // v_dot4_u32_u8 v17, v18, v19, v20
            0xCC184015u, 0x1C622F16u, // v_dot8_i32_i4 v21, v22, v23, v24
            0xCC19C019u, 0x1C72371Au, // v_dot8_u32_u4 v25, v26, v27, v28 clamp
            0xCC20001Du, 0x04823F1Eu, // v_fma_mix_f32 v29, v30, v31, v32
            0xCC210021u, 0x04924722u, // v_fma_mixlo_f16 v33, v34, v35, v36
            0xCC220025u, 0x04A24F26u, // v_fma_mixhi_f16 v37, v38, v39, v40
            0xCC0F4000u, 0x180202F2u, // v_pk_add_f16 v0, 1.0, v1
            0xCC024002u, 0x18020681u, // v_pk_add_i16 v2, 1, v3
            0xCC134004u, 0x1BD20AF2u, // v_dot2_f32_f16 v4, 1.0, v5, 2.0
            0xCC144006u, 0x1A0A0E81u, // v_dot2_i32_i16 v6, 1, v7, 2
            0xCC200008u, 0x03D212F2u, // v_fma_mix_f32 v8, 1.0, v9, 2.0
            SEndpgm,
        ]);
        Assert.True(
            Gen5ShaderTranslator.TryDecodeProgram(
                ctx,
                CodeAddress,
                out var program,
                out var decodeError),
            decodeError);
        var state = new Gen5ShaderState(program, [], Metadata: null);
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
        Assert.True(ContainsSpirvOpcode(shader.Spirv, (ushort)SpirvOp.ExtInst));
        Assert.True(ContainsSpirvOpcode(shader.Spirv, (ushort)SpirvOp.IMul));
        Assert.True(ContainsSpirvOpcode(shader.Spirv, (ushort)SpirvOp.BitFieldSExtract));
        Assert.True(ContainsSpirvOpcode(shader.Spirv, (ushort)SpirvOp.BitFieldUExtract));
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
