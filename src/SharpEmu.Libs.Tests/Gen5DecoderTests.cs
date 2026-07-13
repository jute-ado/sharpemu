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
    public void CompilesVectorQuadSadOperationsToSpirv()
    {
        // Encodings assembled with LLVM 18 llvm-mc for gfx1030 and verified
        // with llvm-objdump.
        var ctx = CreateContext(
        [
            0xD5720000u, 0x04160902u, // v_qsad_pk_u16_u8 v[0:1], v[2:3], v4, v[5:6]
            0xD5730007u, 0x04321709u, // v_mqsad_pk_u16_u8 v[7:8], v[9:10], v11, v[12:13]
            0xD575000Eu, 0x04562912u, // v_mqsad_u32_u8 v[14:17], v[18:19], v20, v[21:24]
            0xD5720000u, 0x04160802u, // v_qsad_pk_u16_u8 v[0:1], s[2:3], v4, v[5:6]
            0xD5720007u, 0x04301709u, // v_qsad_pk_u16_u8 v[7:8], v[9:10], s11, v[12:13]
            0xD572000Eu, 0x00522510u, // v_qsad_pk_u16_u8 v[14:15], v[16:17], v18, s[20:21]
            0xD5750016u, 0x04723418u, // v_mqsad_u32_u8 v[22:25], s[24:25], v26, v[28:31]
            0xD5750020u, 0x04A04D24u, // v_mqsad_u32_u8 v[32:35], v[36:37], s38, v[40:43]
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
                "VQsadPkU16U8",
                "VMqsadPkU16U8",
                "VMqsadU32U8",
                "VQsadPkU16U8",
                "VQsadPkU16U8",
                "VQsadPkU16U8",
                "VMqsadU32U8",
                "VMqsadU32U8",
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
        Assert.True(ContainsSpirvOpcode(shader.Spirv, (ushort)SpirvOp.IAdd));
        Assert.True(ContainsSpirvOpcode(shader.Spirv, (ushort)SpirvOp.ISub));
        Assert.True(ContainsSpirvOpcode(shader.Spirv, (ushort)SpirvOp.IEqual));
        Assert.True(ContainsSpirvOpcode(shader.Spirv, (ushort)SpirvOp.Select));
        Assert.True(ContainsSpirvOpcode(shader.Spirv, (ushort)SpirvOp.UConvert));
        Assert.True(ContainsSpirvOpcode(shader.Spirv, (ushort)SpirvOp.ShiftRightLogical));
        Assert.True(ContainsSpirvOpcode(shader.Spirv, (ushort)SpirvOp.BitwiseOr));
    }

    [Fact]
    public void CompilesVectorWideBitwiseOperationsToSpirv()
    {
        // Encodings assembled with LLVM 18 llvm-mc for gfx1030 and verified
        // with llvm-objdump.
        var ctx = CreateContext(
        [
            0xD5780000u, 0x040E0501u, // v_xor3_b32 v0, v1, v2, v3
            0xD5780004u, 0x041E0C05u, // v_xor3_b32 v4, s5, v6, v7
            0xD6FF0008u, 0x0002190Au, // v_lshlrev_b64 v[8:9], v10, v[12:13]
            0xD700000Eu, 0x00022410u, // v_lshrrev_b64 v[14:15], s16, v[18:19]
            0xD7010014u, 0x00003116u, // v_ashrrev_i64 v[20:21], v22, s[24:25]
            0xD6FF001Au, 0x000238A1u, // v_lshlrev_b64 v[26:27], 33, v[28:29]
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
                "VXor3B32",
                "VXor3B32",
                "VLshlrevB64",
                "VLshrrevB64",
                "VAshrrevI64",
                "VLshlrevB64",
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
        Assert.True(ContainsSpirvOpcode(shader.Spirv, (ushort)SpirvOp.BitwiseXor));
        Assert.True(ContainsSpirvOpcode(shader.Spirv, (ushort)SpirvOp.ShiftLeftLogical));
        Assert.True(ContainsSpirvOpcode(shader.Spirv, (ushort)SpirvOp.ShiftRightLogical));
        Assert.True(ContainsSpirvOpcode(shader.Spirv, (ushort)SpirvOp.ShiftRightArithmetic));
        Assert.True(ContainsSpirvOpcode(shader.Spirv, (ushort)SpirvOp.UConvert));
    }

    [Fact]
    public void CompilesVectorHalfPackOperationsToSpirv()
    {
        // Encodings assembled with LLVM 18 llvm-mc for gfx1030 and verified
        // with llvm-objdump.
        var ctx = CreateContext(
        [
            0xD7110000u, 0x00020501u, // v_pack_b32_f16 v0, v1, v2
            0xD7110803u, 0x00020B04u, // v_pack_b32_f16 v3, v4, v5 op_sel:[1,0,0]
            0xD7110306u, 0x20021107u, // v_pack_b32_f16 v6, -|v7|, |v8|
            0xD7110009u, 0x0002160Au, // v_pack_b32_f16 v9, s10, v11
            0xD712000Cu, 0x00021D0Du, // v_cvt_pknorm_i16_f16 v12, v13, v14
            0xD713080Fu, 0x00022310u, // v_cvt_pknorm_u16_f16 v15, v16, v17 op_sel:[1,0,0]
            0xD7120312u, 0x20022913u, // v_cvt_pknorm_i16_f16 v18, -|v19|, |v20|
            0xD7130015u, 0x00022E16u, // v_cvt_pknorm_u16_f16 v21, s22, v23
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
                "VPackB32F16",
                "VPackB32F16",
                "VPackB32F16",
                "VPackB32F16",
                "VCvtPknormI16F16",
                "VCvtPknormU16F16",
                "VCvtPknormI16F16",
                "VCvtPknormU16F16",
                "SEndpgm",
            ],
            program.Instructions.Select(instruction => instruction.Opcode));
        var modifiedControl = Assert.IsType<Gen5Vop3Control>(program.Instructions[2].Control);
        Assert.Equal(3u, modifiedControl.AbsoluteMask);
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
        Assert.True(ContainsSpirvOpcode(shader.Spirv, (ushort)SpirvOp.ExtInst));
        Assert.True(ContainsSpirvOpcode(shader.Spirv, (ushort)SpirvOp.CompositeConstruct));
        Assert.True(ContainsSpirvOpcode(shader.Spirv, (ushort)SpirvOp.BitFieldUExtract));
        Assert.True(ContainsSpirvOpcode(shader.Spirv, (ushort)SpirvOp.BitwiseAnd));
        Assert.True(ContainsSpirvOpcode(shader.Spirv, (ushort)SpirvOp.BitwiseXor));
        Assert.True(ContainsSpirvOpcode(shader.Spirv, (ushort)SpirvOp.ShiftLeftLogical));
    }

    [Fact]
    public void CompilesVectorInteger16BinaryOperationsToSpirv()
    {
        // Encodings assembled with LLVM 18 llvm-mc for gfx1030 and verified
        // with llvm-objdump.
        var ctx = CreateContext(
        [
            0xD703C800u, 0x00020501u, // v_add_nc_u16 v0, v1, v2 op_sel:[1,0,1] clamp
            0xD7048003u, 0x00020B04u, // v_sub_nc_u16 v3, v4, v5 clamp
            0xD7050006u, 0x00021107u, // v_mul_lo_u16 v6, v7, v8
            0xD7070009u, 0x0002170Au, // v_lshrrev_b16 v9, v10, v11
            0xD708000Cu, 0x00021D0Du, // v_ashrrev_i16 v12, v13, v14
            0xD709000Fu, 0x00022310u, // v_max_u16 v15, v16, v17
            0xD70A0012u, 0x00022913u, // v_max_i16 v18, v19, v20
            0xD70B0015u, 0x00022F16u, // v_min_u16 v21, v22, v23
            0xD70C0018u, 0x00023519u, // v_min_i16 v24, v25, v26
            0xD70D001Bu, 0x00023B1Cu, // v_add_nc_i16 v27, v28, v29
            0xD70ED00Cu, 0x00021D0Du, // v_sub_nc_i16 v12, v13, v14 op_sel:[0,1,1] clamp
            0xD7140021u, 0x00024722u, // v_lshlrev_b16 v33, v34, v35
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
                "VAddNcU16",
                "VSubNcU16",
                "VMulLoU16",
                "VLshrrevB16",
                "VAshrrevI16",
                "VMaxU16",
                "VMaxI16",
                "VMinU16",
                "VMinI16",
                "VAddNcI16",
                "VSubNcI16",
                "VLshlrevB16",
                "SEndpgm",
            ],
            program.Instructions.Select(instruction => instruction.Opcode));
        var unsignedClamp = Assert.IsType<Gen5Vop3Control>(program.Instructions[0].Control);
        Assert.True(unsignedClamp.Clamp);
        Assert.Equal(9u, unsignedClamp.OpSelectMask);
        var signedClamp = Assert.IsType<Gen5Vop3Control>(program.Instructions[10].Control);
        Assert.True(signedClamp.Clamp);
        Assert.Equal(10u, signedClamp.OpSelectMask);
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
        Assert.True(ContainsSpirvOpcode(shader.Spirv, (ushort)SpirvOp.IAdd));
        Assert.True(ContainsSpirvOpcode(shader.Spirv, (ushort)SpirvOp.ISub));
        Assert.True(ContainsSpirvOpcode(shader.Spirv, (ushort)SpirvOp.IMul));
        Assert.True(ContainsSpirvOpcode(shader.Spirv, (ushort)SpirvOp.ULessThan));
        Assert.True(ContainsSpirvOpcode(shader.Spirv, (ushort)SpirvOp.Select));
        Assert.True(ContainsSpirvOpcode(shader.Spirv, (ushort)SpirvOp.ShiftLeftLogical));
        Assert.True(ContainsSpirvOpcode(shader.Spirv, (ushort)SpirvOp.ShiftRightLogical));
        Assert.True(ContainsSpirvOpcode(shader.Spirv, (ushort)SpirvOp.ShiftRightArithmetic));
        Assert.True(ContainsSpirvOpcode(shader.Spirv, (ushort)SpirvOp.BitFieldSExtract));
        Assert.True(ContainsSpirvOpcode(shader.Spirv, (ushort)SpirvOp.BitFieldUExtract));
    }

    [Fact]
    public void CompilesExtendedLdsAtomic32OperationsToSpirv()
    {
        // Encodings assembled with LLVM 18 llvm-mc for gfx1030 and verified
        // with llvm-objdump.
        var ctx = CreateContext(
        [
            0xD8180004u, 0x00000100u, // ds_max_i32 v0, v1 offset:4
            0xD81C0000u, 0x00000302u, // ds_min_u32 v2, v3
            0xD8980000u, 0x04000605u, // ds_max_rtn_i32 v4, v5, v6
            0xD89C0000u, 0x07000908u, // ds_min_rtn_u32 v7, v8, v9
            0xD8B40000u, 0x0A000C0Bu, // ds_wrxchg_rtn_b32 v10, v11, v12
            0xD8400000u, 0x000F0E0Du, // ds_cmpst_b32 v13, v14, v15
            0xD8C00000u, 0x10131211u, // ds_cmpst_rtn_b32 v16, v17, v18, v19
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
                "DsMaxI32",
                "DsMinU32",
                "DsMaxRtnI32",
                "DsMinRtnU32",
                "DsWrxchgRtnB32",
                "DsCmpstB32",
                "DsCmpstRtnB32",
                "SEndpgm",
            ],
            program.Instructions.Select(instruction => instruction.Opcode));
        Assert.All(
            program.Instructions.Take(5),
            instruction => Assert.Equal(2, instruction.Sources.Count));
        Assert.Equal(3, program.Instructions[5].Sources.Count);
        Assert.Equal(3, program.Instructions[6].Sources.Count);
        Assert.Empty(program.Instructions[0].Destinations);
        Assert.Empty(program.Instructions[1].Destinations);
        Assert.Empty(program.Instructions[5].Destinations);
        Assert.Equal(
            [4u, 7u, 10u, 16u],
            new[] { 2, 3, 4, 6 }
                .Select(index => Assert.Single(program.Instructions[index].Destinations).Value));
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
        Assert.True(ContainsSpirvOpcode(shader.Spirv, (ushort)SpirvOp.AtomicSMax));
        Assert.True(ContainsSpirvOpcode(shader.Spirv, (ushort)SpirvOp.AtomicUMin));
        Assert.True(ContainsSpirvOpcode(shader.Spirv, (ushort)SpirvOp.AtomicExchange));
        Assert.True(ContainsSpirvOpcode(shader.Spirv, (ushort)SpirvOp.AtomicCompareExchange));
    }

    [Fact]
    public void CompilesPairedLdsExchange32OperationsToSpirv()
    {
        // Encodings assembled with LLVM 18 llvm-mc for gfx1030 and verified
        // with llvm-objdump. Both forms exchange two independent dwords; ST64
        // applies a 64-dword stride to each encoded offset.
        var ctx = CreateContext(
        [
            0xD8B80201u, 0x00040302u,
            0xD8BC0403u, 0x05090807u,
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
            ["DsWrxchg2RtnB32", "DsWrxchg2St64RtnB32", "SEndpgm"],
            program.Instructions.Select(instruction => instruction.Opcode));
        Assert.Equal(
            [Gen5Operand.Vector(2), Gen5Operand.Vector(3), Gen5Operand.Vector(4)],
            program.Instructions[0].Sources);
        Assert.Equal(
            [Gen5Operand.Vector(0), Gen5Operand.Vector(1)],
            program.Instructions[0].Destinations);
        Assert.Equal(
            [Gen5Operand.Vector(7), Gen5Operand.Vector(8), Gen5Operand.Vector(9)],
            program.Instructions[1].Sources);
        Assert.Equal(
            [Gen5Operand.Vector(5), Gen5Operand.Vector(6)],
            program.Instructions[1].Destinations);
        var regular = Assert.IsType<Gen5DataShareControl>(program.Instructions[0].Control);
        Assert.Equal(1u, regular.Offset0);
        Assert.Equal(2u, regular.Offset1);
        var st64 = Assert.IsType<Gen5DataShareControl>(program.Instructions[1].Control);
        Assert.Equal(3u, st64.Offset0);
        Assert.Equal(4u, st64.Offset1);

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
        Assert.Equal(
            4,
            CountSpirvOpcode(shader.Spirv, (ushort)SpirvOp.AtomicExchange));
        Assert.True(ContainsSpirvConstant(shader.Spirv, 768));
        Assert.True(ContainsSpirvConstant(shader.Spirv, 1024));
        Assert.True(ContainsSpirvVariable(
            shader.Spirv,
            SpirvStorageClass.Workgroup));
    }

    [Fact]
    public void CompilesCompareExchangeLdsAtomic32OperationsToSpirv()
    {
        // Encodings assembled with LLVM 18 llvm-mc for gfx1030 and verified
        // with llvm-objdump. These atomics have no direct SPIR-V operation and
        // are implemented with retrying atomic compare/exchange loops.
        var ctx = CreateContext(
        [
            0xD80801FCu, 0x00000201u,
            0xD80C01FCu, 0x00000403u,
            0xD81001FCu, 0x00000605u,
            0xD83001FCu, 0x00090807u,
            0xD88801FCu, 0x0A000C0Bu,
            0xD88C01FCu, 0x0D000F0Eu,
            0xD89001FCu, 0x10001211u,
            0xD8B001FCu, 0x13161514u,
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
                "DsRsubU32", "DsIncU32", "DsDecU32", "DsMskorB32",
                "DsRsubRtnU32", "DsIncRtnU32", "DsDecRtnU32",
                "DsMskorRtnB32", "SEndpgm",
            ],
            program.Instructions.Select(instruction => instruction.Opcode));
        Assert.All(
            program.Instructions.Take(3),
            instruction => Assert.Equal(2, instruction.Sources.Count));
        Assert.Equal(3, program.Instructions[3].Sources.Count);
        Assert.All(
            program.Instructions.Skip(4).Take(3),
            instruction =>
            {
                Assert.Equal(2, instruction.Sources.Count);
                Assert.Single(instruction.Destinations);
            });
        Assert.Equal(3, program.Instructions[7].Sources.Count);
        Assert.Single(program.Instructions[7].Destinations);

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
        Assert.Equal(8, CountSpirvOpcode(shader.Spirv, (ushort)SpirvOp.AtomicLoad));
        Assert.Equal(
            8,
            CountSpirvOpcode(shader.Spirv, (ushort)SpirvOp.AtomicCompareExchange));
        Assert.Equal(9, CountSpirvOpcode(shader.Spirv, (ushort)SpirvOp.LoopMerge));
        Assert.True(ContainsSpirvOpcode(shader.Spirv, (ushort)SpirvOp.IAdd));
        Assert.True(ContainsSpirvOpcode(shader.Spirv, (ushort)SpirvOp.ISub));
        Assert.True(ContainsSpirvOpcode(
            shader.Spirv,
            (ushort)SpirvOp.UGreaterThanEqual));
        Assert.True(ContainsSpirvOpcode(shader.Spirv, (ushort)SpirvOp.UGreaterThan));
        Assert.True(ContainsSpirvOpcode(shader.Spirv, (ushort)SpirvOp.LogicalOr));
        Assert.True(ContainsSpirvOpcode(shader.Spirv, (ushort)SpirvOp.Not));
        Assert.True(ContainsSpirvOpcode(shader.Spirv, (ushort)SpirvOp.BitwiseAnd));
        Assert.True(ContainsSpirvOpcode(shader.Spirv, (ushort)SpirvOp.BitwiseOr));
        Assert.True(ContainsSpirvOpcode(shader.Spirv, (ushort)SpirvOp.Select));
        Assert.True(ContainsSpirvConstant(shader.Spirv, 508));
    }

    [Fact]
    public void CompilesWrappingLdsAtomicToSpirv()
    {
        // Encoding assembled with LLVM 18 llvm-mc for gfx1030 and verified
        // with llvm-objdump. The atomic subtracts data0 when the old value is
        // at least data0; otherwise it adds data1.
        var ctx = CreateContext(
        [
            0xD8D001FCu, 0x01040302u,
            SEndpgm,
        ]);
        Assert.True(
            Gen5ShaderTranslator.TryDecodeProgram(
                ctx,
                CodeAddress,
                out var program,
                out var decodeError),
            decodeError);
        var instruction = Assert.Single(program.Instructions, instruction =>
            instruction.Opcode == "DsWrapRtnB32");
        Assert.Equal(
            [Gen5Operand.Vector(2), Gen5Operand.Vector(3), Gen5Operand.Vector(4)],
            instruction.Sources);
        Assert.Equal(Gen5Operand.Vector(1), Assert.Single(instruction.Destinations));

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
        Assert.Equal(1, CountSpirvOpcode(shader.Spirv, (ushort)SpirvOp.AtomicLoad));
        Assert.Equal(
            1,
            CountSpirvOpcode(shader.Spirv, (ushort)SpirvOp.AtomicCompareExchange));
        Assert.Equal(2, CountSpirvOpcode(shader.Spirv, (ushort)SpirvOp.LoopMerge));
        Assert.True(ContainsSpirvOpcode(
            shader.Spirv,
            (ushort)SpirvOp.UGreaterThanEqual));
        Assert.True(ContainsSpirvOpcode(shader.Spirv, (ushort)SpirvOp.ISub));
        Assert.True(ContainsSpirvOpcode(shader.Spirv, (ushort)SpirvOp.IAdd));
        Assert.True(ContainsSpirvOpcode(shader.Spirv, (ushort)SpirvOp.Select));
        Assert.True(ContainsSpirvConstant(shader.Spirv, 508));
    }

    [Fact]
    public void CompilesFloatLdsAtomic32OperationsToSpirv()
    {
        // Encodings assembled with LLVM 18 llvm-mc for gfx1030 and verified
        // with llvm-objdump. SPIR-V has no core float LDS atomics, so the
        // operations use raw-word compare/exchange loops around float math.
        var ctx = CreateContext(
        [
            0xD84401FCu, 0x00030201u,
            0xD84801FCu, 0x00000504u,
            0xD84C01FCu, 0x00000706u,
            0xD85401FCu, 0x00000908u,
            0xD8C401FCu, 0x0A0D0C0Bu,
            0xD8C801FCu, 0x0E00100Fu,
            0xD8CC01FCu, 0x11001312u,
            0xD95401FCu, 0x14001615u,
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
                "DsCmpstF32", "DsMinF32", "DsMaxF32", "DsAddF32",
                "DsCmpstRtnF32", "DsMinRtnF32", "DsMaxRtnF32",
                "DsAddRtnF32", "SEndpgm",
            ],
            program.Instructions.Select(instruction => instruction.Opcode));
        Assert.Equal(3, program.Instructions[0].Sources.Count);
        Assert.All(
            program.Instructions.Skip(1).Take(3),
            instruction => Assert.Equal(2, instruction.Sources.Count));
        Assert.Equal(3, program.Instructions[4].Sources.Count);
        Assert.All(
            program.Instructions.Skip(4).Take(4),
            instruction => Assert.Single(instruction.Destinations));

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
        Assert.Equal(8, CountSpirvOpcode(shader.Spirv, (ushort)SpirvOp.AtomicLoad));
        Assert.Equal(
            8,
            CountSpirvOpcode(shader.Spirv, (ushort)SpirvOp.AtomicCompareExchange));
        Assert.Equal(9, CountSpirvOpcode(shader.Spirv, (ushort)SpirvOp.LoopMerge));
        Assert.True(ContainsSpirvOpcode(shader.Spirv, (ushort)SpirvOp.FOrdEqual));
        Assert.True(ContainsSpirvOpcode(
            shader.Spirv,
            (ushort)SpirvOp.FOrdLessThan));
        Assert.True(ContainsSpirvOpcode(
            shader.Spirv,
            (ushort)SpirvOp.FOrdGreaterThan));
        Assert.True(ContainsSpirvOpcode(shader.Spirv, (ushort)SpirvOp.FAdd));
        Assert.True(ContainsSpirvOpcode(shader.Spirv, (ushort)SpirvOp.Bitcast));
        Assert.True(ContainsSpirvOpcode(shader.Spirv, (ushort)SpirvOp.Select));
        Assert.True(ContainsSpirvConstant(shader.Spirv, 508));
    }

    [Fact]
    public void CompilesLdsBackwardPermuteToSubgroupShuffle()
    {
        // Encoding assembled with LLVM 18 llvm-mc for gfx1030 and verified
        // with llvm-objdump. The offset deliberately exercises both DS offset
        // bytes; only address bits [6:2] select the source wave lane.
        var ctx = CreateContext(
        [
            0xDACC01FCu, 0x03000504u, // ds_bpermute_b32 v3, v4, v5 offset:508
            SEndpgm,
        ]);
        Assert.True(
            Gen5ShaderTranslator.TryDecodeProgram(
                ctx,
                CodeAddress,
                out var program,
                out var decodeError),
            decodeError);
        var instruction = Assert.Single(program.Instructions, instruction =>
            instruction.Opcode == "DsBpermuteB32");
        Assert.Equal(
            [Gen5Operand.Vector(4), Gen5Operand.Vector(5)],
            instruction.Sources);
        Assert.Equal(Gen5Operand.Vector(3), Assert.Single(instruction.Destinations));
        var control = Assert.IsType<Gen5DataShareControl>(instruction.Control);
        Assert.Equal(0xFCu, control.Offset0);
        Assert.Equal(0x01u, control.Offset1);

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
        Assert.True(ContainsSpirvCapability(
            shader.Spirv,
            SpirvCapability.GroupNonUniformShuffle));
        Assert.True(ContainsSpirvOpcode(
            shader.Spirv,
            (ushort)SpirvOp.GroupNonUniformShuffle));
        Assert.True(ContainsSpirvOpcode(shader.Spirv, (ushort)SpirvOp.IAdd));
        Assert.True(ContainsSpirvOpcode(
            shader.Spirv,
            (ushort)SpirvOp.ShiftRightLogical));
        Assert.True(ContainsSpirvOpcode(shader.Spirv, (ushort)SpirvOp.BitwiseAnd));
        Assert.True(ContainsSpirvOpcode(shader.Spirv, (ushort)SpirvOp.Select));
        Assert.True(ContainsSpirvConstant(shader.Spirv, 508));
        Assert.False(ContainsSpirvVariable(
            shader.Spirv,
            SpirvStorageClass.Workgroup));
    }

    [Fact]
    public void CompilesLdsForwardPermuteToDeterministicSubgroupScatter()
    {
        // Encoding assembled with LLVM 18 llvm-mc for gfx1030 and verified
        // with llvm-objdump. Forward permute resolves collisions in favor of
        // the highest active source lane and produces zero for unwritten lanes.
        var ctx = CreateContext(
        [
            0xDAC801FCu, 0x03000504u, // ds_permute_b32 v3, v4, v5 offset:508
            SEndpgm,
        ]);
        Assert.True(
            Gen5ShaderTranslator.TryDecodeProgram(
                ctx,
                CodeAddress,
                out var program,
                out var decodeError),
            decodeError);
        var instruction = Assert.Single(program.Instructions, instruction =>
            instruction.Opcode == "DsPermuteB32");
        Assert.Equal(
            [Gen5Operand.Vector(4), Gen5Operand.Vector(5)],
            instruction.Sources);
        Assert.Equal(Gen5Operand.Vector(3), Assert.Single(instruction.Destinations));
        var control = Assert.IsType<Gen5DataShareControl>(instruction.Control);
        Assert.Equal(0xFCu, control.Offset0);
        Assert.Equal(0x01u, control.Offset1);

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
        Assert.True(ContainsSpirvCapability(
            shader.Spirv,
            SpirvCapability.GroupNonUniformShuffle));
        Assert.Equal(
            33,
            CountSpirvOpcode(
                shader.Spirv,
                (ushort)SpirvOp.GroupNonUniformShuffle));
        Assert.True(ContainsSpirvOpcode(shader.Spirv, (ushort)SpirvOp.IEqual));
        Assert.True(ContainsSpirvOpcode(shader.Spirv, (ushort)SpirvOp.Select));
        Assert.True(ContainsSpirvConstant(shader.Spirv, 508));
        Assert.True(ContainsSpirvConstant(shader.Spirv, 32));
        Assert.False(ContainsSpirvVariable(
            shader.Spirv,
            SpirvStorageClass.Workgroup));
    }

    [Fact]
    public void CompilesLdsAddTidTransfersWithM0AndLaneAddressing()
    {
        // Encodings assembled with LLVM 18 llvm-mc for gfx1030 and verified
        // with llvm-objdump. ADD_TID uses DATA0 without an ADDR VGPR and forms
        // its byte address from M0, the immediate, and the wave lane ID.
        var ctx = CreateContext(
        [
            0xBEFC03C0u,              // s_mov_b32 m0, 64
            0xDAC001FCu, 0x00000500u, // ds_write_addtid_b32 v5 offset:508
            0xDAC401FCu, 0x03000000u, // ds_read_addtid_b32 v3 offset:508
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
            ["SMovB32", "DsWriteAddtidB32", "DsReadAddtidB32", "SEndpgm"],
            program.Instructions.Select(instruction => instruction.Opcode));
        Assert.Equal(
            Gen5Operand.Vector(5),
            Assert.Single(program.Instructions[1].Sources));
        Assert.Empty(program.Instructions[1].Destinations);
        Assert.Empty(program.Instructions[2].Sources);
        Assert.Equal(
            Gen5Operand.Vector(3),
            Assert.Single(program.Instructions[2].Destinations));
        Assert.All(
            program.Instructions.Skip(1).Take(2),
            instruction =>
            {
                var control = Assert.IsType<Gen5DataShareControl>(instruction.Control);
                Assert.Equal(0xFCu, control.Offset0);
                Assert.Equal(0x01u, control.Offset1);
            });

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
        Assert.True(ContainsSpirvCapability(
            shader.Spirv,
            SpirvCapability.GroupNonUniform));
        Assert.True(ContainsSpirvOpcode(shader.Spirv, (ushort)SpirvOp.IAdd));
        Assert.True(ContainsSpirvOpcode(
            shader.Spirv,
            (ushort)SpirvOp.ShiftLeftLogical));
        Assert.True(ContainsSpirvConstant(shader.Spirv, 64));
        Assert.True(ContainsSpirvConstant(shader.Spirv, 508));
        Assert.True(ContainsSpirvVariable(
            shader.Spirv,
            SpirvStorageClass.Workgroup));
    }

    [Fact]
    public void CompilesAllLdsSwizzleModesToSubgroupShuffle()
    {
        // Encodings assembled with LLVM 18 llvm-mc for gfx1030 and verified
        // with llvm-objdump. These cover quad, bitmask, rotate-left,
        // rotate-right, and FFT swizzle modes respectively.
        var ctx = CreateContext(
        [
            0xD8D480B1u, 0x00000001u,
            0xD8D4041Fu, 0x02000003u,
            0xD8D4C020u, 0x04000005u,
            0xD8D4C420u, 0x06000007u,
            0xD8D4E010u, 0x08000009u,
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
                "DsSwizzleB32",
                "DsSwizzleB32",
                "DsSwizzleB32",
                "DsSwizzleB32",
                "DsSwizzleB32",
            ],
            program.Instructions.Take(5).Select(instruction => instruction.Opcode));
        Assert.Equal(
            [1u, 3u, 5u, 7u, 9u],
            program.Instructions.Take(5)
                .Select(instruction => Assert.Single(instruction.Sources).Value));
        Assert.Equal(
            [0u, 2u, 4u, 6u, 8u],
            program.Instructions.Take(5)
                .Select(instruction => Assert.Single(instruction.Destinations).Value));
        Assert.Equal(
            [0x80B1u, 0x041Fu, 0xC020u, 0xC420u, 0xE010u],
            program.Instructions.Take(5).Select(instruction =>
            {
                var control = Assert.IsType<Gen5DataShareControl>(instruction.Control);
                return control.Offset0 | (control.Offset1 << 8);
            }));

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
        Assert.True(ContainsSpirvCapability(
            shader.Spirv,
            SpirvCapability.GroupNonUniformShuffle));
        Assert.True(ContainsSpirvOpcode(
            shader.Spirv,
            (ushort)SpirvOp.GroupNonUniformShuffle));
        Assert.True(ContainsSpirvOpcode(shader.Spirv, (ushort)SpirvOp.BitReverse));
        Assert.True(ContainsSpirvOpcode(shader.Spirv, (ushort)SpirvOp.IAdd));
        Assert.True(ContainsSpirvOpcode(shader.Spirv, (ushort)SpirvOp.ISub));
        Assert.True(ContainsSpirvOpcode(
            shader.Spirv,
            (ushort)SpirvOp.ShiftRightLogical));
        Assert.True(ContainsSpirvOpcode(
            shader.Spirv,
            (ushort)SpirvOp.ShiftLeftLogical));
        Assert.True(ContainsSpirvOpcode(shader.Spirv, (ushort)SpirvOp.BitwiseAnd));
        Assert.True(ContainsSpirvOpcode(shader.Spirv, (ushort)SpirvOp.BitwiseOr));
        Assert.True(ContainsSpirvOpcode(shader.Spirv, (ushort)SpirvOp.BitwiseXor));
        Assert.True(ContainsSpirvOpcode(shader.Spirv, (ushort)SpirvOp.Select));
        Assert.False(ContainsSpirvVariable(
            shader.Spirv,
            SpirvStorageClass.Workgroup));
    }

    [Fact]
    public void CompilesLdsAtomic32OperationsToSpirv()
    {
        // Encodings assembled with LLVM 18 llvm-mc for gfx1030 and verified
        // with llvm-objdump.
        var ctx = CreateContext(
        [
            0xD8000004u, 0x00000100u, // ds_add_u32 v0, v1 offset:4
            0xD8040000u, 0x00000302u, // ds_sub_u32 v2, v3
            0xD8140000u, 0x00000504u, // ds_min_i32 v4, v5
            0xD8200000u, 0x00000706u, // ds_max_u32 v6, v7
            0xD8240000u, 0x00000908u, // ds_and_b32 v8, v9
            0xD8280000u, 0x00000B0Au, // ds_or_b32 v10, v11
            0xD82C0000u, 0x00000D0Cu, // ds_xor_b32 v12, v13
            0xD8800000u, 0x0E00100Fu, // ds_add_rtn_u32 v14, v15, v16
            0xD8840000u, 0x11001312u, // ds_sub_rtn_u32 v17, v18, v19
            0xD8940000u, 0x14001615u, // ds_min_rtn_i32 v20, v21, v22
            0xD8A00000u, 0x17001918u, // ds_max_rtn_u32 v23, v24, v25
            0xD8A40000u, 0x1A001C1Bu, // ds_and_rtn_b32 v26, v27, v28
            0xD8A80000u, 0x1D001F1Eu, // ds_or_rtn_b32 v29, v30, v31
            0xD8AC0000u, 0x20002221u, // ds_xor_rtn_b32 v32, v33, v34
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
                "DsAddU32",
                "DsSubU32",
                "DsMinI32",
                "DsMaxU32",
                "DsAndB32",
                "DsOrB32",
                "DsXorB32",
                "DsAddRtnU32",
                "DsSubRtnU32",
                "DsMinRtnI32",
                "DsMaxRtnU32",
                "DsAndRtnB32",
                "DsOrRtnB32",
                "DsXorRtnB32",
                "SEndpgm",
            ],
            program.Instructions.Select(instruction => instruction.Opcode));
        Assert.All(
            program.Instructions.Take(14),
            instruction => Assert.Equal(2, instruction.Sources.Count));
        Assert.All(
            program.Instructions.Take(7),
            instruction => Assert.Empty(instruction.Destinations));
        Assert.Equal(
            [14u, 17u, 20u, 23u, 26u, 29u, 32u],
            program.Instructions.Skip(7).Take(7)
                .Select(instruction => Assert.Single(instruction.Destinations).Value));
        Assert.Equal(
            4u,
            Assert.IsType<Gen5DataShareControl>(program.Instructions[0].Control).Offset0);
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
        Assert.True(ContainsSpirvOpcode(shader.Spirv, (ushort)SpirvOp.AtomicIAdd));
        Assert.True(ContainsSpirvOpcode(shader.Spirv, (ushort)SpirvOp.AtomicISub));
        Assert.True(ContainsSpirvOpcode(shader.Spirv, (ushort)SpirvOp.AtomicSMin));
        Assert.True(ContainsSpirvOpcode(shader.Spirv, (ushort)SpirvOp.AtomicUMax));
        Assert.True(ContainsSpirvOpcode(shader.Spirv, (ushort)SpirvOp.AtomicAnd));
        Assert.True(ContainsSpirvOpcode(shader.Spirv, (ushort)SpirvOp.AtomicOr));
        Assert.True(ContainsSpirvOpcode(shader.Spirv, (ushort)SpirvOp.AtomicXor));
    }

    [Fact]
    public void CompilesLdsSubwordAndRead64OperationsToSpirv()
    {
        // Encodings assembled with LLVM 18 llvm-mc for gfx1030 and verified
        // with llvm-objdump.
        var ctx = CreateContext(
        [
            0xD8780003u, 0x00000100u, // ds_write_b8 v0, v1 offset:3
            0xD87C0002u, 0x00000302u, // ds_write_b16 v2, v3 offset:2
            0xD8E80001u, 0x04000005u, // ds_read_u8 v4, v5 offset:1
            0xD8E40002u, 0x06000007u, // ds_read_i8 v6, v7 offset:2
            0xD8F00002u, 0x08000009u, // ds_read_u16 v8, v9 offset:2
            0xD8EC0000u, 0x0A00000Bu, // ds_read_i16 v10, v11
            0xD9D80004u, 0x0C00000Eu, // ds_read_b64 v[12:13], v14 offset:4
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
                "DsWriteB8",
                "DsWriteB16",
                "DsReadU8",
                "DsReadI8",
                "DsReadU16",
                "DsReadI16",
                "DsReadB64",
                "SEndpgm",
            ],
            program.Instructions.Select(instruction => instruction.Opcode));
        Assert.Equal(2, program.Instructions[0].Sources.Count);
        Assert.Equal(2, program.Instructions[1].Sources.Count);
        Assert.All(
            program.Instructions.Skip(2).Take(4),
            instruction => Assert.Single(instruction.Destinations));
        Assert.Equal(
            [Gen5Operand.Vector(12), Gen5Operand.Vector(13)],
            program.Instructions[6].Destinations);
        Assert.Equal(
            [3u, 2u, 1u, 2u, 2u, 0u, 4u],
            program.Instructions.Take(7)
                .Select(instruction =>
                    Assert.IsType<Gen5DataShareControl>(instruction.Control).Offset0));
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
        Assert.True(ContainsSpirvOpcode(shader.Spirv, (ushort)SpirvOp.BitFieldInsert));
        Assert.True(ContainsSpirvOpcode(shader.Spirv, (ushort)SpirvOp.BitFieldUExtract));
        Assert.True(ContainsSpirvOpcode(shader.Spirv, (ushort)SpirvOp.BitFieldSExtract));
        Assert.True(ContainsSpirvOpcode(shader.Spirv, (ushort)SpirvOp.Load));
        Assert.True(ContainsSpirvOpcode(shader.Spirv, (ushort)SpirvOp.Store));
    }

    [Fact]
    public void CompilesLdsD16SubwordOperationsToSpirv()
    {
        // Encodings assembled with LLVM 18 llvm-mc for gfx1030 and verified
        // with llvm-objdump.
        var ctx = CreateContext(
        [
            0xDA800101u, 0x00000100u, // ds_write_b8_d16_hi v0, v1 offset:257
            0xDA840102u, 0x00000302u, // ds_write_b16_d16_hi v2, v3 offset:258
            0xDA880103u, 0x04000005u, // ds_read_u8_d16 v4, v5 offset:259
            0xDA8C0104u, 0x06000007u, // ds_read_u8_d16_hi v6, v7 offset:260
            0xDA900105u, 0x08000009u, // ds_read_i8_d16 v8, v9 offset:261
            0xDA940106u, 0x0A00000Bu, // ds_read_i8_d16_hi v10, v11 offset:262
            0xDA980108u, 0x0C00000Du, // ds_read_u16_d16 v12, v13 offset:264
            0xDA9C010Au, 0x0E00000Fu, // ds_read_u16_d16_hi v14, v15 offset:266
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
                "DsWriteB8D16Hi",
                "DsWriteB16D16Hi",
                "DsReadU8D16",
                "DsReadU8D16Hi",
                "DsReadI8D16",
                "DsReadI8D16Hi",
                "DsReadU16D16",
                "DsReadU16D16Hi",
                "SEndpgm",
            ],
            program.Instructions.Select(instruction => instruction.Opcode));
        Assert.All(
            program.Instructions.Take(2),
            instruction => Assert.Equal(2, instruction.Sources.Count));
        Assert.Equal(
            [4u, 6u, 8u, 10u, 12u, 14u],
            program.Instructions.Skip(2).Take(6)
                .Select(instruction => Assert.Single(instruction.Destinations).Value));
        Assert.Equal(
            [257u, 258u, 259u, 260u, 261u, 262u, 264u, 266u],
            program.Instructions.Take(8).Select(instruction =>
            {
                var control = Assert.IsType<Gen5DataShareControl>(instruction.Control);
                return control.Offset0 | (control.Offset1 << 8);
            }));

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
        Assert.True(ContainsSpirvOpcode(
            shader.Spirv,
            (ushort)SpirvOp.BitFieldUExtract));
        Assert.True(ContainsSpirvOpcode(
            shader.Spirv,
            (ushort)SpirvOp.BitFieldSExtract));
        Assert.Equal(
            8,
            CountSpirvOpcode(shader.Spirv, (ushort)SpirvOp.BitFieldInsert));
        Assert.True(ContainsSpirvOpcode(
            shader.Spirv,
            (ushort)SpirvOp.ShiftRightLogical));
        Assert.True(ContainsSpirvOpcode(shader.Spirv, (ushort)SpirvOp.Load));
        Assert.True(ContainsSpirvOpcode(shader.Spirv, (ushort)SpirvOp.Store));
    }

    [Fact]
    public void CompilesWideLdsReadsAndWritesToSpirv()
    {
        // Encodings assembled with LLVM 18 llvm-mc for gfx1030 and verified
        // with llvm-objdump. All offsets exercise the upper single-address
        // offset byte.
        var ctx = CreateContext(
        [
            0xDB780104u, 0x00000100u, // ds_write_b96 v0, v[1:3] offset:260
            0xDB7C0108u, 0x00000504u, // ds_write_b128 v4, v[5:8] offset:264
            0xDBF8010Cu, 0x0900000Cu, // ds_read_b96 v[9:11], v12 offset:268
            0xDBFC0110u, 0x0D000011u, // ds_read_b128 v[13:16], v17 offset:272
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
            ["DsWriteB96", "DsWriteB128", "DsReadB96", "DsReadB128", "SEndpgm"],
            program.Instructions.Select(instruction => instruction.Opcode));
        Assert.Equal(
            [
                Gen5Operand.Vector(0),
                Gen5Operand.Vector(1),
                Gen5Operand.Vector(2),
                Gen5Operand.Vector(3),
            ],
            program.Instructions[0].Sources);
        Assert.Equal(
            [
                Gen5Operand.Vector(4),
                Gen5Operand.Vector(5),
                Gen5Operand.Vector(6),
                Gen5Operand.Vector(7),
                Gen5Operand.Vector(8),
            ],
            program.Instructions[1].Sources);
        Assert.Equal(
            [Gen5Operand.Vector(9), Gen5Operand.Vector(10), Gen5Operand.Vector(11)],
            program.Instructions[2].Destinations);
        Assert.Equal(
            [
                Gen5Operand.Vector(13),
                Gen5Operand.Vector(14),
                Gen5Operand.Vector(15),
                Gen5Operand.Vector(16),
            ],
            program.Instructions[3].Destinations);
        Assert.Equal(
            [260u, 264u, 268u, 272u],
            program.Instructions.Take(4).Select(instruction =>
            {
                var control = Assert.IsType<Gen5DataShareControl>(instruction.Control);
                return control.Offset0 | (control.Offset1 << 8);
            }));

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
        Assert.True(ContainsSpirvOpcode(shader.Spirv, (ushort)SpirvOp.Load));
        Assert.True(ContainsSpirvOpcode(shader.Spirv, (ushort)SpirvOp.Store));
        Assert.True(ContainsSpirvConstant(shader.Spirv, 260));
        Assert.True(ContainsSpirvConstant(shader.Spirv, 272));
    }

    [Fact]
    public void CompilesPaired64BitLdsOperationsToSpirv()
    {
        // Encodings assembled with LLVM 18 llvm-mc for gfx1030 and verified
        // with llvm-objdump.
        var ctx = CreateContext(
        [
            0xD9380502u, 0x00030100u, // ds_write2_b64 v0, v[1:2], v[3:4]
            0xD93C0301u, 0x00080605u, // ds_write2st64_b64 v5, v[6:7], v[8:9]
            0xD9DC0502u, 0x0A00000Eu, // ds_read2_b64 v[10:13], v14
            0xD9E00301u, 0x0F000013u, // ds_read2st64_b64 v[15:18], v19
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
                "DsWrite2B64",
                "DsWrite2St64B64",
                "DsRead2B64",
                "DsRead2St64B64",
                "SEndpgm",
            ],
            program.Instructions.Select(instruction => instruction.Opcode));
        Assert.Equal(
            [
                Gen5Operand.Vector(0),
                Gen5Operand.Vector(1),
                Gen5Operand.Vector(2),
                Gen5Operand.Vector(3),
                Gen5Operand.Vector(4),
            ],
            program.Instructions[0].Sources);
        Assert.Equal(
            [
                Gen5Operand.Vector(5),
                Gen5Operand.Vector(6),
                Gen5Operand.Vector(7),
                Gen5Operand.Vector(8),
                Gen5Operand.Vector(9),
            ],
            program.Instructions[1].Sources);
        Assert.Equal(
            [
                Gen5Operand.Vector(10),
                Gen5Operand.Vector(11),
                Gen5Operand.Vector(12),
                Gen5Operand.Vector(13),
            ],
            program.Instructions[2].Destinations);
        Assert.Equal(
            [
                Gen5Operand.Vector(15),
                Gen5Operand.Vector(16),
                Gen5Operand.Vector(17),
                Gen5Operand.Vector(18),
            ],
            program.Instructions[3].Destinations);
        Assert.Equal(
            [(2u, 5u), (1u, 3u), (2u, 5u), (1u, 3u)],
            program.Instructions.Take(4).Select(instruction =>
            {
                var control = Assert.IsType<Gen5DataShareControl>(instruction.Control);
                return (control.Offset0, control.Offset1);
            }));

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
        Assert.True(ContainsSpirvOpcode(shader.Spirv, (ushort)SpirvOp.Load));
        Assert.True(ContainsSpirvOpcode(shader.Spirv, (ushort)SpirvOp.Store));
        Assert.True(ContainsSpirvConstant(shader.Spirv, 16));
        Assert.True(ContainsSpirvConstant(shader.Spirv, 40));
        Assert.True(ContainsSpirvConstant(shader.Spirv, 512));
        Assert.True(ContainsSpirvConstant(shader.Spirv, 1536));
    }

    [Fact]
    public void CompilesExtendedInterpolationOperationsToSpirv()
    {
        // Encodings assembled with LLVM 18 llvm-mc for gfx1030 and verified
        // with llvm-objdump.
        var ctx = CreateContext(
        [
            0xD6000000u, 0x00020200u, // v_interp_p1_f32_e64 v0, v1, attr0.x
            0xD6010002u, 0x00020641u, // v_interp_p2_f32_e64 v2, v3, attr1.y
            0xD6020004u, 0x00000082u, // v_interp_mov_f32_e64 v4, p10, attr2.z
            0xD7420005u, 0x00020CC3u, // v_interp_p1ll_f16 v5, v6, attr3.w
            0xD7430007u, 0x04261004u, // v_interp_p1lv_f16 v7, v8, attr4.x, v9
            0xD75A000Au, 0x04321645u, // v_interp_p2_f16 v10, v11, attr5.y, v12
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
                "VInterpP1F32",
                "VInterpP2F32",
                "VInterpMovF32",
                "VInterpP1llF16",
                "VInterpP1lvF16",
                "VInterpP2F16",
                "SEndpgm",
            ],
            program.Instructions.Select(instruction => instruction.Opcode));
        uint[] destinations = [0, 2, 4, 5, 7, 10];
        for (var index = 0; index < destinations.Length; index++)
        {
            var interpolation = Assert.IsType<Gen5InterpolationControl>(
                program.Instructions[index].Control);
            Assert.Equal((uint)index, interpolation.Attribute);
            Assert.Equal((uint)(index & 3), interpolation.Channel);
            Assert.Equal(
                destinations[index],
                program.Instructions[index].Destinations[0].Value);
        }

        Assert.Equal(Gen5Operand.Vector(1), program.Instructions[0].Sources[0]);
        Assert.Equal(Gen5Operand.Vector(3), program.Instructions[1].Sources[0]);
        Assert.Equal(2, program.Instructions[4].Sources.Count);
        Assert.Equal(Gen5Operand.Vector(8), program.Instructions[4].Sources[0]);
        Assert.Equal(Gen5Operand.Vector(9), program.Instructions[4].Sources[1]);
        Assert.Equal(Gen5Operand.Vector(11), program.Instructions[5].Sources[0]);
        Assert.Equal(Gen5Operand.Vector(12), program.Instructions[5].Sources[1]);
        var state = new Gen5ShaderState(program, [], Metadata: null);
        Assert.True(
            Gen5ShaderScalarEvaluator.TryEvaluate(
                ctx,
                state,
                out var evaluation,
                out var evaluationError),
            evaluationError);

        Assert.True(
            Gen5SpirvTranslator.TryCompilePixelShader(
                state,
                evaluation,
                Gen5PixelOutputKind.Float,
                out var shader,
                out var compileError),
            compileError);
        Assert.Equal(6u, shader.AttributeCount);
        Assert.True(ContainsSpirvOpcode(shader.Spirv, (ushort)SpirvOp.CompositeExtract));
        Assert.True(ContainsSpirvOpcode(shader.Spirv, (ushort)SpirvOp.CompositeConstruct));
        Assert.True(ContainsSpirvOpcode(shader.Spirv, (ushort)SpirvOp.ExtInst));
        Assert.True(ContainsSpirvOpcode(shader.Spirv, (ushort)SpirvOp.BitwiseAnd));
        Assert.True(ContainsSpirvOpcode(shader.Spirv, (ushort)SpirvOp.Store));
    }

    [Fact]
    public void CompilesVectorSignedInteger32SaturatingOperationsToSpirv()
    {
        // Encodings assembled with LLVM 18 llvm-mc for gfx1030 and verified
        // with llvm-objdump.
        var ctx = CreateContext(
        [
            0xD77F0000u, 0x00020501u, // v_add_nc_i32 v0, v1, v2
            0xD77F8003u, 0x00020B04u, // v_add_nc_i32 v3, v4, v5 clamp
            0xD77F0006u, 0x00021007u, // v_add_nc_i32 v6, s7, v8
            0xD7760009u, 0x0002170Au, // v_sub_nc_i32 v9, v10, v11
            0xD776800Cu, 0x00021D0Du, // v_sub_nc_i32 v12, v13, v14 clamp
            0xD776000Fu, 0x00002310u, // v_sub_nc_i32 v15, v16, s17
            0xD77F8012u, 0x000226AAu, // v_add_nc_i32 v18, 42, v19 clamp
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
                "VAddNcI32",
                "VAddNcI32",
                "VAddNcI32",
                "VSubNcI32",
                "VSubNcI32",
                "VSubNcI32",
                "VAddNcI32",
                "SEndpgm",
            ],
            program.Instructions.Select(instruction => instruction.Opcode));
        Assert.False(Assert.IsType<Gen5Vop3Control>(program.Instructions[0].Control).Clamp);
        Assert.True(Assert.IsType<Gen5Vop3Control>(program.Instructions[1].Control).Clamp);
        Assert.Equal(
            Gen5OperandKind.ScalarRegister,
            program.Instructions[2].Sources[0].Kind);
        Assert.True(Assert.IsType<Gen5Vop3Control>(program.Instructions[4].Control).Clamp);
        Assert.Equal(
            Gen5OperandKind.ScalarRegister,
            program.Instructions[5].Sources[1].Kind);
        Assert.Equal(
            Gen5OperandKind.EncodedConstant,
            program.Instructions[6].Sources[0].Kind);
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
        Assert.True(ContainsSpirvOpcode(shader.Spirv, (ushort)SpirvOp.IAdd));
        Assert.True(ContainsSpirvOpcode(shader.Spirv, (ushort)SpirvOp.ISub));
        Assert.True(ContainsSpirvOpcode(shader.Spirv, (ushort)SpirvOp.SConvert));
        Assert.True(ContainsSpirvOpcode(shader.Spirv, (ushort)SpirvOp.SLessThan));
        Assert.True(ContainsSpirvOpcode(shader.Spirv, (ushort)SpirvOp.SGreaterThan));
        Assert.True(ContainsSpirvOpcode(shader.Spirv, (ushort)SpirvOp.Select));
    }

    [Fact]
    public void CompilesVectorLegacyFusedMultiplyAddToSpirv()
    {
        // Encodings assembled with LLVM 18 llvm-mc for gfx1030 and verified
        // with llvm-objdump.
        var ctx = CreateContext(
        [
            0xD5400000u, 0x040E0501u, // v_fma_legacy_f32 v0, v1, v2, v3
            0xD5408304u, 0xAC1E0D05u, // v_fma_legacy_f32 v4, -|v5|, |v6|, -v7 clamp mul:2
            0xD5400008u, 0x042E1409u, // v_fma_legacy_f32 v8, s9, v10, v11
            0xD540000Cu, 0x043C1D0Du, // v_fma_legacy_f32 v12, v13, s14, v15
            0xD5400010u, 0x004E2511u, // v_fma_legacy_f32 v16, v17, v18, s19
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
                "VFmaLegacyF32",
                "VFmaLegacyF32",
                "VFmaLegacyF32",
                "VFmaLegacyF32",
                "VFmaLegacyF32",
                "SEndpgm",
            ],
            program.Instructions.Select(instruction => instruction.Opcode));
        var modifiedControl = Assert.IsType<Gen5Vop3Control>(program.Instructions[1].Control);
        Assert.Equal(3u, modifiedControl.AbsoluteMask);
        Assert.Equal(5u, modifiedControl.NegateMask);
        Assert.True(modifiedControl.Clamp);
        Assert.Equal(1u, modifiedControl.OutputModifier);
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
        Assert.True(ContainsSpirvOpcode(shader.Spirv, (ushort)SpirvOp.FAdd));
        Assert.True(ContainsSpirvOpcode(shader.Spirv, (ushort)SpirvOp.FMul));
        Assert.True(ContainsSpirvOpcode(shader.Spirv, (ushort)SpirvOp.FNegate));
        Assert.True(ContainsSpirvOpcode(shader.Spirv, (ushort)SpirvOp.IEqual));
        Assert.True(ContainsSpirvOpcode(shader.Spirv, (ushort)SpirvOp.LogicalOr));
        Assert.True(ContainsSpirvOpcode(shader.Spirv, (ushort)SpirvOp.Select));
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
        Assert.Equal(
            [106u, 106u, 106u, 126u, 126u, 126u, 126u],
            program.Instructions
                .Skip(5)
                .Take(7)
                .Select(instruction => Assert.Single(instruction.Destinations).Value));
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
    public void CompilesVectorFloat16UnaryOperationsToSpirv()
    {
        // Encodings assembled with LLVM 18 llvm-mc for gfx1030 and verified
        // with llvm-objdump. The E64 forms exercise source modifiers and F16
        // output clamp/multiplier handling in addition to the compact forms.
        var ctx = CreateContext(
        [
            0x7E34A91Bu,              // v_rcp_f16_e32 v26, v27
            0x7E3CAB1Fu,              // v_sqrt_f16_e32 v30, v31
            0x7E38AD1Du,              // v_rsq_f16_e32 v28, v29
            0x7E30AF19u,              // v_log_f16_e32 v24, v25
            0x7E2CB117u,              // v_exp_f16_e32 v22, v23
            0x7E4CB327u,              // v_frexp_mant_f16_e32 v38, v39
            0x7E48B525u,              // v_frexp_exp_i16_f16_e32 v36, v37
            0x7E24B713u,              // v_floor_f16_e32 v18, v19
            0x7E20B911u,              // v_ceil_f16_e32 v16, v17
            0x7E1CBB0Fu,              // v_trunc_f16_e32 v14, v15
            0x7E28BD15u,              // v_rndne_f16_e32 v20, v21
            0x7E18BF0Du,              // v_fract_f16_e32 v12, v13
            0x7E40C121u,              // v_sin_f16_e32 v32, v33
            0x7E44C323u,              // v_cos_f16_e32 v34, v35
            0xD5D58128u, 0x08000129u, // v_sqrt_f16_e64 v40, |v41| clamp mul:2
            0xD5DF002Au, 0x2000012Bu, // v_fract_f16_e64 v42, -v43
            0xD5DA002Cu, 0x0000012Du, // v_frexp_exp_i16_f16_e64 v44, v45
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
                "VRcpF16", "VSqrtF16", "VRsqF16", "VLogF16", "VExpF16",
                "VFrexpMantF16", "VFrexpExpI16F16", "VFloorF16", "VCeilF16",
                "VTruncF16", "VRndneF16", "VFractF16", "VSinF16", "VCosF16",
                "VSqrtF16", "VFractF16", "VFrexpExpI16F16", "SEndpgm",
            ],
            program.Instructions.Select(instruction => instruction.Opcode));
        Assert.Equal(
            [26u, 30u, 28u, 24u, 22u, 38u, 36u, 18u, 16u, 14u, 20u, 12u,
             32u, 34u, 40u, 42u, 44u],
            program.Instructions
                .Take(17)
                .Select(instruction => Assert.Single(instruction.Destinations).Value));

        var modified = Assert.IsType<Gen5Vop3Control>(program.Instructions[14].Control);
        Assert.Equal(1u, modified.AbsoluteMask);
        Assert.Equal(1u, modified.OutputModifier);
        Assert.True(modified.Clamp);
        Assert.Equal(
            1u,
            Assert.IsType<Gen5Vop3Control>(program.Instructions[15].Control).NegateMask);

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
        Assert.False(ContainsSpirvCapability(shader.Spirv, SpirvCapability.Float16));
        Assert.True(ContainsGlslExtInst(shader.Spirv, 2));
        Assert.True(ContainsGlslExtInst(shader.Spirv, 3));
        Assert.True(ContainsGlslExtInst(shader.Spirv, 4));
        Assert.True(ContainsGlslExtInst(shader.Spirv, 8));
        Assert.True(ContainsGlslExtInst(shader.Spirv, 9));
        Assert.True(ContainsGlslExtInst(shader.Spirv, 10));
        Assert.True(ContainsGlslExtInst(shader.Spirv, 13));
        Assert.True(ContainsGlslExtInst(shader.Spirv, 14));
        Assert.True(ContainsGlslExtInst(shader.Spirv, 29));
        Assert.True(ContainsGlslExtInst(shader.Spirv, 30));
        Assert.True(ContainsGlslExtInst(shader.Spirv, 31));
        Assert.True(ContainsGlslExtInst(shader.Spirv, 32));
        Assert.True(ContainsGlslExtInst(shader.Spirv, 43));
        Assert.True(ContainsGlslExtInst(shader.Spirv, 52));
        Assert.True(ContainsGlslExtInst(shader.Spirv, 58));
        Assert.True(ContainsGlslExtInst(shader.Spirv, 62));
        Assert.True(ContainsSpirvOpcode(shader.Spirv, (ushort)SpirvOp.FDiv));
        Assert.True(ContainsSpirvOpcode(shader.Spirv, (ushort)SpirvOp.FMul));
        Assert.True(ContainsSpirvOpcode(shader.Spirv, (ushort)SpirvOp.FNegate));
        Assert.True(ContainsSpirvOpcode(shader.Spirv, (ushort)SpirvOp.CompositeExtract));
        Assert.True(ContainsSpirvOpcode(shader.Spirv, (ushort)SpirvOp.BitwiseAnd));
    }

    [Fact]
    public void CompilesVectorFloat16IntegerConversionsToSpirv()
    {
        // Encodings assembled with LLVM 18 llvm-mc for gfx1030 and verified
        // with llvm-objdump. The E64 forms also cover source modifiers and
        // F16 output clamp/multiplier handling.
        var ctx = CreateContext(
        [
            0x7E00A701u,              // v_cvt_i16_f16_e32 v0, v1
            0x7E04A503u,              // v_cvt_u16_f16_e32 v2, v3
            0x7E08A305u,              // v_cvt_f16_i16_e32 v4, v5
            0x7E0CA107u,              // v_cvt_f16_u16_e32 v6, v7
            0x7E10C709u,              // v_cvt_norm_i16_f16_e32 v8, v9
            0x7E14C90Bu,              // v_cvt_norm_u16_f16_e32 v10, v11
            0xD5D08014u, 0x08000115u, // v_cvt_f16_u16_e64 v20, v21 clamp mul:2
            0xD5D10016u, 0x00000117u, // v_cvt_f16_i16_e64 v22, v23
            0xD5D20118u, 0x00000119u, // v_cvt_u16_f16_e64 v24, |v25|
            0xD5D3001Au, 0x2000011Bu, // v_cvt_i16_f16_e64 v26, -v27
            0xD5E3001Cu, 0x0000011Du, // v_cvt_norm_i16_f16_e64 v28, v29
            0xD5E4001Eu, 0x0000011Fu, // v_cvt_norm_u16_f16_e64 v30, v31
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
                "VCvtI16F16", "VCvtU16F16", "VCvtF16I16", "VCvtF16U16",
                "VCvtNormI16F16", "VCvtNormU16F16", "VCvtF16U16", "VCvtF16I16",
                "VCvtU16F16", "VCvtI16F16", "VCvtNormI16F16", "VCvtNormU16F16",
                "SEndpgm",
            ],
            program.Instructions.Select(instruction => instruction.Opcode));
        Assert.Equal(
            [0u, 2u, 4u, 6u, 8u, 10u, 20u, 22u, 24u, 26u, 28u, 30u],
            program.Instructions
                .Take(12)
                .Select(instruction => Assert.Single(instruction.Destinations).Value));

        var modified = Assert.IsType<Gen5Vop3Control>(program.Instructions[6].Control);
        Assert.Equal(1u, modified.OutputModifier);
        Assert.True(modified.Clamp);
        Assert.Equal(
            1u,
            Assert.IsType<Gen5Vop3Control>(program.Instructions[8].Control).AbsoluteMask);
        Assert.Equal(
            1u,
            Assert.IsType<Gen5Vop3Control>(program.Instructions[9].Control).NegateMask);

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
        Assert.False(ContainsSpirvCapability(shader.Spirv, SpirvCapability.Float16));
        Assert.True(ContainsGlslExtInst(shader.Spirv, 4));
        Assert.True(ContainsGlslExtInst(shader.Spirv, 43));
        Assert.True(ContainsGlslExtInst(shader.Spirv, 56));
        Assert.True(ContainsGlslExtInst(shader.Spirv, 57));
        Assert.True(ContainsGlslExtInst(shader.Spirv, 58));
        Assert.True(ContainsGlslExtInst(shader.Spirv, 62));
        Assert.True(ContainsSpirvOpcode(shader.Spirv, (ushort)SpirvOp.ConvertFToS));
        Assert.True(ContainsSpirvOpcode(shader.Spirv, (ushort)SpirvOp.ConvertFToU));
        Assert.True(ContainsSpirvOpcode(shader.Spirv, (ushort)SpirvOp.ConvertSToF));
        Assert.True(ContainsSpirvOpcode(shader.Spirv, (ushort)SpirvOp.ConvertUToF));
        Assert.True(ContainsSpirvOpcode(shader.Spirv, (ushort)SpirvOp.BitFieldSExtract));
        Assert.True(ContainsSpirvOpcode(shader.Spirv, (ushort)SpirvOp.BitFieldUExtract));
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
            0xD5680030u, 0x00026932u, // v_ldexp_f64 v[48:49], v[50:51], v52
            0xD5688136u, 0x28027538u, // v_ldexp_f64 v[54:55], -|v[56:57]|, v58 clamp mul:2
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
                "VLdexpF64",
                "VLdexpF64",
                "SEndpgm",
            ],
            program.Instructions.Select(instruction => instruction.Opcode));
        var modifiedLdexp = Assert.IsType<Gen5Vop3Control>(
            program.Instructions[9].Control);
        Assert.Equal(1u, modifiedLdexp.AbsoluteMask);
        Assert.Equal(1u, modifiedLdexp.NegateMask);
        Assert.Equal(1u, modifiedLdexp.OutputModifier);
        Assert.True(modifiedLdexp.Clamp);
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
        Assert.True(ContainsGlslExtInst(shader.Spirv, 53));
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
    public void CompilesVectorFloat64ComparisonsToSpirv()
    {
        // The compact opcode blocks and E64 examples were verified with
        // LLVM 18 llvm-mc for gfx1030. Opcode 0x3f is unsupported on GFX10.
        var compactOpcodes = Enumerable
            .Range(0x20, 0x10)
            .Concat(Enumerable.Range(0x30, 0x0F))
            .Select(opcode => (uint)opcode)
            .ToArray();
        var instructionWords = compactOpcodes
            .Select(opcode => 0x7C000000u | (opcode << 17) | (2u << 9) | 0x100u)
            .Concat(
            [
                0xD4220108u, 0x20021508u, // v_cmp_eq_f64 s8, -|v[8:9]|, v[10:11]
                0xD438007Eu, 0x00021D0Cu, // v_cmpx_u_f64 v[12:13], v[14:15]
                SEndpgm,
            ])
            .ToArray();
        var ctx = CreateContext(instructionWords);
        Assert.True(
            Gen5ShaderTranslator.TryDecodeProgram(
                ctx,
                CodeAddress,
                out var program,
                out var decodeError),
            decodeError);
        Assert.Equal(
            [
                "VCmpFF64", "VCmpLtF64", "VCmpEqF64", "VCmpLeF64",
                "VCmpGtF64", "VCmpLgF64", "VCmpGeF64", "VCmpOF64",
                "VCmpUF64", "VCmpNgeF64", "VCmpNlgF64", "VCmpNgtF64",
                "VCmpNleF64", "VCmpNeqF64", "VCmpNltF64", "VCmpTruF64",
                "VCmpxFF64", "VCmpxLtF64", "VCmpxEqF64", "VCmpxLeF64",
                "VCmpxGtF64", "VCmpxLgF64", "VCmpxGeF64", "VCmpxOF64",
                "VCmpxUF64", "VCmpxNgeF64", "VCmpxNlgF64", "VCmpxNgtF64",
                "VCmpxNleF64", "VCmpxNeqF64", "VCmpxNltF64",
                "VCmpEqF64", "VCmpxUF64", "SEndpgm",
            ],
            program.Instructions.Select(instruction => instruction.Opcode));
        Assert.Equal(
            [.. Enumerable.Repeat(106u, 16), .. Enumerable.Repeat(126u, 15), 8u, 126u],
            program.Instructions
                .Take(33)
                .Select(instruction => Assert.Single(instruction.Destinations).Value));
        Assert.All(
            program.Instructions.Take(33),
            instruction => Assert.Equal(
                Gen5OperandKind.ScalarRegister,
                Assert.Single(instruction.Destinations).Kind));

        var modifiedControl = Assert.IsType<Gen5Vop3Control>(program.Instructions[31].Control);
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
        Assert.True(ContainsSpirvCapability(shader.Spirv, SpirvCapability.Float64));
        Assert.True(ContainsSpirvOpcode(shader.Spirv, (ushort)SpirvOp.FOrdLessThan));
        Assert.True(ContainsSpirvOpcode(shader.Spirv, (ushort)SpirvOp.FUnordEqual));
        Assert.True(ContainsSpirvOpcode(shader.Spirv, (ushort)SpirvOp.IsNan));
        Assert.True(ContainsSpirvOpcode(shader.Spirv, (ushort)SpirvOp.FNegate));
        Assert.True(ContainsSpirvOpcode(shader.Spirv, (ushort)SpirvOp.LogicalAnd));
    }

    [Fact]
    public void CompilesVectorFloat16ComparisonsToSpirv()
    {
        // The compact opcode blocks and E64 examples were verified with
        // LLVM 18 llvm-mc for gfx1030. The true predicate slots are invalid.
        var compactOpcodes = Enumerable
            .Range(0xC8, 8)
            .Concat(Enumerable.Range(0xD8, 8))
            .Concat(Enumerable.Range(0xE8, 7))
            .Concat(Enumerable.Range(0xF8, 7))
            .Select(opcode => (uint)opcode)
            .ToArray();
        var instructionWords = compactOpcodes
            .Select(opcode => 0x7C000000u | (opcode << 17) | (2u << 9) | 0x100u)
            .Concat(
            [
                0xD4CA0108u, 0x20021508u, // v_cmp_eq_f16 s8, -|v8|, v10
                0xD4F8007Eu, 0x00021D0Cu, // v_cmpx_u_f16 v12, v14
                SEndpgm,
            ])
            .ToArray();
        var ctx = CreateContext(instructionWords);
        Assert.True(
            Gen5ShaderTranslator.TryDecodeProgram(
                ctx,
                CodeAddress,
                out var program,
                out var decodeError),
            decodeError);
        Assert.Equal(
            [
                "VCmpFF16", "VCmpLtF16", "VCmpEqF16", "VCmpLeF16",
                "VCmpGtF16", "VCmpLgF16", "VCmpGeF16", "VCmpOF16",
                "VCmpxFF16", "VCmpxLtF16", "VCmpxEqF16", "VCmpxLeF16",
                "VCmpxGtF16", "VCmpxLgF16", "VCmpxGeF16", "VCmpxOF16",
                "VCmpUF16", "VCmpNgeF16", "VCmpNlgF16", "VCmpNgtF16",
                "VCmpNleF16", "VCmpNeqF16", "VCmpNltF16",
                "VCmpxUF16", "VCmpxNgeF16", "VCmpxNlgF16", "VCmpxNgtF16",
                "VCmpxNleF16", "VCmpxNeqF16", "VCmpxNltF16",
                "VCmpEqF16", "VCmpxUF16", "SEndpgm",
            ],
            program.Instructions.Select(instruction => instruction.Opcode));
        Assert.Equal(
            [
                .. Enumerable.Repeat(106u, 8), .. Enumerable.Repeat(126u, 8),
                .. Enumerable.Repeat(106u, 7), .. Enumerable.Repeat(126u, 7),
                8u, 126u,
            ],
            program.Instructions
                .Take(32)
                .Select(instruction => Assert.Single(instruction.Destinations).Value));

        var modifiedControl = Assert.IsType<Gen5Vop3Control>(program.Instructions[30].Control);
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
        Assert.False(ContainsSpirvCapability(shader.Spirv, SpirvCapability.Float16));
        Assert.True(ContainsGlslExtInst(shader.Spirv, 62));
        Assert.True(ContainsSpirvOpcode(shader.Spirv, (ushort)SpirvOp.FOrdLessThan));
        Assert.True(ContainsSpirvOpcode(shader.Spirv, (ushort)SpirvOp.FUnordEqual));
        Assert.True(ContainsSpirvOpcode(shader.Spirv, (ushort)SpirvOp.IsNan));
        Assert.True(ContainsSpirvOpcode(shader.Spirv, (ushort)SpirvOp.FNegate));
        Assert.True(ContainsSpirvOpcode(shader.Spirv, (ushort)SpirvOp.LogicalAnd));
    }

    [Fact]
    public void CompilesVectorFloatClassComparisonsToSpirv()
    {
        // Encodings assembled with LLVM 18 llvm-mc for gfx1030. This covers
        // compact and E64 F16/F64 forms plus E64 source modifiers.
        var ctx = CreateContext(
        [
            0x7D1E0500u,                         // v_cmp_class_f16_e32 v0, v2
            0x7D3E0D04u,                         // v_cmpx_class_f16_e32 v4, v6
            0xD48F0008u, 0x00021508u,            // v_cmp_class_f16_e64 s8, v8, v10
            0xD49F007Eu, 0x00021D0Cu,            // v_cmpx_class_f16_e64 v12, v14
            0x7D500500u,                         // v_cmp_class_f64_e32 v[0:1], v2
            0x7D700D04u,                         // v_cmpx_class_f64_e32 v[4:5], v6
            0xD4A80008u, 0x00020D04u,            // v_cmp_class_f64_e64 s8, v[4:5], v6
            0xD4B8007Eu, 0x00021508u,            // v_cmpx_class_f64_e64 v[8:9], v10
            0xD488010Au, 0x20021D0Cu,            // v_cmp_class_f32 s10, -|v12|, v14
            0xD4A8010Eu, 0x20022D14u,            // v_cmp_class_f64 s14, -|v[20:21]|, v22
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
                "VCmpClassF16", "VCmpxClassF16",
                "VCmpClassF16", "VCmpxClassF16",
                "VCmpClassF64", "VCmpxClassF64",
                "VCmpClassF64", "VCmpxClassF64",
                "VCmpClassF32", "VCmpClassF64", "SEndpgm",
            ],
            program.Instructions.Select(instruction => instruction.Opcode));
        Assert.Equal(
            [106u, 126u, 8u, 126u, 106u, 126u, 8u, 126u, 10u, 14u],
            program.Instructions
                .Take(10)
                .Select(instruction => Assert.Single(instruction.Destinations).Value));
        Assert.All(
            program.Instructions.Take(10),
            instruction => Assert.Equal(2, instruction.Sources.Count));
        Assert.All(
            program.Instructions.Skip(8).Take(2),
            instruction =>
            {
                var control = Assert.IsType<Gen5Vop3Control>(instruction.Control);
                Assert.Equal(1u, control.AbsoluteMask);
                Assert.Equal(1u, control.NegateMask);
            });

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
        Assert.True(ContainsSpirvCapability(shader.Spirv, SpirvCapability.Int64));
        Assert.True(ContainsSpirvOpcode(shader.Spirv, (ushort)SpirvOp.BitFieldUExtract));
        Assert.True(ContainsSpirvOpcode(shader.Spirv, (ushort)SpirvOp.BitwiseAnd));
        Assert.True(ContainsSpirvOpcode(shader.Spirv, (ushort)SpirvOp.BitwiseXor));
        Assert.True(ContainsSpirvOpcode(shader.Spirv, (ushort)SpirvOp.IEqual));
        Assert.True(ContainsSpirvOpcode(shader.Spirv, (ushort)SpirvOp.INotEqual));
        Assert.True(ContainsSpirvOpcode(shader.Spirv, (ushort)SpirvOp.LogicalAnd));
        Assert.True(ContainsSpirvOpcode(shader.Spirv, (ushort)SpirvOp.LogicalOr));
    }

    [Fact]
    public void CompilesVectorInteger64ComparisonsToSpirv()
    {
        // The compact opcode blocks and E64 examples were verified with
        // LLVM 18 llvm-mc for gfx1030.
        var compactOpcodes = Enumerable
            .Range(0xA0, 8)
            .Concat(Enumerable.Range(0xB0, 8))
            .Concat(Enumerable.Range(0xE0, 8))
            .Concat(Enumerable.Range(0xF0, 8))
            .Select(opcode => (uint)opcode)
            .ToArray();
        var instructionWords = compactOpcodes
            .Select(opcode => 0x7C000000u | (opcode << 17) | (2u << 9) | 0x100u)
            .Concat(
            [
                0xD4A20008u, 0x00022510u, // v_cmp_eq_i64 s8, v[16:17], v[18:19]
                0xD4E4000Au, 0x00022D14u, // v_cmp_gt_u64 s10, v[20:21], v[22:23]
                0xD4A1000Cu, 0x000230C1u, // v_cmp_lt_i64 s12, -1, v[24:25]
                0xD4E6000Eu, 0x00023484u, // v_cmp_ge_u64 s14, 4, v[26:27]
                SEndpgm,
            ])
            .ToArray();
        var ctx = CreateContext(instructionWords);
        Assert.True(
            Gen5ShaderTranslator.TryDecodeProgram(
                ctx,
                CodeAddress,
                out var program,
                out var decodeError),
            decodeError);
        Assert.Equal(
            [
                "VCmpFI64", "VCmpLtI64", "VCmpEqI64", "VCmpLeI64",
                "VCmpGtI64", "VCmpNeI64", "VCmpGeI64", "VCmpTI64",
                "VCmpxFI64", "VCmpxLtI64", "VCmpxEqI64", "VCmpxLeI64",
                "VCmpxGtI64", "VCmpxNeI64", "VCmpxGeI64", "VCmpxTI64",
                "VCmpFU64", "VCmpLtU64", "VCmpEqU64", "VCmpLeU64",
                "VCmpGtU64", "VCmpNeU64", "VCmpGeU64", "VCmpTU64",
                "VCmpxFU64", "VCmpxLtU64", "VCmpxEqU64", "VCmpxLeU64",
                "VCmpxGtU64", "VCmpxNeU64", "VCmpxGeU64", "VCmpxTU64",
                "VCmpEqI64", "VCmpGtU64", "VCmpLtI64", "VCmpGeU64",
                "SEndpgm",
            ],
            program.Instructions.Select(instruction => instruction.Opcode));
        Assert.Equal(
            [
                .. Enumerable.Repeat(106u, 8), .. Enumerable.Repeat(126u, 8),
                .. Enumerable.Repeat(106u, 8), .. Enumerable.Repeat(126u, 8),
                8u, 10u, 12u, 14u,
            ],
            program.Instructions
                .Take(36)
                .Select(instruction => Assert.Single(instruction.Destinations).Value));

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
        Assert.True(ContainsSpirvCapability(shader.Spirv, SpirvCapability.Int64));
        Assert.True(ContainsSpirvOpcode(shader.Spirv, (ushort)SpirvOp.SLessThan));
        Assert.True(ContainsSpirvOpcode(shader.Spirv, (ushort)SpirvOp.SGreaterThan));
        Assert.True(ContainsSpirvOpcode(shader.Spirv, (ushort)SpirvOp.UGreaterThan));
        Assert.True(ContainsSpirvOpcode(shader.Spirv, (ushort)SpirvOp.IEqual));
        Assert.True(ContainsSpirvOpcode(shader.Spirv, (ushort)SpirvOp.SConvert));
        Assert.True(ContainsSpirvOpcode(shader.Spirv, (ushort)SpirvOp.UConvert));
        Assert.True(ContainsSpirvOpcode(shader.Spirv, (ushort)SpirvOp.LogicalAnd));
    }

    [Fact]
    public void CompilesVectorInteger16ComparisonsToSpirv()
    {
        // The compact opcode blocks and E64 examples were verified with
        // LLVM 18 llvm-mc for gfx1030. False/true slots are not I16/U16 ops.
        var compactOpcodes = Enumerable
            .Range(0x89, 6)
            .Concat(Enumerable.Range(0x99, 6))
            .Concat(Enumerable.Range(0xA9, 6))
            .Concat(Enumerable.Range(0xB9, 6))
            .Select(opcode => (uint)opcode)
            .ToArray();
        var instructionWords = compactOpcodes
            .Select(opcode => 0x7C000000u | (opcode << 17) | (2u << 9) | 0x100u)
            .Concat(
            [
                0xD48A0008u, 0x00022510u, // v_cmp_eq_i16 s8, v16, v18
                0xD4AC000Au, 0x00022D14u, // v_cmp_gt_u16 s10, v20, v22
                0xD489000Cu, 0x000230C1u, // v_cmp_lt_i16 s12, -1, v24
                0xD4AE000Eu, 0x00023484u, // v_cmp_ge_u16 s14, 4, v26
                SEndpgm,
            ])
            .ToArray();
        var ctx = CreateContext(instructionWords);
        Assert.True(
            Gen5ShaderTranslator.TryDecodeProgram(
                ctx,
                CodeAddress,
                out var program,
                out var decodeError),
            decodeError);
        Assert.Equal(
            [
                "VCmpLtI16", "VCmpEqI16", "VCmpLeI16", "VCmpGtI16",
                "VCmpNeI16", "VCmpGeI16",
                "VCmpxLtI16", "VCmpxEqI16", "VCmpxLeI16", "VCmpxGtI16",
                "VCmpxNeI16", "VCmpxGeI16",
                "VCmpLtU16", "VCmpEqU16", "VCmpLeU16", "VCmpGtU16",
                "VCmpNeU16", "VCmpGeU16",
                "VCmpxLtU16", "VCmpxEqU16", "VCmpxLeU16", "VCmpxGtU16",
                "VCmpxNeU16", "VCmpxGeU16",
                "VCmpEqI16", "VCmpGtU16", "VCmpLtI16", "VCmpGeU16",
                "SEndpgm",
            ],
            program.Instructions.Select(instruction => instruction.Opcode));
        Assert.Equal(
            [
                .. Enumerable.Repeat(106u, 6), .. Enumerable.Repeat(126u, 6),
                .. Enumerable.Repeat(106u, 6), .. Enumerable.Repeat(126u, 6),
                8u, 10u, 12u, 14u,
            ],
            program.Instructions
                .Take(28)
                .Select(instruction => Assert.Single(instruction.Destinations).Value));

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
        Assert.True(ContainsSpirvOpcode(shader.Spirv, (ushort)SpirvOp.BitFieldSExtract));
        Assert.True(ContainsSpirvOpcode(shader.Spirv, (ushort)SpirvOp.BitFieldUExtract));
        Assert.True(ContainsSpirvOpcode(shader.Spirv, (ushort)SpirvOp.SLessThan));
        Assert.True(ContainsSpirvOpcode(shader.Spirv, (ushort)SpirvOp.SGreaterThan));
        Assert.True(ContainsSpirvOpcode(shader.Spirv, (ushort)SpirvOp.UGreaterThan));
        Assert.True(ContainsSpirvOpcode(shader.Spirv, (ushort)SpirvOp.IEqual));
        Assert.True(ContainsSpirvOpcode(shader.Spirv, (ushort)SpirvOp.LogicalAnd));
    }

    [Fact]
    public void CompilesVectorCarryInOutArithmeticToSpirv()
    {
        // Encodings assembled with LLVM 18 llvm-mc for gfx1030. E64 forms
        // exercise arbitrary carry-out and carry-in scalar wave masks.
        var ctx = CreateContext(
        [
            0xD5280800u, 0x01AA0501u, // v_add_co_ci_u32 v0, s8, v1, v2, vcc_lo
            0xD5286A03u, 0x002A0B04u, // v_add_co_ci_u32 v3, vcc_lo, v4, v5, s10
            0xD5290C06u, 0x01AA1107u, // v_sub_co_ci_u32 v6, s12, v7, v8, vcc_lo
            0xD52A0E09u, 0x01AA170Au, // v_subrev_co_ci_u32 v9, s14, v10, v11, vcc_lo
            0x50202511u,              // v_add_co_ci_u32_e32 v16, v17, v18
            0x52262B14u,              // v_sub_co_ci_u32_e32 v19, v20, v21
            0x542C3117u,              // v_subrev_co_ci_u32_e32 v22, v23, v24
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
                "VAddCoCiU32", "VAddCoCiU32", "VSubCoCiU32",
                "VSubrevCoCiU32", "VAddcU32", "VSubbU32", "VSubbrevU32",
                "SEndpgm",
            ],
            program.Instructions.Select(instruction => instruction.Opcode));
        Assert.Equal(
            [0u, 3u, 6u, 9u, 16u, 19u, 22u],
            program.Instructions
                .Take(7)
                .Select(instruction => Assert.Single(instruction.Destinations).Value));
        Assert.Equal(
            [8u, 106u, 12u, 14u],
            program.Instructions
                .Take(4)
                .Select(instruction =>
                    Assert.IsType<Gen5Vop3Control>(instruction.Control)
                        .ScalarDestination));
        Assert.Equal(
            [106u, 10u, 106u, 106u],
            program.Instructions
                .Take(4)
                .Select(instruction => instruction.Sources[2].Value));
        Assert.All(
            program.Instructions.Take(4),
            instruction => Assert.Equal(
                Gen5OperandKind.ScalarRegister,
                instruction.Sources[2].Kind));
        Assert.All(
            program.Instructions.Skip(4).Take(3),
            instruction => Assert.Equal(2, instruction.Sources.Count));

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
        Assert.True(ContainsSpirvOpcode(shader.Spirv, (ushort)SpirvOp.IAdd));
        Assert.True(ContainsSpirvOpcode(shader.Spirv, (ushort)SpirvOp.ISub));
        Assert.True(ContainsSpirvOpcode(shader.Spirv, (ushort)SpirvOp.ULessThan));
        Assert.True(ContainsSpirvOpcode(shader.Spirv, (ushort)SpirvOp.LogicalOr));
        Assert.True(ContainsSpirvOpcode(shader.Spirv, (ushort)SpirvOp.Select));
        Assert.True(ContainsSpirvOpcode(shader.Spirv, (ushort)SpirvOp.ShiftLeftLogical));
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
    public void CompilesExtendedVectorComparisonsToScalarMasks()
    {
        // E64 encodings assembled with LLVM 18 llvm-mc for gfx1030 and
        // verified with llvm-objdump.
        var ctx = CreateContext(
        [
            0xD4010104u, 0x20020500u, // v_cmp_lt_f32 s4, -|v0|, v2
            0xD416007Eu, 0x00020D04u, // v_cmpx_ge_f32 v4, v6 (writes exec_lo)
            0xD4820008u, 0x00021508u, // v_cmp_eq_i32 s8, v8, v10
            0xD4C4000Au, 0x00021D0Cu, // v_cmp_gt_u32 s10, v12, v14
            0xD417007Eu, 0x00022510u, // v_cmpx_o_f32 v16, v18
            0xD418007Eu, 0x00022D14u, // v_cmpx_u_f32 v20, v22
            0xD41A007Eu, 0x00023518u, // v_cmpx_nlg_f32 v24, v26
            0xD498007Eu, 0x00023D1Cu, // v_cmpx_class_f32 v28, v30
            0x7C321508u,              // v_cmpx_nge_f32_e32 v8, v10
            0x7C362510u,              // v_cmpx_ngt_f32_e32 v16, v18
            0x7C382D14u,              // v_cmpx_nle_f32_e32 v20, v22
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
                "VCmpLtF32", "VCmpxGeF32", "VCmpEqI32", "VCmpGtU32",
                "VCmpxOF32", "VCmpxUF32", "VCmpxNlgF32", "VCmpxClassF32",
                "VCmpxNgeF32", "VCmpxNgtF32", "VCmpxNleF32", "SEndpgm",
            ],
            program.Instructions.Select(instruction => instruction.Opcode));
        Assert.All(program.Instructions.Take(11), instruction => Assert.Equal(2, instruction.Sources.Count));
        Assert.Equal(
            [4u, 126u, 8u, 10u, 126u, 126u, 126u, 126u, 126u, 126u, 126u],
            program.Instructions
                .Take(11)
                .Select(instruction => Assert.Single(instruction.Destinations).Value));
        Assert.All(
            program.Instructions.Take(11),
            instruction => Assert.Equal(
                Gen5OperandKind.ScalarRegister,
                Assert.Single(instruction.Destinations).Kind));

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
        Assert.True(ContainsSpirvOpcode(shader.Spirv, (ushort)SpirvOp.FOrdLessThan));
        Assert.True(ContainsSpirvOpcode(shader.Spirv, (ushort)SpirvOp.FOrdGreaterThanEqual));
        Assert.True(ContainsSpirvOpcode(shader.Spirv, (ushort)SpirvOp.FNegate));
        Assert.True(ContainsSpirvOpcode(shader.Spirv, (ushort)SpirvOp.IEqual));
        Assert.True(ContainsSpirvOpcode(shader.Spirv, (ushort)SpirvOp.UGreaterThan));
        Assert.True(ContainsSpirvOpcode(shader.Spirv, (ushort)SpirvOp.LogicalAnd));
        Assert.True(ContainsSpirvOpcode(shader.Spirv, (ushort)SpirvOp.FUnordEqual));
        Assert.True(ContainsSpirvOpcode(shader.Spirv, (ushort)SpirvOp.IsNan));
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
    public void CompilesVector16BitTernaryArithmeticToSpirv()
    {
        // Encodings assembled with LLVM 18 llvm-mc for gfx1030. The final
        // forms exercise source-half selection, high-half writes and F16
        // output modifiers in addition to the default low-half forms.
        var ctx = CreateContext(
        [
            0xD7510000u, 0x040E0501u, // v_min3_f16 v0, v1, v2, v3
            0xD7540004u, 0x041E0D05u, // v_max3_f16 v4, v5, v6, v7
            0xD7570008u, 0x042E1509u, // v_med3_f16 v8, v9, v10, v11
            0xD752000Cu, 0x043E1D0Du, // v_min3_i16 v12, v13, v14, v15
            0xD7550010u, 0x044E2511u, // v_max3_i16 v16, v17, v18, v19
            0xD7580014u, 0x045E2D15u, // v_med3_i16 v20, v21, v22, v23
            0xD7530018u, 0x046E3519u, // v_min3_u16 v24, v25, v26, v27
            0xD756001Cu, 0x047E3D1Du, // v_max3_u16 v28, v29, v30, v31
            0xD7590020u, 0x048E4521u, // v_med3_u16 v32, v33, v34, v35
            0xD7750024u, 0x049E4D25u, // v_mad_i32_i16 v36, v37, v38, v39
            0xD7730028u, 0x04AE5529u, // v_mad_u32_u16 v40, v41, v42, v43
            0xD751692Cu, 0x24BE5D2Du, // v_min3_f16 v44, -|v45|, v46, v47 op_sel:[1,0,1,1]
            0xD7526830u, 0x04CE6531u, // v_min3_i16 v48, v49, v50, v51 op_sel:[1,0,1,1]
            0xD7752834u, 0x04DE6D35u, // v_mad_i32_i16 v52, v53, v54, v55 op_sel:[1,0,1,0]
            0xD75E6838u, 0x04EE7539u, // v_mad_i16 v56, v57, v58, v59 op_sel:[1,0,1,1]
            0xD740683Cu, 0x04FE7D3Du, // v_mad_u16 v60, v61, v62, v63 op_sel:[1,0,1,1]
            0xD7548040u, 0x0D0E8541u, // v_max3_f16 v64, v65, v66, v67 clamp mul:2
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
                "VMin3F16", "VMax3F16", "VMed3F16",
                "VMin3I16", "VMax3I16", "VMed3I16",
                "VMin3U16", "VMax3U16", "VMed3U16",
                "VMadI32I16", "VMadU32U16", "VMin3F16", "VMin3I16",
                "VMadI32I16", "VMadI16", "VMadU16", "VMax3F16", "SEndpgm",
            ],
            program.Instructions.Select(instruction => instruction.Opcode));
        Assert.Equal(
            Enumerable.Range(0, 17).Select(index => (uint)(index * 4)),
            program.Instructions
                .Take(17)
                .Select(instruction => Assert.Single(instruction.Destinations).Value));
        Assert.All(
            program.Instructions.Take(17),
            instruction => Assert.Equal(3, instruction.Sources.Count));

        var floatSelect = Assert.IsType<Gen5Vop3Control>(program.Instructions[11].Control);
        Assert.Equal(1u, floatSelect.AbsoluteMask);
        Assert.Equal(1u, floatSelect.NegateMask);
        Assert.Equal(13u, floatSelect.OpSelectMask);
        Assert.Equal(
            13u,
            Assert.IsType<Gen5Vop3Control>(program.Instructions[12].Control).OpSelectMask);
        Assert.Equal(
            5u,
            Assert.IsType<Gen5Vop3Control>(program.Instructions[13].Control).OpSelectMask);
        Assert.All(
            program.Instructions.Skip(14).Take(2),
            instruction => Assert.Equal(
                13u,
                Assert.IsType<Gen5Vop3Control>(instruction.Control).OpSelectMask));
        var modifiedOutput = Assert.IsType<Gen5Vop3Control>(program.Instructions[16].Control);
        Assert.True(modifiedOutput.Clamp);
        Assert.Equal(1u, modifiedOutput.OutputModifier);

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
        Assert.True(ContainsGlslExtInst(shader.Spirv, 37));
        Assert.True(ContainsGlslExtInst(shader.Spirv, 38));
        Assert.True(ContainsGlslExtInst(shader.Spirv, 39));
        Assert.True(ContainsGlslExtInst(shader.Spirv, 40));
        Assert.True(ContainsGlslExtInst(shader.Spirv, 41));
        Assert.True(ContainsGlslExtInst(shader.Spirv, 42));
        Assert.True(ContainsGlslExtInst(shader.Spirv, 43));
        Assert.True(ContainsGlslExtInst(shader.Spirv, 58));
        Assert.True(ContainsGlslExtInst(shader.Spirv, 62));
        Assert.True(ContainsSpirvOpcode(shader.Spirv, (ushort)SpirvOp.BitFieldSExtract));
        Assert.True(ContainsSpirvOpcode(shader.Spirv, (ushort)SpirvOp.BitFieldUExtract));
        Assert.True(ContainsSpirvOpcode(shader.Spirv, (ushort)SpirvOp.IMul));
        Assert.True(ContainsSpirvOpcode(shader.Spirv, (ushort)SpirvOp.IAdd));
        Assert.True(ContainsSpirvOpcode(shader.Spirv, (ushort)SpirvOp.ShiftLeftLogical));
        Assert.True(ContainsSpirvOpcode(shader.Spirv, (ushort)SpirvOp.Store));
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
    public void CompilesVectorHalfDivisionFixupToSpirv()
    {
        // Encodings assembled with LLVM 18 llvm-mc for gfx1030 and verified
        // with llvm-objdump.
        var ctx = CreateContext(
        [
            0xD75F0000u, 0x040E0501u, // v_div_fixup_f16 v0, v1, v2, v3
            0xD75FE904u, 0x6C1E0D05u, // v_div_fixup_f16 v4, -|v5|, -v6, v7 op_sel:[1,0,1,1] clamp mul:2
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
            ["VDivFixupF16", "VDivFixupF16", "SEndpgm"],
            program.Instructions.Select(instruction => instruction.Opcode));

        var modifiedControl = Assert.IsType<Gen5Vop3Control>(
            program.Instructions[1].Control);
        Assert.Equal(1u, modifiedControl.AbsoluteMask);
        Assert.Equal(3u, modifiedControl.NegateMask);
        Assert.Equal(13u, modifiedControl.OpSelectMask);
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
        Assert.True(ContainsGlslExtInst(shader.Spirv, 58));
        Assert.True(ContainsGlslExtInst(shader.Spirv, 62));
        Assert.True(ContainsSpirvOpcode(shader.Spirv, (ushort)SpirvOp.BitFieldUExtract));
        Assert.True(ContainsSpirvOpcode(shader.Spirv, (ushort)SpirvOp.UGreaterThan));
        Assert.True(ContainsSpirvOpcode(shader.Spirv, (ushort)SpirvOp.IEqual));
        Assert.True(ContainsSpirvOpcode(shader.Spirv, (ushort)SpirvOp.LogicalAnd));
        Assert.True(ContainsSpirvOpcode(shader.Spirv, (ushort)SpirvOp.LogicalOr));
        Assert.True(ContainsSpirvOpcode(shader.Spirv, (ushort)SpirvOp.BitwiseXor));
        Assert.True(ContainsSpirvOpcode(shader.Spirv, (ushort)SpirvOp.Select));
        Assert.True(ContainsSpirvOpcode(shader.Spirv, (ushort)SpirvOp.Store));
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
    public void CompilesVectorFloat64DivisionCorrectionToSpirv()
    {
        // Encodings assembled with LLVM 18 llvm-mc for gfx1030 and verified
        // with llvm-objdump.
        var ctx = CreateContext(
        [
            0xD5600000u, 0x041A0902u, // v_div_fixup_f64 v[0:1], v[2:3], v[4:5], v[6:7]
            0xD56E6A00u, 0x041A0902u, // v_div_scale_f64 v[0:1], vcc_lo, v[2:3], v[4:5], v[6:7]
            0xD5700000u, 0x041A0902u, // v_div_fmas_f64 v[0:1], v[2:3], v[4:5], v[6:7]
            0xD5608108u, 0x4C3A190Au, // v_div_fixup_f64 v[8:9], |v[10:11]|, -v[12:13], v[14:15] clamp mul:2
            0xD56E0A10u, 0x045A2912u, // v_div_scale_f64 v[16:17], s10, v[18:19], v[20:21], v[22:23]
            0xD5708118u, 0x4C7A391Au, // v_div_fmas_f64 v[24:25], |v[26:27]|, -v[28:29], v[30:31] clamp mul:2
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
                "VDivFixupF64", "VDivScaleF64", "VDivFmasF64",
                "VDivFixupF64", "VDivScaleF64", "VDivFmasF64", "SEndpgm",
            ],
            program.Instructions.Select(instruction => instruction.Opcode));
        Assert.All(
            program.Instructions.Take(6),
            instruction => Assert.Equal(3, instruction.Sources.Count));

        Assert.Equal(
            106u,
            Assert.IsType<Gen5Vop3Control>(program.Instructions[1].Control)
                .ScalarDestination);
        Assert.Equal(
            10u,
            Assert.IsType<Gen5Vop3Control>(program.Instructions[4].Control)
                .ScalarDestination);
        var modifiedFixup = Assert.IsType<Gen5Vop3Control>(
            program.Instructions[3].Control);
        Assert.Equal(1u, modifiedFixup.AbsoluteMask);
        Assert.Equal(2u, modifiedFixup.NegateMask);
        Assert.Equal(1u, modifiedFixup.OutputModifier);
        Assert.True(modifiedFixup.Clamp);
        var modifiedFmas = Assert.IsType<Gen5Vop3Control>(
            program.Instructions[5].Control);
        Assert.Equal(1u, modifiedFmas.AbsoluteMask);
        Assert.Equal(2u, modifiedFmas.NegateMask);
        Assert.Equal(1u, modifiedFmas.OutputModifier);
        Assert.True(modifiedFmas.Clamp);

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
        Assert.True(ContainsGlslExtInst(shader.Spirv, 4));
        Assert.True(ContainsGlslExtInst(shader.Spirv, 43));
        Assert.True(ContainsGlslExtInst(shader.Spirv, 50));
        Assert.True(ContainsGlslExtInst(shader.Spirv, 53));
        Assert.True(ContainsSpirvOpcode(shader.Spirv, (ushort)SpirvOp.FDiv));
        Assert.True(ContainsSpirvOpcode(shader.Spirv, (ushort)SpirvOp.IsNan));
        Assert.True(ContainsSpirvOpcode(shader.Spirv, (ushort)SpirvOp.BitwiseXor));
        Assert.True(ContainsSpirvOpcode(shader.Spirv, (ushort)SpirvOp.ULessThan));
        Assert.True(ContainsSpirvOpcode(shader.Spirv, (ushort)SpirvOp.UGreaterThanEqual));
        Assert.True(ContainsSpirvOpcode(shader.Spirv, (ushort)SpirvOp.ULessThanEqual));
        Assert.True(ContainsSpirvOpcode(shader.Spirv, (ushort)SpirvOp.FOrdEqual));
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
    public void CompilesRemainingVop1RegisterUtilitiesToSpirv()
    {
        // Encodings assembled with LLVM 18 llvm-mc for gfx1030 and verified
        // with llvm-objdump. M0 selects source +10 and destination +20 for
        // the split-offset relative operations.
        var ctx = CreateContext(
        [
            0xBEFC03FFu, 0x0014000Au, // s_mov_b32 m0, 0x0014000a
            0x7E00C501u,              // v_sat_pk_u8_i16_e32 v0, v1
            0x7E04CB03u,              // v_swap_b32 v2, v3
            0x7E08D105u,              // v_swaprel_b32 v4, v5
            0x7E0C9107u,              // v_movrelsd_2_b32_e32 v6, v7
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
                "SMovB32", "VSatPkU8I16", "VSwapB32", "VSwaprelB32",
                "VMovrelsd2B32", "SEndpgm",
            ],
            program.Instructions.Select(instruction => instruction.Opcode));
        Assert.Equal(
            [0u, 2u, 4u, 6u],
            program.Instructions
                .Skip(1)
                .Take(4)
                .Select(instruction => Assert.Single(instruction.Destinations).Value));
        Assert.Equal(
            [1u, 3u, 5u, 7u],
            program.Instructions
                .Skip(1)
                .Take(4)
                .Select(instruction => Assert.Single(instruction.Sources).Value));

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
        Assert.True(ContainsGlslExtInst(shader.Spirv, 39));
        Assert.True(ContainsGlslExtInst(shader.Spirv, 42));
        Assert.True(ContainsSpirvOpcode(shader.Spirv, (ushort)SpirvOp.BitFieldSExtract));
        Assert.True(ContainsSpirvOpcode(shader.Spirv, (ushort)SpirvOp.ShiftLeftLogical));
        Assert.True(ContainsSpirvOpcode(shader.Spirv, (ushort)SpirvOp.BitwiseAnd));
        Assert.True(ContainsSpirvOpcode(shader.Spirv, (ushort)SpirvOp.BitwiseOr));
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
        // LLVM 18 gfx1030 rejects the reserved encodings below. The signed
        // multiply uses its RDNA2 VOP3 opcode; the canonical bitwise encodings
        // were assembled and verified with LLVM.
        var ctx = CreateContext(
        [
            0xD5020000u, 0x040A0301u, // reserved (compact v_dot2c_f32_f16 encoding)
            0xD51F0000u, 0x040A0301u, // reserved (legacy v_mac_f32 encoding)
            0xD5220000u, 0x040A0301u, // reserved (compact v_bcnt_u32_b32 encoding)
            0xD5300000u, 0x040A0301u, // reserved (compact v_cvt_pk_u16_u32 encoding)
            0xD5310000u, 0x040A0301u, // reserved (compact v_cvt_pk_i16_i32 encoding)
            0xD43F007Eu, 0x00020500u, // reserved (v_cmpx_t_f64 encoding)
            0xD4EF0004u, 0x00020500u, // reserved (v_cmp_t_f16 encoding)
            0xD4FF007Eu, 0x00020500u, // reserved (v_cmpx_t_f16 encoding)
            0xD4AF0004u, 0x00020500u, // reserved (v_cmp_t_u16 encoding)
            0xD4BF007Eu, 0x00020500u, // reserved (v_cmpx_t_u16 encoding)
            0xD5410000u, 0x040A0301u, // reserved (legacy v_mad_f32 encoding)
            0xD56B0000u, 0x040A0301u, // v_mul_lo_i32 v0, v1, v1
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
                "Vop3Raw131", "Vop3Raw03F", "Vop3Raw0EF", "Vop3Raw0FF",
                "Vop3Raw0AF", "Vop3Raw0BF", "Vop3Raw141", "VMulLoI32",
                "VLshlOrB32", "VOr3B32", "SEndpgm",
            ],
            program.Instructions.Select(instruction => instruction.Opcode));

        // Reserved instructions remain decodable for diagnostics, but only
        // the supported RDNA2 instructions are submitted to the translator.
        var supportedProgram = program with
        {
            Instructions = program.Instructions.Skip(11).ToArray(),
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
        Assert.True(ContainsSpirvOpcode(shader.Spirv, (ushort)SpirvOp.IMul));
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

    private static bool ContainsSpirvConstant(byte[] spirv, uint value)
    {
        for (var offset = 5 * sizeof(uint); offset < spirv.Length;)
        {
            var instruction = BitConverter.ToUInt32(spirv, offset);
            var wordCount = instruction >> 16;
            if ((ushort)instruction == (ushort)SpirvOp.Constant &&
                wordCount == 4 &&
                BitConverter.ToUInt32(spirv, offset + (3 * sizeof(uint))) == value)
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

    private static bool ContainsSpirvVariable(
        byte[] spirv,
        SpirvStorageClass storageClass)
    {
        for (var offset = 5 * sizeof(uint); offset < spirv.Length;)
        {
            var instruction = BitConverter.ToUInt32(spirv, offset);
            var wordCount = instruction >> 16;
            if ((ushort)instruction == (ushort)SpirvOp.Variable &&
                wordCount >= 4 &&
                BitConverter.ToUInt32(spirv, offset + (3 * sizeof(uint))) ==
                    (uint)storageClass)
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

    private static int CountSpirvOpcode(byte[] spirv, ushort opcode)
    {
        var count = 0;
        for (var offset = 5 * sizeof(uint); offset < spirv.Length;)
        {
            var instruction = BitConverter.ToUInt32(spirv, offset);
            if ((ushort)instruction == opcode)
            {
                count++;
            }

            var wordCount = instruction >> 16;
            if (wordCount == 0)
            {
                break;
            }

            offset += checked((int)wordCount * sizeof(uint));
        }

        return count;
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
