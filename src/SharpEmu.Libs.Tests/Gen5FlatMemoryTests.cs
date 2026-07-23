// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using SharpEmu.ShaderCompiler;
using SharpEmu.ShaderCompiler.Vulkan;
using Xunit;

namespace SharpEmu.Libs.Tests;

public sealed class Gen5FlatMemoryTests
{
    private const ulong CodeAddress = 0x1_0000_0000;
    private const ulong BufferAddress = 0x2000_0000;
    private const uint SEndpgm = 0xBF810000;

    [Theory]
    [InlineData(0xDC200000u, 0x00000801u, "FlatLoadUbyte", 1u, 1)]
    [InlineData(0xDC340000u, 0x00000801u, "FlatLoadDwordx2", 2u, 2)]
    [InlineData(0xDC3C0000u, 0x00000801u, "FlatLoadDwordx3", 3u, 3)]
    [InlineData(0xDC380000u, 0x00000801u, "FlatLoadDwordx4", 4u, 4)]
    [InlineData(0xDC600000u, 0x00000801u, "FlatStoreByte", 1u, 0)]
    [InlineData(0xDC740000u, 0x00000801u, "FlatStoreDwordx2", 2u, 0)]
    [InlineData(0xDC7C0000u, 0x00000801u, "FlatStoreDwordx3", 3u, 0)]
    [InlineData(0xDC780000u, 0x00000801u, "FlatStoreDwordx4", 4u, 0)]
    public void DecodesFlatWidthsWithVectorAddressPairs(
        uint word,
        uint extra,
        string opcode,
        uint dwordCount,
        int destinationCount)
    {
        var ctx = CreateContext([word, extra, SEndpgm]);

        Assert.True(
            Gen5ShaderTranslator.TryDecodeProgram(
                ctx,
                CodeAddress,
                out var program,
                out var decodeError),
            decodeError);

        var instruction = program.Instructions[0];
        var control = Assert.IsType<Gen5GlobalMemoryControl>(instruction.Control);
        Assert.Equal(opcode, instruction.Opcode);
        Assert.Equal(dwordCount, control.DwordCount);
        Assert.True(control.UsesFlatAddress);
        Assert.Equal(uint.MaxValue, control.ScalarAddress);
        Assert.Equal(
            [Gen5Operand.Vector(1), Gen5Operand.Vector(2)],
            instruction.Sources.Take(2));
        Assert.Equal(destinationCount, instruction.Destinations.Count);
    }

    [Fact]
    public void FlatLoadInfersScalarBaseEvaluatesAndCompiles()
    {
        var ctx = CreateContext(
        [
            // v_add_co_u32 v1, vcc_lo, s12, v6
            0xD70F6A01u, 0x00020C0Cu,
            // v_add_co_ci_u32_sdwa v2, vcc_lo, 0, s13, vcc_lo
            0x50041AF9u, 0x86860680u,
            // flat_load_ubyte v0, v[1:2]
            0xDC200000u, 0x007D0001u,
            SEndpgm,
        ]);
        Assert.True(
            Gen5ShaderTranslator.TryDecodeProgram(
                ctx,
                CodeAddress,
                out var program,
                out var decodeError),
            decodeError);

        var instruction = Assert.Single(
            program.Instructions,
            candidate => candidate.Opcode == "FlatLoadUbyte");
        var control = Assert.IsType<Gen5GlobalMemoryControl>(instruction.Control);
        Assert.True(control.UsesFlatAddress);
        Assert.Equal(12u, control.ScalarAddress);
        Assert.Equal(
            [
                Gen5Operand.Vector(1),
                Gen5Operand.Vector(2),
                Gen5Operand.Scalar(12),
            ],
            instruction.Sources);

        var state = CreateState(program);
        Assert.True(
            Gen5ShaderScalarEvaluator.TryEvaluate(
                ctx,
                state,
                out var evaluation,
                out var evaluationError),
            evaluationError);
        var binding = Assert.Single(evaluation.GlobalMemoryBindings);
        Assert.Equal(12u, binding.ScalarAddress);
        Assert.False(binding.Writable);
        Assert.Contains(instruction.Pc, binding.InstructionPcs);

        Assert.True(
            Gen5SpirvTranslator.TryCompileComputeShader(
                state,
                evaluation,
                localSizeX: 1,
                localSizeY: 1,
                localSizeZ: 1,
                out var shader,
                out var compileError),
            compileError);
        Assert.True(ContainsSpirvOpcode(shader.Spirv, SpirvOp.ISub));
    }

    [Fact]
    public void FlatStoreInfersScalarBaseAndMarksBindingWritable()
    {
        var ctx = CreateContext(
        [
            // v_add_co_u32 v1, vcc_lo, s12, v6
            0xD70F6A01u, 0x00020C0Cu,
            // v_add_co_ci_u32_sdwa v2, vcc_lo, 0, s13, vcc_lo
            0x50041AF9u, 0x86860680u,
            // flat_store_dword v[1:2], v7
            0xDC700000u, 0x007D0701u,
            SEndpgm,
        ]);
        Assert.True(
            Gen5ShaderTranslator.TryDecodeProgram(
                ctx,
                CodeAddress,
                out var program,
                out var decodeError),
            decodeError);

        var instruction = Assert.Single(
            program.Instructions,
            candidate => candidate.Opcode == "FlatStoreDword");
        var control = Assert.IsType<Gen5GlobalMemoryControl>(instruction.Control);
        Assert.True(control.UsesFlatAddress);
        Assert.Equal(12u, control.ScalarAddress);
        Assert.Equal(
            [
                Gen5Operand.Vector(1),
                Gen5Operand.Vector(2),
                Gen5Operand.Vector(7),
                Gen5Operand.Scalar(12),
            ],
            instruction.Sources);

        var state = CreateState(program);
        Assert.True(
            Gen5ShaderScalarEvaluator.TryEvaluate(
                ctx,
                state,
                out var evaluation,
                out var evaluationError),
            evaluationError);
        Assert.True(Assert.Single(evaluation.GlobalMemoryBindings).Writable);
        Assert.True(
            Gen5SpirvTranslator.TryCompileComputeShader(
                state,
                evaluation,
                localSizeX: 1,
                localSizeY: 1,
                localSizeZ: 1,
                out var shader,
                out var compileError),
            compileError);
        Assert.True(ContainsSpirvOpcode(shader.Spirv, SpirvOp.ISub));
    }

    [Fact]
    public void FlatAddressWithoutAdjacentScalarPairFailsClosed()
    {
        var ctx = CreateContext(
        [
            // Low and high address halves derive from non-adjacent SGPRs.
            0xD70F6A01u, 0x00020C0Cu,
            0x50041EF9u, 0x86860680u,
            0xDC300000u, 0x007D0801u,
            SEndpgm,
        ]);
        Assert.True(
            Gen5ShaderTranslator.TryDecodeProgram(
                ctx,
                CodeAddress,
                out var program,
                out var decodeError),
            decodeError);

        var instruction = Assert.Single(
            program.Instructions,
            candidate => candidate.Opcode == "FlatLoadDword");
        var control = Assert.IsType<Gen5GlobalMemoryControl>(instruction.Control);
        Assert.True(control.UsesFlatAddress);
        Assert.Equal(uint.MaxValue, control.ScalarAddress);
        Assert.DoesNotContain(
            instruction.Sources,
            operand => operand.Kind == Gen5OperandKind.ScalarRegister);

        Assert.False(
            Gen5ShaderScalarEvaluator.TryEvaluate(
                ctx,
                CreateState(program),
                out var evaluation,
                out var evaluationError));
        Assert.Null(evaluation);
        Assert.Contains("flat-address-base-unresolved", evaluationError);
    }

    [Fact]
    public void GlobalMemoryKeepsItsEncodedScalarBase()
    {
        var ctx = CreateContext(
        [
            0xDC308000u, 0x080C0000u, // global_load_dword v8, v0, s[12:13]
            SEndpgm,
        ]);
        Assert.True(
            Gen5ShaderTranslator.TryDecodeProgram(
                ctx,
                CodeAddress,
                out var program,
                out var decodeError),
            decodeError);

        var control = Assert.IsType<Gen5GlobalMemoryControl>(
            program.Instructions[0].Control);
        Assert.False(control.UsesFlatAddress);
        Assert.Equal(12u, control.ScalarAddress);
        Assert.Equal(
            [Gen5Operand.Vector(0), Gen5Operand.Scalar(12)],
            program.Instructions[0].Sources);
    }

    private static Gen5ShaderState CreateState(Gen5ShaderProgram program) =>
        new(
            program,
            [
                unchecked((uint)BufferAddress),
                unchecked((uint)(BufferAddress >> 32)),
            ],
            Metadata: null,
            UserDataScalarRegisterBase: 12);

    private static CpuContext CreateContext(uint[] instructionWords)
    {
        var bytes = new byte[instructionWords.Length * sizeof(uint)];
        for (var index = 0; index < instructionWords.Length; index++)
        {
            BitConverter.TryWriteBytes(
                bytes.AsSpan(index * sizeof(uint)),
                instructionWords[index]);
        }

        var memory = new FakeGuestMemory();
        memory.AddRegion(CodeAddress, bytes);
        memory.AddRegion(BufferAddress, new byte[1024 * 1024]);
        return new CpuContext(memory, Generation.Gen5);
    }

    private static bool ContainsSpirvOpcode(byte[] spirv, SpirvOp opcode)
    {
        for (var offset = 5 * sizeof(uint); offset < spirv.Length;)
        {
            var instruction = BitConverter.ToUInt32(spirv, offset);
            if ((ushort)instruction == (ushort)opcode)
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
}
