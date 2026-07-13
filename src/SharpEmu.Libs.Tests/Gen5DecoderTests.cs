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
    private const uint VReadfirstlaneB32_S4_V6 = 0x7E080506;
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
            VReadfirstlaneB32_S4_V6,
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

        var readFirst = program.Instructions[2];
        Assert.Equal("VReadfirstlaneB32", readFirst.Opcode);
        Assert.Equal(Gen5Operand.Vector(6), Assert.Single(readFirst.Sources));
        Assert.Equal(Gen5Operand.Scalar(4), Assert.Single(readFirst.Destinations));
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
            VReadfirstlaneB32_S4_V6,
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
        Assert.True(ContainsSpirvCapability(
            shader.Spirv,
            SpirvCapability.GroupNonUniformBallot));
        Assert.True(ContainsSpirvOpcode(
            shader.Spirv,
            (ushort)SpirvOp.GroupNonUniformBallot));
        Assert.True(ContainsSpirvOpcode(
            shader.Spirv,
            (ushort)SpirvOp.GroupNonUniformBallotFindLSB));
        Assert.True(ContainsSpirvOpcode(
            shader.Spirv,
            (ushort)SpirvOp.GroupNonUniformShuffle));
    }

    [Fact]
    public void CompilesVectorClearExceptionsAsNoOp()
    {
        // Encoding assembled with LLVM 18 llvm-mc for gfx1030 and verified
        // with llvm-objdump.
        var ctx = CreateContext(
        [
            0x7E008200u, // v_clrexcp
            SEndpgm,
        ]);
        Assert.True(
            Gen5ShaderTranslator.TryDecodeProgram(
                ctx,
                CodeAddress,
                out var program,
                out var decodeError),
            decodeError);
        Assert.Equal(["VClrexcp", "SEndpgm"],
            program.Instructions.Select(instruction => instruction.Opcode));
        Assert.Empty(program.Instructions[0].Sources);
        Assert.Empty(program.Instructions[0].Destinations);

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
                out _,
                out var compileError),
            compileError);
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

    [Fact]
    public void CompilesCommonVectorIntegerOperationsToSpirv()
    {
        // Encodings assembled with LLVM 18 llvm-mc for gfx1030 and verified
        // with llvm-objdump.
        var ctx = CreateContext(
        [
            0xD7640000u, 0x00020501u, // v_bcnt_u32_b32 v0, v1, v2
            0x7E067304u,              // v_ffbh_u32_e32 v3, v4
            0x7E0A7706u,              // v_ffbh_i32_e32 v5, v6
            0xD76A0007u, 0x00021308u, // v_cvt_pk_u16_u32 v7, v8, v9
            0xD76B000Au, 0x0002190Bu, // v_cvt_pk_i16_i32 v10, v11, v12
            0x121A1F0Eu,              // v_mul_i32_i24_e32 v13, v14, v15
            0x14202511u,              // v_mul_hi_i32_i24_e32 v16, v17, v18
            0xD75E0000u, 0x040E0501u, // v_mad_i16 v0, v1, v2, v3
            0xD7400004u, 0x041E0D05u, // v_mad_u16 v4, v5, v6, v7
            0xD5420008u, 0x042E1509u, // v_mad_i32_i24 v8, v9, v10, v11
            0xD56C0016u, 0x00023117u, // v_mul_hi_i32 v22, v23, v24
            0xD5490019u, 0x0472371Au, // v_bfe_i32 v25, v26, v27, v28
            0x3C3A3F1Eu,              // v_xnor_b32_e32 v29, v30, v31
            0xD7650020u, 0x00024521u, // v_mbcnt_lo_u32_b32 v32, v33, v34
            0xD7660023u, 0x00024B24u, // v_mbcnt_hi_u32_b32 v35, v36, v37
            0xD54E0000u, 0x040E0501u, // v_alignbit_b32 v0, v1, v2, v3
            0xD54F0004u, 0x041E0D05u, // v_alignbyte_b32 v4, v5, v6, v7
            0xD54D0008u, 0x042E1509u, // v_lerp_u8 v8, v9, v10, v11
            0xD571000Cu, 0x043E1D0Du, // v_msad_u8 v12, v13, v14, v15
            0xD55B0010u, 0x044E2511u, // v_sad_hi_u8 v16, v17, v18, v19
            0xD55C0014u, 0x045E2D15u, // v_sad_u16 v20, v21, v22, v23
            0xD55D0018u, 0x046E3519u, // v_sad_u32 v24, v25, v26, v27
            0xD55A001Cu, 0x047E3D1Du, // v_sad_u8 v28, v29, v30, v31
            0xD7450020u, 0x048E4521u, // v_xad_u32 v32, v33, v34, v35
            SEndpgm,
        ]);
        Assert.True(
            Gen5ShaderTranslator.TryDecodeProgram(
                ctx,
                CodeAddress,
                out var program,
                out var decodeError),
            decodeError);
        Assert.Equal(
            [
                "VBcntU32B32",
                "VFfbhU32",
                "VFfbhI32",
                "VCvtPkU16U32",
                "VCvtPkI16I32",
                "VMulI32I24",
                "VMulHiI32I24",
                "VMadI16",
                "VMadU16",
                "VMadI32I24",
                "VMulHiI32",
                "VBfeI32",
                "VXnorB32",
                "VMbcntLoU32B32",
                "VMbcntHiU32B32",
                "VAlignbitB32",
                "VAlignbyteB32",
                "VLerpU8",
                "VMsadU8",
                "VSadHiU8",
                "VSadU16",
                "VSadU32",
                "VSadU8",
                "VXadU32",
                "SEndpgm",
            ],
            program.Instructions.Select(instruction => instruction.Opcode));
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
        Assert.True(ContainsSpirvOpcode(shader.Spirv, (ushort)SpirvOp.BitCount));
        Assert.True(ContainsSpirvOpcode(shader.Spirv, (ushort)SpirvOp.SConvert));
        Assert.True(ContainsSpirvOpcode(shader.Spirv, (ushort)SpirvOp.BitFieldSExtract));
        Assert.True(ContainsSpirvOpcode(shader.Spirv, (ushort)SpirvOp.ExtInst));
        Assert.True(ContainsSpirvOpcode(shader.Spirv, (ushort)SpirvOp.Not));
    }

    [Fact]
    public void CompilesVectorFloatUtilityOperationsToSpirv()
    {
        // Encodings assembled with LLVM 18 llvm-mc for gfx1030 and verified
        // with llvm-objdump.
        var ctx = CreateContext(
        [
            0xD7680000u, 0x00020501u, // v_cvt_pknorm_i16_f32 v0, v1, v2
            0xD7690003u, 0x00020B04u, // v_cvt_pknorm_u16_f32 v3, v4, v5
            0x7E0C7F07u,              // v_frexp_exp_i32_f32_e32 v6, v7
            0x7E108109u,              // v_frexp_mant_f32_e32 v8, v9
            0x0E14190Bu,              // v_mul_legacy_f32_e32 v10, v11, v12
            0x7C141D0Du,              // v_cmp_nlg_f32_e32 vcc_lo, v13, v14
            0x7C0E210Fu,              // v_cmp_o_f32_e32 vcc_lo, v15, v16
            0x7C102511u,              // v_cmp_u_f32_e32 vcc_lo, v17, v18
            0x7D202913u,              // v_cmpx_f_i32_e32 v19, v20
            0x7D2E2D15u,              // v_cmpx_t_i32_e32 v21, v22
            0x7DA03117u,              // v_cmpx_f_u32_e32 v23, v24
            0x7DAE3519u,              // v_cmpx_t_u32_e32 v25, v26
            SEndpgm,
        ]);
        Assert.True(
            Gen5ShaderTranslator.TryDecodeProgram(
                ctx,
                CodeAddress,
                out var program,
                out var decodeError),
            decodeError);
        Assert.Equal(
            [
                "VCvtPknormI16F32",
                "VCvtPknormU16F32",
                "VFrexpExpI32F32",
                "VFrexpMantF32",
                "VMulLegacyF32",
                "VCmpNlgF32",
                "VCmpOF32",
                "VCmpUF32",
                "VCmpxFI32",
                "VCmpxTI32",
                "VCmpxFU32",
                "VCmpxTU32",
                "SEndpgm",
            ],
            program.Instructions.Select(instruction => instruction.Opcode));
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
        Assert.False(ContainsSpirvCapability(shader.Spirv, SpirvCapability.Float64));
        Assert.True(ContainsSpirvOpcode(shader.Spirv, (ushort)SpirvOp.ExtInst));
        Assert.True(ContainsGlslExtInst(shader.Spirv, 52));
        Assert.True(ContainsGlslExtInst(shader.Spirv, 56));
        Assert.True(ContainsGlslExtInst(shader.Spirv, 57));
        Assert.True(ContainsSpirvOpcode(shader.Spirv, (ushort)SpirvOp.CompositeExtract));
        Assert.True(ContainsSpirvOpcode(shader.Spirv, (ushort)SpirvOp.FUnordEqual));
        Assert.True(ContainsSpirvOpcode(shader.Spirv, (ushort)SpirvOp.IsNan));
        Assert.True(ContainsSpirvOpcode(shader.Spirv, (ushort)SpirvOp.Select));
    }

    [Fact]
    public void CompilesVectorBytePermuteAndDotAccumulateToSpirv()
    {
        // Encodings assembled with LLVM 18 llvm-mc for gfx1030 and verified
        // with llvm-objdump.
        var ctx = CreateContext(
        [
            0xD7440000u, 0x040E0501u, // v_perm_b32 v0, v1, v2, v3
            0x04080D05u,              // v_dot2c_f32_f16 v4, v5, v6
            SEndpgm,
        ]);
        Assert.True(
            Gen5ShaderTranslator.TryDecodeProgram(
                ctx,
                CodeAddress,
                out var program,
                out var decodeError),
            decodeError);
        Assert.Equal(
            ["VPermB32", "VDot2cF32F16", "SEndpgm"],
            program.Instructions.Select(instruction => instruction.Opcode));
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
        Assert.True(ContainsGlslExtInst(shader.Spirv, 50));
        Assert.True(ContainsGlslExtInst(shader.Spirv, 62));
        Assert.True(ContainsSpirvOpcode(shader.Spirv, (ushort)SpirvOp.CompositeExtract));
        Assert.True(ContainsSpirvOpcode(shader.Spirv, (ushort)SpirvOp.ShiftRightLogical));
        Assert.True(ContainsSpirvOpcode(shader.Spirv, (ushort)SpirvOp.UGreaterThanEqual));
        Assert.True(ContainsSpirvOpcode(shader.Spirv, (ushort)SpirvOp.Select));
    }

    [Fact]
    public void CompilesVectorFloat64ArithmeticToSpirv()
    {
        // Encodings assembled with LLVM 18 llvm-mc for gfx1030 and verified
        // with llvm-objdump.
        var ctx = CreateContext(
        [
            0xD5640000u, 0x00020902u, // v_add_f64 v[0:1], v[2:3], v[4:5]
            0xD5650006u, 0x00021508u, // v_mul_f64 v[6:7], v[8:9], v[10:11]
            0xD54C000Cu, 0x044A210Eu, // v_fma_f64 v[12:13], v[14:15], v[16:17], v[18:19]
            0xD5660014u, 0x00023116u, // v_min_f64 v[20:21], v[22:23], v[24:25]
            0xD567001Au, 0x00023D1Cu, // v_max_f64 v[26:27], v[28:29], v[30:31]
            0xD5640020u, 0x000244F2u, // v_add_f64 v[32:33], 1.0, v[34:35]
            0xD5650024u, 0x00024CF5u, // v_mul_f64 v[36:37], -2.0, v[38:39]
            0xD54C0228u, 0x23C2592Au, // v_fma_f64 v[40:41], -v[42:43], |v[44:45]|, 0.5
            SEndpgm,
        ]);
        Assert.True(
            Gen5ShaderTranslator.TryDecodeProgram(
                ctx,
                CodeAddress,
                out var program,
                out var decodeError),
            decodeError);
        Assert.Equal(
            [
                "VAddF64",
                "VMulF64",
                "VFmaF64",
                "VMinF64",
                "VMaxF64",
                "VAddF64",
                "VMulF64",
                "VFmaF64",
                "SEndpgm",
            ],
            program.Instructions.Select(instruction => instruction.Opcode));
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
        Assert.True(ContainsSpirvCapability(shader.Spirv, SpirvCapability.Float64));
        Assert.True(ContainsSpirvOpcode(shader.Spirv, (ushort)SpirvOp.FAdd));
        Assert.True(ContainsSpirvOpcode(shader.Spirv, (ushort)SpirvOp.FMul));
        Assert.True(ContainsSpirvOpcode(shader.Spirv, (ushort)SpirvOp.FNegate));
        Assert.True(ContainsGlslExtInst(shader.Spirv, 4));
        Assert.True(ContainsGlslExtInst(shader.Spirv, 37));
        Assert.True(ContainsGlslExtInst(shader.Spirv, 40));
        Assert.True(ContainsGlslExtInst(shader.Spirv, 50));
    }

    [Fact]
    public void CompilesVectorFloat64ConversionsAndUnaryMathToSpirv()
    {
        // Encodings assembled with LLVM 18 llvm-mc for gfx1030 and verified
        // with llvm-objdump.
        var ctx = CreateContext(
        [
            0x7E000732u, // v_cvt_i32_f64 v0, v[50:51]
            0x7E040934u, // v_cvt_f64_i32 v[2:3], v52
            0x7E081F32u, // v_cvt_f32_f64 v4, v[50:51]
            0x7E0C2134u, // v_cvt_f64_f32 v[6:7], v52
            0x7E102B32u, // v_cvt_u32_f64 v8, v[50:51]
            0x7E142D34u, // v_cvt_f64_u32 v[10:11], v52
            0x7E182F32u, // v_trunc_f64 v[12:13], v[50:51]
            0x7E1C3132u, // v_ceil_f64 v[14:15], v[50:51]
            0x7E203332u, // v_rndne_f64 v[16:17], v[50:51]
            0x7E243532u, // v_floor_f64 v[18:19], v[50:51]
            0x7E287D32u, // v_fract_f64 v[20:21], v[50:51]
            0x7E2C7932u, // v_frexp_exp_i32_f64 v22, v[50:51]
            0x7E307B32u, // v_frexp_mant_f64 v[24:25], v[50:51]
            0x7E345F32u, // v_rcp_f64 v[26:27], v[50:51]
            0x7E386332u, // v_rsq_f64 v[28:29], v[50:51]
            0x7E3C6932u, // v_sqrt_f64 v[30:31], v[50:51]
            SEndpgm,
        ]);
        Assert.True(
            Gen5ShaderTranslator.TryDecodeProgram(
                ctx,
                CodeAddress,
                out var program,
                out var decodeError),
            decodeError);
        Assert.Equal(
            [
                "VCvtI32F64", "VCvtF64I32", "VCvtF32F64", "VCvtF64F32",
                "VCvtU32F64", "VCvtF64U32", "VTruncF64", "VCeilF64",
                "VRndneF64", "VFloorF64", "VFractF64", "VFrexpExpI32F64",
                "VFrexpMantF64", "VRcpF64", "VRsqF64", "VSqrtF64", "SEndpgm",
            ],
            program.Instructions.Select(instruction => instruction.Opcode));
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
        Assert.True(ContainsSpirvCapability(shader.Spirv, SpirvCapability.Float64));
        Assert.True(ContainsSpirvOpcode(shader.Spirv, (ushort)SpirvOp.ConvertFToS));
        Assert.True(ContainsSpirvOpcode(shader.Spirv, (ushort)SpirvOp.ConvertFToU));
        Assert.True(ContainsSpirvOpcode(shader.Spirv, (ushort)SpirvOp.ConvertSToF));
        Assert.True(ContainsSpirvOpcode(shader.Spirv, (ushort)SpirvOp.ConvertUToF));
        Assert.True(ContainsSpirvOpcode(shader.Spirv, (ushort)SpirvOp.FConvert));
        Assert.True(ContainsSpirvOpcode(shader.Spirv, (ushort)SpirvOp.FDiv));
        Assert.True(ContainsGlslExtInst(shader.Spirv, 2));
        Assert.True(ContainsGlslExtInst(shader.Spirv, 3));
        Assert.True(ContainsGlslExtInst(shader.Spirv, 8));
        Assert.True(ContainsGlslExtInst(shader.Spirv, 9));
        Assert.True(ContainsGlslExtInst(shader.Spirv, 10));
        Assert.True(ContainsGlslExtInst(shader.Spirv, 31));
        Assert.True(ContainsGlslExtInst(shader.Spirv, 32));
        Assert.True(ContainsGlslExtInst(shader.Spirv, 52));
    }

    [Fact]
    public void CompilesExtendedVop1EncodingsToSpirv()
    {
        // E64 encodings assembled with LLVM 18 llvm-mc for gfx1030 and
        // verified with llvm-objdump.
        var ctx = CreateContext(
        [
            0xD5830100u, 0x20000102u, // v_cvt_i32_f64 v0, -|v[2:3]|
            0xD5840004u, 0x00000106u, // v_cvt_f64_i32 v[4:5], v6
            0xD5970108u, 0x2000010Au, // v_trunc_f64 v[8:9], -|v[10:11]|
            0xD5AF000Cu, 0x2000010Eu, // v_rcp_f64 v[12:13], -v[14:15]
            0xD5A40110u, 0x20000111u, // v_floor_f32 v16, -|v17|
            0xD5B80012u, 0x00000113u, // v_bfrev_b32 v18, v19
            0xD5BC0114u, 0x00000116u, // v_frexp_exp_i32_f64 v20, |v[22:23]|
            0xD5BD0018u, 0x2000011Au, // v_frexp_mant_f64 v[24:25], -v[26:27]
            SEndpgm,
        ]);
        Assert.True(
            Gen5ShaderTranslator.TryDecodeProgram(
                ctx,
                CodeAddress,
                out var program,
                out var decodeError),
            decodeError);
        Assert.Equal(
            [
                "VCvtI32F64", "VCvtF64I32", "VTruncF64", "VRcpF64",
                "VFloorF32", "VBfrevB32", "VFrexpExpI32F64",
                "VFrexpMantF64", "SEndpgm",
            ],
            program.Instructions.Select(instruction => instruction.Opcode));
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
        Assert.True(ContainsSpirvCapability(shader.Spirv, SpirvCapability.Float64));
        Assert.True(ContainsSpirvOpcode(shader.Spirv, (ushort)SpirvOp.FNegate));
        Assert.True(ContainsSpirvOpcode(shader.Spirv, (ushort)SpirvOp.BitReverse));
        Assert.True(ContainsGlslExtInst(shader.Spirv, 4));
        Assert.True(ContainsGlslExtInst(shader.Spirv, 52));
    }

    [Fact]
    public void CompilesExtendedVop2EncodingsToSpirv()
    {
        // E64 encodings assembled with LLVM 18 llvm-mc for gfx1030 and
        // verified with llvm-objdump.
        var ctx = CreateContext(
        [
            0xD5050100u, 0x20020902u, // v_subrev_f32 v0, -|v2|, v4
            0xD5090006u, 0x00021508u, // v_mul_i32_i24 v6, v8, v10
            0xD516000Cu, 0x0002210Eu, // v_lshrrev_b32 v12, v14, v16
            0xD51B0012u, 0x00022D14u, // v_and_b32 v18, v20, v22
            0xD52B0024u, 0x00025126u, // v_fmac_f32 v36, v38, v40
            0xD52F002Au, 0x00025D2Cu, // v_cvt_pkrtz_f16_f32 v42, v44, v46
            SEndpgm,
        ]);
        Assert.True(
            Gen5ShaderTranslator.TryDecodeProgram(
                ctx,
                CodeAddress,
                out var program,
                out var decodeError),
            decodeError);
        Assert.Equal(
            [
                "VSubrevF32", "VMulI32I24", "VLshrrevB32", "VAndB32",
                "VFmacF32", "VCvtPkrtzF16F32", "SEndpgm",
            ],
            program.Instructions.Select(instruction => instruction.Opcode));

        var modifiedControl = Assert.IsType<Gen5Vop3Control>(program.Instructions[0].Control);
        Assert.Equal(1u, modifiedControl.AbsoluteMask);
        Assert.Equal(1u, modifiedControl.NegateMask);

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
        Assert.True(ContainsSpirvOpcode(shader.Spirv, (ushort)SpirvOp.FSub));
        Assert.True(ContainsSpirvOpcode(shader.Spirv, (ushort)SpirvOp.FNegate));
        Assert.True(ContainsSpirvOpcode(shader.Spirv, (ushort)SpirvOp.IMul));
        Assert.True(ContainsSpirvOpcode(shader.Spirv, (ushort)SpirvOp.BitFieldSExtract));
        Assert.True(ContainsSpirvOpcode(shader.Spirv, (ushort)SpirvOp.ShiftRightLogical));
        Assert.True(ContainsSpirvOpcode(shader.Spirv, (ushort)SpirvOp.BitwiseAnd));
        Assert.True(ContainsGlslExtInst(shader.Spirv, 50));
        Assert.True(ContainsGlslExtInst(shader.Spirv, 58));
    }

    [Fact]
    public void CompilesVectorInteger64MultiplyAddToSpirv()
    {
        // Encodings assembled with LLVM 18 llvm-mc for gfx1030 and verified
        // with llvm-objdump.
        var ctx = CreateContext(
        [
            0xD5766A00u, 0x04120702u, // v_mad_u64_u32 v[0:1], vcc_lo, v2, v3, v[4:5]
            0xD5776A06u, 0x042A1308u, // v_mad_i64_i32 v[6:7], vcc_lo, v8, v9, v[10:11]
            0xD576280Cu, 0x04421F0Eu, // v_mad_u64_u32 v[12:13], s40, v14, v15, v[16:17]
            0xD5772A12u, 0x045A2B14u, // v_mad_i64_i32 v[18:19], s42, v20, v21, v[22:23]
            SEndpgm,
        ]);
        Assert.True(
            Gen5ShaderTranslator.TryDecodeProgram(
                ctx,
                CodeAddress,
                out var program,
                out var decodeError),
            decodeError);
        Assert.Equal(
            ["VMadU64U32", "VMadI64I32", "VMadU64U32", "VMadI64I32", "SEndpgm"],
            program.Instructions.Select(instruction => instruction.Opcode));
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
        Assert.True(ContainsSpirvOpcode(shader.Spirv, (ushort)SpirvOp.IMul));
        Assert.True(ContainsSpirvOpcode(shader.Spirv, (ushort)SpirvOp.IAdd));
        Assert.True(ContainsSpirvOpcode(shader.Spirv, (ushort)SpirvOp.SConvert));
        Assert.True(ContainsSpirvOpcode(shader.Spirv, (ushort)SpirvOp.UConvert));
        Assert.True(ContainsSpirvOpcode(shader.Spirv, (ushort)SpirvOp.ULessThan));
        Assert.True(ContainsSpirvOpcode(shader.Spirv, (ushort)SpirvOp.SLessThan));
        Assert.True(ContainsSpirvOpcode(shader.Spirv, (ushort)SpirvOp.LogicalEqual));
    }

    [Fact]
    public void CompilesVectorFloat16FusedMultiplyAddToSpirv()
    {
        // Encodings assembled with LLVM 18 llvm-mc for gfx1030 and verified
        // with llvm-objdump.
        var ctx = CreateContext(
        [
            0xD74B0001u, 0x04120702u, // v_fma_f16 v1, v2, v3, v4
            0xD74B7805u, 0x04220F06u, // v_fma_f16 v5, v6, v7, v8 op_sel:[1,1,1,1]
            0xD74B8109u, 0x4C32170Au, // v_fma_f16 v9, |v10|, -v11, v12 clamp mul:2
            SEndpgm,
        ]);
        Assert.True(
            Gen5ShaderTranslator.TryDecodeProgram(
                ctx,
                CodeAddress,
                out var program,
                out var decodeError),
            decodeError);
        Assert.Equal(
            ["VFmaF16", "VFmaF16", "VFmaF16", "SEndpgm"],
            program.Instructions.Select(instruction => instruction.Opcode));

        var lowControl = Assert.IsType<Gen5Vop3Control>(program.Instructions[0].Control);
        Assert.Equal(0u, lowControl.OpSelectMask);
        var highControl = Assert.IsType<Gen5Vop3Control>(program.Instructions[1].Control);
        Assert.Equal(15u, highControl.OpSelectMask);
        var modifiedControl = Assert.IsType<Gen5Vop3Control>(program.Instructions[2].Control);
        Assert.Equal(1u, modifiedControl.AbsoluteMask);
        Assert.Equal(2u, modifiedControl.NegateMask);
        Assert.Equal(1u, modifiedControl.OutputModifier);
        Assert.True(modifiedControl.Clamp);

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
        Assert.True(ContainsGlslExtInst(shader.Spirv, 4));
        Assert.True(ContainsGlslExtInst(shader.Spirv, 43));
        Assert.True(ContainsGlslExtInst(shader.Spirv, 50));
        Assert.True(ContainsGlslExtInst(shader.Spirv, 58));
        Assert.True(ContainsGlslExtInst(shader.Spirv, 62));
        Assert.True(ContainsSpirvOpcode(shader.Spirv, (ushort)SpirvOp.CompositeExtract));
        Assert.True(ContainsSpirvOpcode(shader.Spirv, (ushort)SpirvOp.FNegate));
        Assert.True(ContainsSpirvOpcode(shader.Spirv, (ushort)SpirvOp.FMul));
        Assert.True(ContainsSpirvOpcode(shader.Spirv, (ushort)SpirvOp.BitwiseOr));
    }

    [Fact]
    public void CompilesVectorDivisionCorrectionFmaToSpirv()
    {
        // Encodings assembled with LLVM 18 llvm-mc for gfx1030 and verified
        // with llvm-objdump.
        var ctx = CreateContext(
        [
            0xD56F0000u, 0x040E0501u, // v_div_fmas_f32 v0, v1, v2, v3
            0xD56F8104u, 0x4C1E0D05u, // v_div_fmas_f32 v4, |v5|, -v6, v7 clamp mul:2
            SEndpgm,
        ]);
        Assert.True(
            Gen5ShaderTranslator.TryDecodeProgram(
                ctx,
                CodeAddress,
                out var program,
                out var decodeError),
            decodeError);
        Assert.Equal(
            ["VDivFmasF32", "VDivFmasF32", "SEndpgm"],
            program.Instructions.Select(instruction => instruction.Opcode));

        var modifiedControl = Assert.IsType<Gen5Vop3Control>(program.Instructions[1].Control);
        Assert.Equal(1u, modifiedControl.AbsoluteMask);
        Assert.Equal(2u, modifiedControl.NegateMask);
        Assert.Equal(1u, modifiedControl.OutputModifier);
        Assert.True(modifiedControl.Clamp);

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
        Assert.True(ContainsGlslExtInst(shader.Spirv, 4));
        Assert.True(ContainsGlslExtInst(shader.Spirv, 43));
        Assert.True(ContainsGlslExtInst(shader.Spirv, 50));
        Assert.True(ContainsGlslExtInst(shader.Spirv, 53));
        Assert.True(ContainsSpirvOpcode(shader.Spirv, (ushort)SpirvOp.FNegate));
        Assert.True(ContainsSpirvOpcode(shader.Spirv, (ushort)SpirvOp.FMul));
        Assert.True(ContainsSpirvOpcode(shader.Spirv, (ushort)SpirvOp.Select));
    }

    [Fact]
    public void CompilesVectorDivisionFixupToSpirv()
    {
        // Encodings assembled with LLVM 18 llvm-mc for gfx1030 and verified
        // with llvm-objdump.
        var ctx = CreateContext(
        [
            0xD55F0000u, 0x040E0501u, // v_div_fixup_f32 v0, v1, v2, v3
            0xD55F8104u, 0x4C1E0D05u, // v_div_fixup_f32 v4, |v5|, -v6, v7 clamp mul:2
            SEndpgm,
        ]);
        Assert.True(
            Gen5ShaderTranslator.TryDecodeProgram(
                ctx,
                CodeAddress,
                out var program,
                out var decodeError),
            decodeError);
        Assert.Equal(
            ["VDivFixupF32", "VDivFixupF32", "SEndpgm"],
            program.Instructions.Select(instruction => instruction.Opcode));

        var modifiedControl = Assert.IsType<Gen5Vop3Control>(program.Instructions[1].Control);
        Assert.Equal(1u, modifiedControl.AbsoluteMask);
        Assert.Equal(2u, modifiedControl.NegateMask);
        Assert.Equal(1u, modifiedControl.OutputModifier);
        Assert.True(modifiedControl.Clamp);

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
        Assert.True(ContainsSpirvOpcode(shader.Spirv, (ushort)SpirvOp.IsNan));
        Assert.True(ContainsSpirvOpcode(shader.Spirv, (ushort)SpirvOp.FDiv));
        Assert.True(ContainsSpirvOpcode(shader.Spirv, (ushort)SpirvOp.BitwiseXor));
        Assert.True(ContainsSpirvOpcode(shader.Spirv, (ushort)SpirvOp.ULessThan));
        Assert.True(ContainsSpirvOpcode(shader.Spirv, (ushort)SpirvOp.LogicalAnd));
        Assert.True(ContainsSpirvOpcode(shader.Spirv, (ushort)SpirvOp.LogicalOr));
        Assert.True(ContainsSpirvOpcode(shader.Spirv, (ushort)SpirvOp.Select));
    }

    [Fact]
    public void CompilesVectorDivisionPrescaleToSpirv()
    {
        // Encodings assembled with LLVM 18 llvm-mc for gfx1030 and verified
        // with llvm-objdump.
        var ctx = CreateContext(
        [
            0xD56D6A00u, 0x040E0501u, // v_div_scale_f32 v0, vcc_lo, v1, v2, v3
            0xD56D0A04u, 0x041E0D05u, // v_div_scale_f32 v4, s10, v5, v6, v7
            SEndpgm,
        ]);
        Assert.True(
            Gen5ShaderTranslator.TryDecodeProgram(
                ctx,
                CodeAddress,
                out var program,
                out var decodeError),
            decodeError);
        Assert.Equal(
            ["VDivScaleF32", "VDivScaleF32", "SEndpgm"],
            program.Instructions.Select(instruction => instruction.Opcode));
        Assert.Equal(
            106u,
            Assert.IsType<Gen5Vop3Control>(program.Instructions[0].Control).ScalarDestination);
        Assert.Equal(
            10u,
            Assert.IsType<Gen5Vop3Control>(program.Instructions[1].Control).ScalarDestination);

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
        Assert.True(ContainsGlslExtInst(shader.Spirv, 53));
        Assert.True(ContainsSpirvOpcode(shader.Spirv, (ushort)SpirvOp.FDiv));
        Assert.True(ContainsSpirvOpcode(shader.Spirv, (ushort)SpirvOp.FOrdEqual));
        Assert.True(ContainsSpirvOpcode(shader.Spirv, (ushort)SpirvOp.UGreaterThanEqual));
        Assert.True(ContainsSpirvOpcode(shader.Spirv, (ushort)SpirvOp.ULessThanEqual));
        Assert.True(ContainsSpirvOpcode(shader.Spirv, (ushort)SpirvOp.LogicalAnd));
        Assert.True(ContainsSpirvOpcode(shader.Spirv, (ushort)SpirvOp.LogicalOr));
        Assert.True(ContainsSpirvOpcode(shader.Spirv, (ushort)SpirvOp.Select));
        Assert.True(ContainsSpirvOpcode(shader.Spirv, (ushort)SpirvOp.Store));
    }

    [Fact]
    public void CompilesM0IndexedVectorMovesToSpirv()
    {
        // Encodings assembled with LLVM 18 llvm-mc for gfx1030 and verified
        // with llvm-objdump.
        var ctx = CreateContext(
        [
            0xBEFC0382u, // s_mov_b32 m0, 2
            0x7E0A8507u, // v_movreld_b32_e32 v5, v7
            0x7E108709u, // v_movrels_b32_e32 v8, v9
            0x7E14890Bu, // v_movrelsd_b32_e32 v10, v11
            SEndpgm,
        ]);
        Assert.True(
            Gen5ShaderTranslator.TryDecodeProgram(
                ctx,
                CodeAddress,
                out var program,
                out var decodeError),
            decodeError);
        Assert.Equal(
            ["SMovB32", "VMovreldB32", "VMovrelsB32", "VMovrelsdB32", "SEndpgm"],
            program.Instructions.Select(instruction => instruction.Opcode));
        Assert.Equal(
            Gen5Operand.Vector(7),
            Assert.Single(program.Instructions[1].Sources));
        Assert.Equal(
            Gen5Operand.Vector(5),
            Assert.Single(program.Instructions[1].Destinations));
        Assert.Equal(
            Gen5Operand.Vector(9),
            Assert.Single(program.Instructions[2].Sources));
        Assert.Equal(
            Gen5Operand.Vector(11),
            Assert.Single(program.Instructions[3].Sources));

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
        Assert.True(ContainsSpirvOpcode(shader.Spirv, (ushort)SpirvOp.AccessChain));
        Assert.True(ContainsSpirvOpcode(shader.Spirv, (ushort)SpirvOp.IAdd));
        Assert.True(ContainsSpirvOpcode(shader.Spirv, (ushort)SpirvOp.Load));
        Assert.True(ContainsSpirvOpcode(shader.Spirv, (ushort)SpirvOp.Store));
    }

    [Fact]
    public void CompilesLegacyLightingMultiplyToSpirv()
    {
        // Encodings assembled with LLVM 18 llvm-mc for gfx1030 and verified
        // with llvm-objdump. The ISA encodes a third source, but V_MULLIT_F32
        // defines its result solely as S0 * S1.
        var ctx = CreateContext(
        [
            0xD5500003u, 0x041A0B04u, // v_mullit_f32 v3, v4, v5, v6
            0xD5508107u, 0x4C2A1308u, // v_mullit_f32 v7, |v8|, -v9, v10 clamp mul:2
            SEndpgm,
        ]);
        Assert.True(
            Gen5ShaderTranslator.TryDecodeProgram(
                ctx,
                CodeAddress,
                out var program,
                out var decodeError),
            decodeError);
        Assert.Equal(
            ["VMullitF32", "VMullitF32", "SEndpgm"],
            program.Instructions.Select(instruction => instruction.Opcode));
        Assert.Equal(3, program.Instructions[0].Sources.Count);

        var modifiedControl = Assert.IsType<Gen5Vop3Control>(program.Instructions[1].Control);
        Assert.Equal(1u, modifiedControl.AbsoluteMask);
        Assert.Equal(2u, modifiedControl.NegateMask);
        Assert.Equal(1u, modifiedControl.OutputModifier);
        Assert.True(modifiedControl.Clamp);

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
        Assert.True(ContainsGlslExtInst(shader.Spirv, 4));
        Assert.True(ContainsGlslExtInst(shader.Spirv, 43));
        Assert.True(ContainsSpirvOpcode(shader.Spirv, (ushort)SpirvOp.FNegate));
        Assert.True(ContainsSpirvOpcode(shader.Spirv, (ushort)SpirvOp.FMul));
        Assert.True(ContainsSpirvOpcode(shader.Spirv, (ushort)SpirvOp.IEqual));
        Assert.True(ContainsSpirvOpcode(shader.Spirv, (ushort)SpirvOp.LogicalOr));
        Assert.True(ContainsSpirvOpcode(shader.Spirv, (ushort)SpirvOp.Select));
    }

    [Fact]
    public void UsesRdna2Vop3OpcodesAndCanonicalBitwiseNames()
    {
        // LLVM 18 gfx1030 rejects the reserved encodings below. The
        // canonical bitwise encodings were assembled and verified with LLVM.
        var ctx = CreateContext(
        [
            0xD5020000u, 0x040A0301u, // reserved (compact v_dot2c_f32_f16 encoding)
            0xD51F0000u, 0x040A0301u, // reserved (legacy v_mac_f32 encoding)
            0xD5220000u, 0x040A0301u, // reserved (compact v_bcnt_u32_b32 encoding)
            0xD5300000u, 0x040A0301u, // reserved (compact v_cvt_pk_u16_u32 encoding)
            0xD5310000u, 0x040A0301u, // reserved (compact v_cvt_pk_i16_i32 encoding)
            0xD5410000u, 0x040A0301u, // reserved (legacy v_mad_f32 encoding)
            0xD56B0000u, 0x040A0301u, // reserved (legacy v_mul_lo_i32 encoding)
            0xD76F0007u, 0x042A1308u, // v_lshl_or_b32 v7, v8, v9, v10
            0xD772000Bu, 0x043A1B0Cu, // v_or3_b32 v11, v12, v13, v14
            SEndpgm,
        ]);
        Assert.True(
            Gen5ShaderTranslator.TryDecodeProgram(
                ctx,
                CodeAddress,
                out var program,
                out var decodeError),
            decodeError);
        Assert.Equal(
            [
                "Vop3Raw102", "Vop3Raw11F", "Vop3Raw122", "Vop3Raw130",
                "Vop3Raw131", "Vop3Raw141", "Vop3Raw16B", "VLshlOrB32",
                "VOr3B32", "SEndpgm",
            ],
            program.Instructions.Select(instruction => instruction.Opcode));

        // Reserved instructions remain decodable for diagnostics, but only
        // the supported RDNA2 instructions are submitted to the translator.
        var supportedProgram = program with
        {
            Instructions = program.Instructions.Skip(7).ToArray(),
        };
        var state = new Gen5ShaderState(supportedProgram, [], Metadata: null);
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
        Assert.True(ContainsSpirvOpcode(shader.Spirv, (ushort)SpirvOp.ShiftLeftLogical));
        Assert.True(ContainsSpirvOpcode(shader.Spirv, (ushort)SpirvOp.BitwiseOr));
    }

    [Fact]
    public void CompilesRdna2ScalarIntegerAluToSpirv()
    {
        // Encodings assembled with LLVM 18 llvm-mc for gfx1030 and verified
        // with llvm-objdump.
        var ctx = CreateContext(
        [
            0x96000201u, // s_absdiff_i32 s0, s1, s2
            0x91820604u, // s_ashr_i64 s[2:3], s[4:5], s6
            0x92880B0Au, // s_bfm_b64 s[8:9], s10, s11
            0x990C0E0Du, // s_pack_ll_b32_b16 s12, s13, s14
            0x998F1110u, // s_pack_lh_b32_b16 s15, s16, s17
            0x9A121413u, // s_pack_hh_b32_b16 s18, s19, s20
            0x9A951716u, // s_mul_hi_u32 s21, s22, s23
            0x9B181A19u, // s_mul_hi_i32 s24, s25, s26
            0x971B1D1Cu, // s_lshl1_add_u32 s27, s28, s29
            0x979E201Fu, // s_lshl2_add_u32 s30, s31, s32
            0x98212322u, // s_lshl3_add_u32 s33, s34, s35
            0x98A42625u, // s_lshl4_add_u32 s36, s37, s38
            SEndpgm,
        ]);
        Assert.True(
            Gen5ShaderTranslator.TryDecodeProgram(
                ctx,
                CodeAddress,
                out var program,
                out var decodeError),
            decodeError);
        Assert.Equal(
            [
                "SAbsdiffI32", "SAshrI64", "SBfmB64",
                "SPackLlB32B16", "SPackLhB32B16", "SPackHhB32B16",
                "SMulHiU32", "SMulHiI32",
                "SLshl1AddU32", "SLshl2AddU32", "SLshl3AddU32", "SLshl4AddU32",
                "SEndpgm",
            ],
            program.Instructions.Select(instruction => instruction.Opcode));

        var scalarRegisters = new uint[39];
        scalarRegisters[1] = 0x8000_0000;
        scalarRegisters[2] = 1;
        scalarRegisters[4] = 0xFFFF_FFF8;
        scalarRegisters[5] = uint.MaxValue;
        scalarRegisters[6] = 2;
        scalarRegisters[10] = 4;
        scalarRegisters[11] = 8;
        scalarRegisters[13] = 0x1234_5678;
        scalarRegisters[14] = 0x89AB_CDEF;
        scalarRegisters[16] = 0x1357_2468;
        scalarRegisters[17] = 0xABCD_0123;
        scalarRegisters[19] = 0x1357_2468;
        scalarRegisters[20] = 0xABCD_0123;
        scalarRegisters[22] = uint.MaxValue;
        scalarRegisters[23] = 2;
        scalarRegisters[25] = 0xFFFF_FFFE;
        scalarRegisters[26] = 3;
        scalarRegisters[28] = 0x8000_0000;
        scalarRegisters[31] = 0x4000_0000;
        scalarRegisters[32] = 1;
        scalarRegisters[34] = 0x2000_0000;
        scalarRegisters[35] = 2;
        scalarRegisters[37] = 0x1000_0000;
        scalarRegisters[38] = 3;
        var state = new Gen5ShaderState(program, scalarRegisters, Metadata: null);
        Assert.True(
            Gen5ShaderScalarEvaluator.TryEvaluate(
                ctx,
                state,
                out var evaluation,
                out var evaluationError),
            evaluationError);
        Assert.Equal(0x7FFF_FFFFu, evaluation.ScalarRegisters[0]);
        Assert.Equal(0xFFFF_FFFEu, evaluation.ScalarRegisters[2]);
        Assert.Equal(uint.MaxValue, evaluation.ScalarRegisters[3]);
        Assert.Equal(0x0000_0F00u, evaluation.ScalarRegisters[8]);
        Assert.Equal(0u, evaluation.ScalarRegisters[9]);
        Assert.Equal(0xCDEF_5678u, evaluation.ScalarRegisters[12]);
        Assert.Equal(0xABCD_2468u, evaluation.ScalarRegisters[15]);
        Assert.Equal(0xABCD_1357u, evaluation.ScalarRegisters[18]);
        Assert.Equal(1u, evaluation.ScalarRegisters[21]);
        Assert.Equal(uint.MaxValue, evaluation.ScalarRegisters[24]);
        Assert.Equal(0u, evaluation.ScalarRegisters[27]);
        Assert.Equal(1u, evaluation.ScalarRegisters[30]);
        Assert.Equal(2u, evaluation.ScalarRegisters[33]);
        Assert.Equal(3u, evaluation.ScalarRegisters[36]);
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
        Assert.True(ContainsSpirvOpcode(shader.Spirv, (ushort)SpirvOp.IMul));
        Assert.True(ContainsSpirvOpcode(shader.Spirv, (ushort)SpirvOp.SConvert));
        Assert.True(ContainsSpirvOpcode(shader.Spirv, (ushort)SpirvOp.UConvert));
        Assert.True(ContainsSpirvOpcode(shader.Spirv, (ushort)SpirvOp.ShiftRightArithmetic));
        Assert.True(ContainsSpirvOpcode(shader.Spirv, (ushort)SpirvOp.ShiftRightLogical));
        Assert.True(ContainsSpirvOpcode(shader.Spirv, (ushort)SpirvOp.ShiftLeftLogical));
        Assert.True(ContainsSpirvOpcode(shader.Spirv, (ushort)SpirvOp.SLessThan));
        Assert.True(ContainsSpirvOpcode(shader.Spirv, (ushort)SpirvOp.UGreaterThan));
        Assert.True(ContainsSpirvOpcode(shader.Spirv, (ushort)SpirvOp.Select));
    }

    [Fact]
    public void CompilesRdna2ScalarUnaryAluToSpirv()
    {
        // Encodings assembled with LLVM 18 llvm-mc for gfx1030 and verified
        // with llvm-objdump.
        var ctx = CreateContext(
        [
            0xBE800901u, // s_wqm_b32 s0, s1
            0xBE820A04u, // s_wqm_b64 s[2:3], s[4:5]
            0xBE860C08u, // s_brev_b64 s[6:7], s[8:9]
            0xBE8A0D0Bu, // s_bcnt0_i32_b32 s10, s11
            0xBE8C0E0Eu, // s_bcnt0_i32_b64 s12, s[14:15]
            0xBE8D1010u, // s_bcnt1_i32_b64 s13, s[16:17]
            0xBE921113u, // s_ff0_i32_b32 s18, s19
            0xBE941216u, // s_ff0_i32_b64 s20, s[22:23]
            0xBE951418u, // s_ff1_i32_b64 s21, s[24:25]
            0xBE9A151Bu, // s_flbit_i32_b32 s26, s27
            0xBE9C1620u, // s_flbit_i32_b64 s28, s[32:33]
            0xBE9D171Eu, // s_flbit_i32 s29, s30
            0xBE9F1822u, // s_flbit_i32_i64 s31, s[34:35]
            0xBEA41925u, // s_sext_i32_i8 s36, s37
            0xBEA61A27u, // s_sext_i32_i16 s38, s39
            0xBEA81B29u, // s_bitset0_b32 s40, s41
            0xBEAA1C2Cu, // s_bitset0_b64 s[42:43], s44
            0xBEAE1E30u, // s_bitset1_b64 s[46:47], s48
            0xBEB13432u, // s_abs_i32 s49, s50
            0xBF060000u, // s_cmp_eq_u32 s0, s0
            0xBEB30B34u, // s_brev_b32 s51, s52
            0xB1350007u, // s_cmovk_i32 s53, 7
            0xBF070000u, // s_cmp_lg_u32 s0, s0
            0xBEB31336u, // s_ff1_i32_b32 s51, s54
            0xB1350009u, // s_cmovk_i32 s53, 9
            SEndpgm,
        ]);
        Assert.True(
            Gen5ShaderTranslator.TryDecodeProgram(
                ctx,
                CodeAddress,
                out var program,
                out var decodeError),
            decodeError);

        var scalarRegisters = new uint[55];
        scalarRegisters[1] = 0x0001_0000;
        scalarRegisters[4] = 1;
        scalarRegisters[5] = 0x8000_0000;
        scalarRegisters[8] = 1;
        scalarRegisters[11] = uint.MaxValue;
        scalarRegisters[14] = uint.MaxValue;
        scalarRegisters[16] = 0xF0F0_F0F0;
        scalarRegisters[17] = 0x0F0F_0F0F;
        scalarRegisters[19] = 0xFFFF_FFFE;
        scalarRegisters[22] = uint.MaxValue;
        scalarRegisters[23] = 0xFFFE_FFFF;
        scalarRegisters[25] = 0x10;
        scalarRegisters[27] = 0x0000_CCCC;
        scalarRegisters[33] = 0x0000_CCCC;
        scalarRegisters[30] = 0xFFFF_3333;
        scalarRegisters[34] = uint.MaxValue;
        scalarRegisters[35] = 0xFFFF_3333;
        scalarRegisters[37] = 0x80;
        scalarRegisters[39] = 0x8001;
        scalarRegisters[40] = uint.MaxValue;
        scalarRegisters[41] = 31;
        scalarRegisters[42] = uint.MaxValue;
        scalarRegisters[43] = uint.MaxValue;
        scalarRegisters[44] = 32;
        scalarRegisters[48] = 63;
        scalarRegisters[50] = 0x8000_0001;
        scalarRegisters[54] = 1;
        var state = new Gen5ShaderState(program, scalarRegisters, Metadata: null);
        Assert.True(
            Gen5ShaderScalarEvaluator.TryEvaluate(
                ctx,
                state,
                out var evaluation,
                out var evaluationError),
            evaluationError);
        Assert.Equal(0x000F_0000u, evaluation.ScalarRegisters[0]);
        Assert.Equal(0x0000_000Fu, evaluation.ScalarRegisters[2]);
        Assert.Equal(0xF000_0000u, evaluation.ScalarRegisters[3]);
        Assert.Equal(0u, evaluation.ScalarRegisters[6]);
        Assert.Equal(0x8000_0000u, evaluation.ScalarRegisters[7]);
        Assert.Equal(0u, evaluation.ScalarRegisters[10]);
        Assert.Equal(32u, evaluation.ScalarRegisters[12]);
        Assert.Equal(32u, evaluation.ScalarRegisters[13]);
        Assert.Equal(0u, evaluation.ScalarRegisters[18]);
        Assert.Equal(48u, evaluation.ScalarRegisters[20]);
        Assert.Equal(36u, evaluation.ScalarRegisters[21]);
        Assert.Equal(16u, evaluation.ScalarRegisters[26]);
        Assert.Equal(16u, evaluation.ScalarRegisters[28]);
        Assert.Equal(16u, evaluation.ScalarRegisters[29]);
        Assert.Equal(16u, evaluation.ScalarRegisters[31]);
        Assert.Equal(0xFFFF_FF80u, evaluation.ScalarRegisters[36]);
        Assert.Equal(0xFFFF_8001u, evaluation.ScalarRegisters[38]);
        Assert.Equal(0x7FFF_FFFFu, evaluation.ScalarRegisters[40]);
        Assert.Equal(uint.MaxValue, evaluation.ScalarRegisters[42]);
        Assert.Equal(0xFFFF_FFFEu, evaluation.ScalarRegisters[43]);
        Assert.Equal(0u, evaluation.ScalarRegisters[46]);
        Assert.Equal(0x8000_0000u, evaluation.ScalarRegisters[47]);
        Assert.Equal(0x7FFF_FFFFu, evaluation.ScalarRegisters[49]);
        Assert.Equal(0u, evaluation.ScalarRegisters[51]);
        Assert.Equal(7u, evaluation.ScalarRegisters[53]);
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
        Assert.True(ContainsSpirvOpcode(shader.Spirv, (ushort)SpirvOp.BitCount));
        Assert.True(ContainsSpirvOpcode(shader.Spirv, (ushort)SpirvOp.BitReverse));
        Assert.True(ContainsSpirvOpcode(shader.Spirv, (ushort)SpirvOp.BitFieldSExtract));
        Assert.True(ContainsSpirvOpcode(shader.Spirv, (ushort)SpirvOp.BitFieldInsert));
        Assert.True(ContainsSpirvOpcode(shader.Spirv, (ushort)SpirvOp.ExtInst));
    }

    [Fact]
    public void CompilesRdna2ScalarMaskTransformsToSpirv()
    {
        // Encodings assembled with LLVM 18 llvm-mc for gfx1030 and verified
        // with llvm-objdump.
        var ctx = CreateContext(
        [
            0xBE802C01u, // s_quadmask_b32 s0, s1
            0xBE822D04u, // s_quadmask_b64 s[2:3], s[4:5]
            0xBE863B08u, // s_bitreplicate_b64_b32 s[6:7], s8
            SEndpgm,
        ]);
        Assert.True(
            Gen5ShaderTranslator.TryDecodeProgram(
                ctx,
                CodeAddress,
                out var program,
                out var decodeError),
            decodeError);
        Assert.Equal(
            ["SQuadmaskB32", "SQuadmaskB64", "SBitreplicateB64B32", "SEndpgm"],
            program.Instructions.Select(instruction => instruction.Opcode));

        var scalarRegisters = new uint[9];
        scalarRegisters[1] = 0x8000_0101;
        scalarRegisters[4] = 0x8000_0001;
        scalarRegisters[5] = 0x0001_0000;
        scalarRegisters[8] = 0x8000_0001;
        var state = new Gen5ShaderState(program, scalarRegisters, Metadata: null);
        Assert.True(
            Gen5ShaderScalarEvaluator.TryEvaluate(
                ctx,
                state,
                out var evaluation,
                out var evaluationError),
            evaluationError);
        Assert.Equal(0x85u, evaluation.ScalarRegisters[0]);
        Assert.Equal(0x1081u, evaluation.ScalarRegisters[2]);
        Assert.Equal(0u, evaluation.ScalarRegisters[3]);
        Assert.Equal(3u, evaluation.ScalarRegisters[6]);
        Assert.Equal(0xC000_0000u, evaluation.ScalarRegisters[7]);
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
        Assert.True(ContainsSpirvOpcode(shader.Spirv, (ushort)SpirvOp.Select));
        Assert.True(ContainsSpirvOpcode(shader.Spirv, (ushort)SpirvOp.ShiftLeftLogical));
        Assert.True(ContainsSpirvOpcode(shader.Spirv, (ushort)SpirvOp.ShiftRightLogical));
    }

    [Fact]
    public void CompilesRdna2ScalarRelativeMovesToSpirv()
    {
        // Encodings assembled with LLVM 18 llvm-mc for gfx1030 and verified
        // with llvm-objdump.
        var ctx = CreateContext(
        [
            0xBEFC0382u, // s_mov_b32 m0, 2
            0xBE802E0Au, // s_movrels_b32 s0, s10
            0xBE822F0Eu, // s_movrels_b64 s[2:3], s[14:15]
            0xBE943004u, // s_movreld_b32 s20, s4
            0xBE983106u, // s_movreld_b64 s[24:25], s[6:7]
            0xBEFC3009u, // s_movreld_b32 m0, s9 (writes exec_lo at m0 + 2)
            0xBEFC03FFu, // s_mov_b32 m0, 0x0014000a
            0x0014000Au,
            0xBE9E4908u, // s_movrelsd_2_b32 s30, s8
            SEndpgm,
        ]);
        Assert.True(
            Gen5ShaderTranslator.TryDecodeProgram(
                ctx,
                CodeAddress,
                out var program,
                out var decodeError),
            decodeError);
        Assert.Equal(
            [
                "SMovB32", "SMovrelsB32", "SMovrelsB64", "SMovreldB32",
                "SMovreldB64", "SMovreldB32", "SMovB32", "SMovrelsd2B32", "SEndpgm",
            ],
            program.Instructions.Select(instruction => instruction.Opcode));

        var scalarRegisters = new uint[125];
        scalarRegisters[4] = 0xCAFE_BABE;
        scalarRegisters[6] = 0x0102_0304;
        scalarRegisters[7] = 0x0506_0708;
        scalarRegisters[9] = 0x00FF_00FF;
        scalarRegisters[12] = 0xAABB_CCDD;
        scalarRegisters[16] = 0x1122_3344;
        scalarRegisters[17] = 0x5566_7788;
        scalarRegisters[18] = 0xDEAD_BEEF;
        var state = new Gen5ShaderState(program, scalarRegisters, Metadata: null);
        Assert.True(
            Gen5ShaderScalarEvaluator.TryEvaluate(
                ctx,
                state,
                out var evaluation,
                out var evaluationError),
            evaluationError);
        Assert.Equal(0xAABB_CCDDu, evaluation.ScalarRegisters[0]);
        Assert.Equal(0x1122_3344u, evaluation.ScalarRegisters[2]);
        Assert.Equal(0x5566_7788u, evaluation.ScalarRegisters[3]);
        Assert.Equal(0xCAFE_BABEu, evaluation.ScalarRegisters[22]);
        Assert.Equal(0x0102_0304u, evaluation.ScalarRegisters[26]);
        Assert.Equal(0x0506_0708u, evaluation.ScalarRegisters[27]);
        Assert.Equal(0xDEAD_BEEFu, evaluation.ScalarRegisters[50]);
        Assert.Equal(0x00FF_00FFu, evaluation.ScalarRegisters[126]);
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
        Assert.True(ContainsSpirvOpcode(shader.Spirv, (ushort)SpirvOp.AccessChain));
        Assert.True(ContainsSpirvOpcode(shader.Spirv, (ushort)SpirvOp.IAdd));
    }

    [Fact]
    public void CompilesRdna2ScalarExecMaskOperationsToSpirv()
    {
        // Encodings assembled with LLVM 18 llvm-mc for gfx1030 and verified
        // with llvm-objdump.
        var ctx = CreateContext(
        [
            0xBEFE04C1u, // s_mov_b64 exec, -1
            0xBE80393Cu, // s_andn1_wrexec_b64 s[0:1], s[60:61]
            0xBEB40581u, // s_cmov_b32 s52, 1
            0xBEFE04C1u, // s_mov_b64 exec, -1
            0xBE823A3Cu, // s_andn2_wrexec_b64 s[2:3], s[60:61]
            0xBEB50581u, // s_cmov_b32 s53, 1
            0xBEFE03C1u, // s_mov_b32 exec_lo, -1
            0xBE943C3Cu, // s_and_saveexec_b32 s20, s60
            0xBEA8037Eu, // s_mov_b32 s40, exec_lo
            0xBEFE03C1u, // s_mov_b32 exec_lo, -1
            0xBE953D3Cu, // s_or_saveexec_b32 s21, s60
            0xBEA9037Eu, // s_mov_b32 s41, exec_lo
            0xBEFE03C1u, // s_mov_b32 exec_lo, -1
            0xBE963E3Cu, // s_xor_saveexec_b32 s22, s60
            0xBEAA037Eu, // s_mov_b32 s42, exec_lo
            0xBEFE03C1u, // s_mov_b32 exec_lo, -1
            0xBE973F3Cu, // s_andn2_saveexec_b32 s23, s60
            0xBEAB037Eu, // s_mov_b32 s43, exec_lo
            0xBEFE03C1u, // s_mov_b32 exec_lo, -1
            0xBE98403Cu, // s_orn2_saveexec_b32 s24, s60
            0xBEAC037Eu, // s_mov_b32 s44, exec_lo
            0xBEFE03C1u, // s_mov_b32 exec_lo, -1
            0xBE99413Cu, // s_nand_saveexec_b32 s25, s60
            0xBEAD037Eu, // s_mov_b32 s45, exec_lo
            0xBEFE03C1u, // s_mov_b32 exec_lo, -1
            0xBE9A423Cu, // s_nor_saveexec_b32 s26, s60
            0xBEAE037Eu, // s_mov_b32 s46, exec_lo
            0xBEFE03C1u, // s_mov_b32 exec_lo, -1
            0xBE9B433Cu, // s_xnor_saveexec_b32 s27, s60
            0xBEAF037Eu, // s_mov_b32 s47, exec_lo
            0xBEFE03C1u, // s_mov_b32 exec_lo, -1
            0xBE9C443Cu, // s_andn1_saveexec_b32 s28, s60
            0xBEB0037Eu, // s_mov_b32 s48, exec_lo
            0xBEFE03C1u, // s_mov_b32 exec_lo, -1
            0xBE9D453Cu, // s_orn1_saveexec_b32 s29, s60
            0xBEB1037Eu, // s_mov_b32 s49, exec_lo
            0xBEFE03C1u, // s_mov_b32 exec_lo, -1
            0xBE9E463Cu, // s_andn1_wrexec_b32 s30, s60
            0xBEB20581u, // s_cmov_b32 s50, 1
            0xBEFE03C1u, // s_mov_b32 exec_lo, -1
            0xBE9F473Cu, // s_andn2_wrexec_b32 s31, s60
            0xBEB30581u, // s_cmov_b32 s51, 1
            0xBEFE04C1u, // s_mov_b64 exec, -1
            0xBEC0243Cu, // s_and_saveexec_b64 s[64:65], s[60:61]
            0xBED4047Eu, // s_mov_b64 s[84:85], exec
            0xBEFE04C1u, // s_mov_b64 exec, -1
            0xBEC2253Cu, // s_or_saveexec_b64 s[66:67], s[60:61]
            0xBED6047Eu, // s_mov_b64 s[86:87], exec
            0xBEFE04C1u, // s_mov_b64 exec, -1
            0xBEC4263Cu, // s_xor_saveexec_b64 s[68:69], s[60:61]
            0xBED8047Eu, // s_mov_b64 s[88:89], exec
            0xBEFE04C1u, // s_mov_b64 exec, -1
            0xBEC6273Cu, // s_andn2_saveexec_b64 s[70:71], s[60:61]
            0xBEDA047Eu, // s_mov_b64 s[90:91], exec
            0xBEFE04C1u, // s_mov_b64 exec, -1
            0xBEC8283Cu, // s_orn2_saveexec_b64 s[72:73], s[60:61]
            0xBEDC047Eu, // s_mov_b64 s[92:93], exec
            0xBEFE04C1u, // s_mov_b64 exec, -1
            0xBECA293Cu, // s_nand_saveexec_b64 s[74:75], s[60:61]
            0xBEDE047Eu, // s_mov_b64 s[94:95], exec
            0xBEFE04C1u, // s_mov_b64 exec, -1
            0xBECC2A3Cu, // s_nor_saveexec_b64 s[76:77], s[60:61]
            0xBEE0047Eu, // s_mov_b64 s[96:97], exec
            0xBEFE04C1u, // s_mov_b64 exec, -1
            0xBECE2B3Cu, // s_xnor_saveexec_b64 s[78:79], s[60:61]
            0xBEE2047Eu, // s_mov_b64 s[98:99], exec
            0xBEFE04C1u, // s_mov_b64 exec, -1
            0xBED0373Cu, // s_andn1_saveexec_b64 s[80:81], s[60:61]
            0xBEE4047Eu, // s_mov_b64 s[100:101], exec
            0xBEFE04C1u, // s_mov_b64 exec, -1
            0xBED2383Cu, // s_orn1_saveexec_b64 s[82:83], s[60:61]
            0xBEE6047Eu, // s_mov_b64 s[102:103], exec
            SEndpgm,
        ]);
        Assert.True(
            Gen5ShaderTranslator.TryDecodeProgram(
                ctx,
                CodeAddress,
                out var program,
                out var decodeError),
            decodeError);
        Assert.Equal(
            [
                "SAndn1WrexecB64", "SAndn2WrexecB64",
                "SAndSaveexecB32", "SOrSaveexecB32", "SXorSaveexecB32",
                "SAndn2SaveexecB32", "SOrn2SaveexecB32", "SNandSaveexecB32",
                "SNorSaveexecB32", "SXnorSaveexecB32", "SAndn1SaveexecB32",
                "SOrn1SaveexecB32", "SAndn1WrexecB32", "SAndn2WrexecB32",
                "SAndSaveexecB64", "SOrSaveexecB64", "SXorSaveexecB64",
                "SAndn2SaveexecB64", "SOrn2SaveexecB64", "SNandSaveexecB64",
                "SNorSaveexecB64", "SXnorSaveexecB64", "SAndn1SaveexecB64",
                "SOrn1SaveexecB64",
            ],
            program.Instructions
                .Where(instruction =>
                    instruction.Opcode.Contains("Saveexec", StringComparison.Ordinal) ||
                    instruction.Opcode.Contains("Wrexec", StringComparison.Ordinal))
                .Select(instruction => instruction.Opcode));

        const uint source = 0x0F0F_00FFu;
        var scalarRegisters = new uint[104];
        scalarRegisters[60] = source;
        scalarRegisters[61] = 0xA5A5_5A5Au;
        var state = new Gen5ShaderState(program, scalarRegisters, Metadata: null);
        Assert.True(
            Gen5ShaderScalarEvaluator.TryEvaluate(
                ctx,
                state,
                out var evaluation,
                out var evaluationError),
            evaluationError);
        Assert.Equal(~source, evaluation.ScalarRegisters[0]);
        Assert.Equal(0u, evaluation.ScalarRegisters[1]);
        Assert.Equal(0u, evaluation.ScalarRegisters[2]);
        Assert.Equal(0u, evaluation.ScalarRegisters[3]);
        Assert.Equal(1u, evaluation.ScalarRegisters[52]);
        Assert.Equal(0u, evaluation.ScalarRegisters[53]);
        for (var register = 20; register <= 29; register++)
        {
            Assert.Equal(uint.MaxValue, evaluation.ScalarRegisters[register]);
        }

        Assert.Equal(
            [
                source, uint.MaxValue, ~source, 0u, source,
                ~source, 0u, source, ~source, uint.MaxValue,
            ],
            evaluation.ScalarRegisters.Skip(40).Take(10));
        Assert.Equal(~source, evaluation.ScalarRegisters[30]);
        Assert.Equal(0u, evaluation.ScalarRegisters[31]);
        Assert.Equal(1u, evaluation.ScalarRegisters[50]);
        Assert.Equal(0u, evaluation.ScalarRegisters[51]);
        for (var register = 64; register <= 82; register += 2)
        {
            Assert.Equal(uint.MaxValue, evaluation.ScalarRegisters[register]);
            Assert.Equal(0u, evaluation.ScalarRegisters[register + 1]);
        }

        var expected64Results = new[]
        {
            source, uint.MaxValue, ~source, 0u, source,
            ~source, 0u, source, ~source, uint.MaxValue,
        };
        for (var index = 0; index < expected64Results.Length; index++)
        {
            var register = 84 + index * 2;
            Assert.Equal(expected64Results[index], evaluation.ScalarRegisters[register]);
            Assert.Equal(0u, evaluation.ScalarRegisters[register + 1]);
        }

        Assert.Equal(uint.MaxValue, evaluation.ScalarRegisters[126]);
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
        Assert.True(ContainsSpirvOpcode(shader.Spirv, (ushort)SpirvOp.BitwiseAnd));
        Assert.True(ContainsSpirvOpcode(shader.Spirv, (ushort)SpirvOp.BitwiseOr));
        Assert.True(ContainsSpirvOpcode(shader.Spirv, (ushort)SpirvOp.BitwiseXor));
    }

    [Fact]
    public void CompilesRdna2ScalarConditionalOperationsToSpirv()
    {
        // Encodings assembled with LLVM 18 llvm-mc for gfx1030 and verified
        // with llvm-objdump.
        var ctx = CreateContext(
        [
            0xBF120200u, // s_cmp_eq_u64 s[0:1], s[2:3]
            0xB104FFFEu, // s_cmovk_i32 s4, -2
            0xBE880504u, // s_cmov_b32 s8, s4
            0xBE8A0600u, // s_cmov_b64 s[10:11], s[0:1]
            0xBF130200u, // s_cmp_lg_u64 s[0:1], s[2:3]
            0xB1050007u, // s_cmovk_i32 s5, 7
            0xBE890504u, // s_cmov_b32 s9, s4
            0xBE8C0600u, // s_cmov_b64 s[12:13], s[0:1]
            0xBF0FBF00u, // s_bitcmp1_b64 s[0:1], 63
            0xB1060009u, // s_cmovk_i32 s6, 9
            0xBF0E8000u, // s_bitcmp0_b64 s[0:1], 0
            0xB107000Bu, // s_cmovk_i32 s7, 11
            SEndpgm,
        ]);
        Assert.True(
            Gen5ShaderTranslator.TryDecodeProgram(
                ctx,
                CodeAddress,
                out var program,
                out var decodeError),
            decodeError);
        Assert.Equal(
            [
                "SCmpEqU64", "SCmovkI32", "SCmovB32", "SCmovB64",
                "SCmpLgU64", "SCmovkI32", "SCmovB32", "SCmovB64",
                "SBitcmp1B64", "SCmovkI32", "SBitcmp0B64", "SCmovkI32",
                "SEndpgm",
            ],
            program.Instructions.Select(instruction => instruction.Opcode));

        var scalarRegisters = new uint[14];
        scalarRegisters[0] = 1;
        scalarRegisters[1] = 0x8000_0000;
        scalarRegisters[2] = 1;
        scalarRegisters[3] = 0x8000_0000;
        scalarRegisters[4] = 4;
        scalarRegisters[5] = 5;
        scalarRegisters[6] = 6;
        scalarRegisters[7] = 7;
        scalarRegisters[8] = 8;
        scalarRegisters[9] = 9;
        scalarRegisters[10] = 10;
        scalarRegisters[11] = 11;
        scalarRegisters[12] = 12;
        scalarRegisters[13] = 13;
        var state = new Gen5ShaderState(program, scalarRegisters, Metadata: null);
        Assert.True(
            Gen5ShaderScalarEvaluator.TryEvaluate(
                ctx,
                state,
                out var evaluation,
                out var evaluationError),
            evaluationError);
        Assert.Equal(0xFFFF_FFFEu, evaluation.ScalarRegisters[4]);
        Assert.Equal(5u, evaluation.ScalarRegisters[5]);
        Assert.Equal(9u, evaluation.ScalarRegisters[6]);
        Assert.Equal(7u, evaluation.ScalarRegisters[7]);
        Assert.Equal(0xFFFF_FFFEu, evaluation.ScalarRegisters[8]);
        Assert.Equal(9u, evaluation.ScalarRegisters[9]);
        Assert.Equal(1u, evaluation.ScalarRegisters[10]);
        Assert.Equal(0x8000_0000u, evaluation.ScalarRegisters[11]);
        Assert.Equal(12u, evaluation.ScalarRegisters[12]);
        Assert.Equal(13u, evaluation.ScalarRegisters[13]);
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
        Assert.True(ContainsSpirvOpcode(shader.Spirv, (ushort)SpirvOp.IEqual));
        Assert.True(ContainsSpirvOpcode(shader.Spirv, (ushort)SpirvOp.INotEqual));
        Assert.True(ContainsSpirvOpcode(shader.Spirv, (ushort)SpirvOp.Select));
    }

    [Fact]
    public void RejectsReservedRdna2Sop2Opcode2D()
    {
        var ctx = CreateContext(
        [
            0x96800201u, // reserved opcode 0x2d with s0, s1, s2 fields
            SEndpgm,
        ]);

        Assert.False(
            Gen5ShaderTranslator.TryDecodeProgram(
                ctx,
                CodeAddress,
                out _,
                out var decodeError));
        Assert.Contains("unknown-sop2 op=0x2D", decodeError, StringComparison.Ordinal);
    }

    private static bool ContainsSpirvCapability(
        byte[] spirv,
        SpirvCapability capability)
    {
        for (var offset = 5 * sizeof(uint); offset < spirv.Length;)
        {
            var instruction = BitConverter.ToUInt32(spirv, offset);
            var wordCount = instruction >> 16;
            if ((ushort)instruction == (ushort)SpirvOp.Capability &&
                wordCount >= 2 &&
                BitConverter.ToUInt32(spirv, offset + sizeof(uint)) == (uint)capability)
            {
                return true;
            }

            if (wordCount == 0)
            {
                return false;
            }

            offset += checked((int)wordCount * sizeof(uint));
        }

        return false;
    }

    private static bool ContainsGlslExtInst(byte[] spirv, uint operation)
    {
        for (var offset = 5 * sizeof(uint); offset < spirv.Length;)
        {
            var instruction = BitConverter.ToUInt32(spirv, offset);
            var wordCount = instruction >> 16;
            if ((ushort)instruction == (ushort)SpirvOp.ExtInst &&
                wordCount >= 5 &&
                BitConverter.ToUInt32(spirv, offset + (4 * sizeof(uint))) == operation)
            {
                return true;
            }

            if (wordCount == 0)
            {
                return false;
            }

            offset += checked((int)wordCount * sizeof(uint));
        }

        return false;
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
