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
