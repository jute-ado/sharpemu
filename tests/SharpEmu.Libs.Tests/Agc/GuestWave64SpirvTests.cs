// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using SharpEmu.Libs.Agc;
using SharpEmu.ShaderCompiler;
using SharpEmu.ShaderCompiler.Vulkan;
using Xunit;

namespace SharpEmu.Libs.Tests.Agc;

public sealed class GuestWave64SpirvTests
{
    [Theory]
    [InlineData(0u, 64u)]
    [InlineData(1u, 64u)]
    [InlineData(1u << 14, 64u)]
    [InlineData(1u << 15, 32u)]
    [InlineData(uint.MaxValue, 32u)]
    public void DispatchInitiatorSelectsGuestWaveSize(
        uint initiator,
        uint expected)
    {
        Assert.Equal(
            expected,
            AgcExports.DecodeComputeWaveLaneCount(initiator));
    }

    [Fact]
    public void Wave32LaneOperationsUseNativeSubgroupInstructions()
    {
        var shader = CompileLaneProgram(32, 32, 1, 1);

        Assert.True(
            ContainsCapability(
                shader,
                SpirvCapability.GroupNonUniformBallot));
        Assert.True(ContainsOpcode(shader, SpirvOp.GroupNonUniformBallot));
        Assert.True(ContainsOpcode(shader, SpirvOp.GroupNonUniformShuffle));
        Assert.False(ContainsOpcode(shader, SpirvOp.ControlBarrier));
        Assert.False(ContainsBuiltIn(shader, SpirvBuiltIn.LocalInvocationIndex));
    }

    [Fact]
    public void Wave64LaneOperationsUseWorkgroupBridge()
    {
        var shader = CompileLaneProgram(64, 64, 1, 1);

        Assert.True(ContainsOpcode(shader, SpirvOp.ControlBarrier));
        Assert.True(ContainsOpcode(shader, SpirvOp.AtomicOr));
        Assert.True(ContainsBuiltIn(shader, SpirvBuiltIn.LocalInvocationIndex));
        Assert.True(ContainsWorkgroupVariable(shader));
        Assert.False(
            ContainsCapability(
                shader,
                SpirvCapability.GroupNonUniformBallot));
        Assert.False(ContainsOpcode(shader, SpirvOp.GroupNonUniformShuffle));
    }

    [Fact]
    public void Wave64LaneOperationsAllowA32LanePartialGuestWave()
    {
        var shader = CompileLaneProgram(64, 32, 1, 1);

        Assert.True(ContainsOpcode(shader, SpirvOp.ControlBarrier));
        Assert.True(ContainsBuiltIn(shader, SpirvBuiltIn.LocalInvocationIndex));
        Assert.True(ContainsWorkgroupVariable(shader));
        Assert.False(
            ContainsCapability(
                shader,
                SpirvCapability.GroupNonUniformBallot));
        Assert.False(ContainsOpcode(shader, SpirvOp.GroupNonUniformShuffle));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void GraphicsLaneInstructionsKeepExecMasksPerInvocation(
        bool pixelShader)
    {
        var (state, evaluation) = CreateProgram(
        [
            new(
                0,
                Gen5ShaderEncoding.Vop3,
                "VWritelaneB32",
                [0, 0],
                [Gen5Operand.Scalar(0), Gen5Operand.Scalar(1)],
                [Gen5Operand.Vector(0)],
                null),
            EndProgram(8),
        ]);

        byte[] spirv;
        if (pixelShader)
        {
            Assert.True(
                Gen5SpirvTranslator.TryCompilePixelShader(
                    state,
                    evaluation,
                    Gen5PixelOutputKind.Float,
                    out var shader,
                    out var error),
                error);
            spirv = shader.Spirv;
        }
        else
        {
            Assert.True(
                Gen5SpirvTranslator.TryCompileVertexShader(
                    state,
                    evaluation,
                    out var shader,
                    out var error),
                error);
            spirv = shader.Spirv;
        }

        Assert.True(
            ContainsBuiltIn(
                spirv,
                SpirvBuiltIn.SubgroupLocalInvocationId));
        Assert.False(
            ContainsOpcode(
                spirv,
                SpirvOp.GroupNonUniformBallot));
        Assert.False(
            ContainsCapability(
                spirv,
                SpirvCapability.GroupNonUniformBallot));
    }

    [Fact]
    public void GraphicsReadFirstLaneRetainsItsNativeBallot()
    {
        var (state, evaluation) = CreateProgram(
        [
            new(
                0,
                Gen5ShaderEncoding.Vop1,
                "VReadfirstlaneB32",
                [0],
                [Gen5Operand.Vector(0)],
                [Gen5Operand.Scalar(0)],
                null),
            EndProgram(4),
        ]);

        Assert.True(
            Gen5SpirvTranslator.TryCompilePixelShader(
                state,
                evaluation,
                Gen5PixelOutputKind.Float,
                out var shader,
                out var error),
            error);
        Assert.True(
            ContainsOpcode(
                shader.Spirv,
                SpirvOp.GroupNonUniformBallot));
        Assert.True(
            ContainsCapability(
                shader.Spirv,
                SpirvCapability.GroupNonUniformBallot));
        Assert.True(
            ContainsOpcode(
                shader.Spirv,
                SpirvOp.GroupNonUniformShuffle));
    }

    [Fact]
    public void Wave64DataSharePermutesUseTheSameWorkgroupBridge()
    {
        var (state, evaluation) = CreateProgram(
        [
            new(
                0,
                Gen5ShaderEncoding.Ds,
                "DsBpermuteB32",
                [0, 0],
                [Gen5Operand.Vector(4), Gen5Operand.Vector(5)],
                [Gen5Operand.Vector(3)],
                new Gen5DataShareControl(0, 0, Gds: false)),
            new(
                8,
                Gen5ShaderEncoding.Ds,
                "DsPermuteB32",
                [0, 0],
                [Gen5Operand.Vector(4), Gen5Operand.Vector(5)],
                [Gen5Operand.Vector(3)],
                new Gen5DataShareControl(0, 0, Gds: false)),
            EndProgram(16),
        ]);

        Assert.True(
            Gen5SpirvTranslator.TryCompileComputeShader(
                state,
                evaluation,
                64,
                1,
                1,
                out var shader,
                out var error,
                waveLaneCount: 64),
            error);
        Assert.True(ContainsOpcode(shader.Spirv, SpirvOp.ControlBarrier));
        Assert.True(ContainsWorkgroupVariable(shader.Spirv));
        Assert.False(
            ContainsOpcode(shader.Spirv, SpirvOp.GroupNonUniformShuffle));
    }

    [Fact]
    public void Wave64ScratchDoesNotAliasGuestLocalDataShare()
    {
        var (state, evaluation) = CreateProgram(
        [
            new(
                0,
                Gen5ShaderEncoding.Ds,
                "DsWriteB32",
                [0, 0],
                [Gen5Operand.Vector(0), Gen5Operand.Vector(1)],
                [],
                new Gen5DataShareControl(0, 0, Gds: false)),
            new(
                8,
                Gen5ShaderEncoding.Vop1,
                "VReadlaneB32",
                [0],
                [Gen5Operand.Vector(2), Gen5Operand.Scalar(0)],
                [Gen5Operand.Scalar(1)],
                null),
            EndProgram(12),
        ]);

        Assert.True(
            Gen5SpirvTranslator.TryCompileComputeShader(
                state,
                evaluation,
                64,
                1,
                1,
                out var shader,
                out var error,
                waveLaneCount: 64),
            error);
        Assert.Equal(2, CountWorkgroupVariables(shader.Spirv));
    }

    [Fact]
    public void Wave64WithoutWaveCoordinationAllowsMultipleGuestWaves()
    {
        var (state, evaluation) = CreateProgram(
        [
            new(
                0,
                Gen5ShaderEncoding.Vop1,
                "VMovB32",
                [0],
                [Gen5Operand.Vector(0)],
                [Gen5Operand.Vector(1)],
                null),
            EndProgram(4),
        ]);

        Assert.True(
            Gen5SpirvTranslator.TryCompileComputeShader(
                state,
                evaluation,
                128,
                1,
                1,
                out var shader,
                out var error,
                waveLaneCount: 64),
            error);
        Assert.False(ContainsOpcode(shader.Spirv, SpirvOp.ControlBarrier));
        Assert.False(ContainsWorkgroupVariable(shader.Spirv));
    }

    [Fact]
    public void ComputeShadersGuardPartialGroupsWithPushConstantThreadLimits()
    {
        var (state, evaluation) = CreateProgram(
        [
            new(
                0,
                Gen5ShaderEncoding.Vop1,
                "VMovB32",
                [0],
                [Gen5Operand.Vector(0)],
                [Gen5Operand.Vector(1)],
                null),
            EndProgram(4),
        ]);

        Assert.True(
            Gen5SpirvTranslator.TryCompileComputeShader(
                state,
                evaluation,
                8,
                4,
                2,
                out var shader,
                out var error),
            error);
        Assert.Equal(
            1,
            CountStorageClassVariables(
                shader.Spirv,
                SpirvStorageClass.PushConstant));
        Assert.True(ContainsOpcode(shader.Spirv, SpirvOp.ULessThan));
    }

    [Theory]
    [InlineData(65u, 1u, 1u, 128u)]
    [InlineData(96u, 1u, 1u, 128u)]
    [InlineData(128u, 1u, 1u, 128u)]
    [InlineData(8u, 8u, 2u, 128u)]
    [InlineData(256u, 1u, 1u, 256u)]
    public void Wave64IsolatesMultipleGuestWavesInOneWorkgroup(
        uint localSizeX,
        uint localSizeY,
        uint localSizeZ,
        uint expectedScratchDwords)
    {
        var (state, evaluation) = CreateLaneProgram();

        Assert.True(
            Gen5SpirvTranslator.TryCompileComputeShader(
                state,
                evaluation,
                localSizeX,
                localSizeY,
                localSizeZ,
                out var shader,
                out var error,
                waveLaneCount: 64),
            error);
        Assert.True(ContainsOpcode(shader.Spirv, SpirvOp.ControlBarrier));
        Assert.Equal(
            expectedScratchDwords,
            GetSingleWorkgroupArrayLength(shader.Spirv));
    }

    [Theory]
    [InlineData(0u)]
    [InlineData(1u)]
    [InlineData(16u)]
    [InlineData(48u)]
    [InlineData(128u)]
    public void ComputeCompilerRejectsInvalidGuestWaveSizes(uint waveLaneCount)
    {
        var (state, evaluation) = CreateLaneProgram();

        Assert.False(
            Gen5SpirvTranslator.TryCompileComputeShader(
                state,
                evaluation,
                32,
                1,
                1,
                out _,
                out var error,
                waveLaneCount: waveLaneCount));
        Assert.Contains("wave lane count must be 32 or 64", error, StringComparison.Ordinal);
    }

    private static byte[] CompileLaneProgram(
        uint waveLaneCount,
        uint localSizeX,
        uint localSizeY,
        uint localSizeZ)
    {
        var (state, evaluation) = CreateLaneProgram();
        Assert.True(
            Gen5SpirvTranslator.TryCompileComputeShader(
                state,
                evaluation,
                localSizeX,
                localSizeY,
                localSizeZ,
                out var shader,
                out var error,
                waveLaneCount: waveLaneCount),
            error);
        return shader.Spirv;
    }

    private static (
        Gen5ShaderState State,
        Gen5ShaderEvaluation Evaluation) CreateLaneProgram()
    {
        var instructions = new Gen5ShaderInstruction[]
        {
            new(
                0,
                Gen5ShaderEncoding.Vop1,
                "VReadlaneB32",
                [0],
                [Gen5Operand.Vector(0), Gen5Operand.Scalar(0)],
                [Gen5Operand.Scalar(1)],
                null),
            new(
                4,
                Gen5ShaderEncoding.Vop1,
                "VReadfirstlaneB32",
                [0],
                [Gen5Operand.Vector(1)],
                [Gen5Operand.Scalar(2)],
                null),
            new(
                8,
                Gen5ShaderEncoding.Vop1,
                "VWritelaneB32",
                [0],
                [Gen5Operand.Scalar(3), Gen5Operand.Scalar(4)],
                [Gen5Operand.Vector(2)],
                null),
            EndProgram(12),
        };
        return CreateProgram(instructions);
    }

    private static (
        Gen5ShaderState State,
        Gen5ShaderEvaluation Evaluation) CreateProgram(
            IReadOnlyList<Gen5ShaderInstruction> instructions)
    {
        var state = new Gen5ShaderState(
            new Gen5ShaderProgram(0x1000, instructions),
            [],
            Metadata: null);
        var evaluation = new Gen5ShaderEvaluation(
            [],
            [],
            new Dictionary<uint, IReadOnlyList<uint>>(),
            [],
            []);
        return (state, evaluation);
    }

    private static Gen5ShaderInstruction EndProgram(uint pc) =>
        new(
            pc,
            Gen5ShaderEncoding.Sopp,
            "SEndpgm",
            [0],
            [],
            [],
            null);

    private static bool ContainsOpcode(byte[] spirv, SpirvOp expected)
    {
        foreach (var instruction in EnumerateInstructions(spirv))
        {
            if (instruction.Opcode == expected)
            {
                return true;
            }
        }

        return false;
    }

    private static bool ContainsCapability(
        byte[] spirv,
        SpirvCapability expected)
    {
        foreach (var instruction in EnumerateInstructions(spirv))
        {
            if (instruction.Opcode == SpirvOp.Capability &&
                instruction.Operands.Length >= 1 &&
                instruction.Operands[0] == (uint)expected)
            {
                return true;
            }
        }

        return false;
    }

    private static bool ContainsBuiltIn(byte[] spirv, SpirvBuiltIn builtIn)
    {
        foreach (var instruction in EnumerateInstructions(spirv))
        {
            if (instruction.Opcode == SpirvOp.Decorate &&
                instruction.Operands.Length >= 3 &&
                instruction.Operands[1] == (uint)SpirvDecoration.BuiltIn &&
                instruction.Operands[2] == (uint)builtIn)
            {
                return true;
            }
        }

        return false;
    }

    private static bool ContainsWorkgroupVariable(byte[] spirv)
        => CountStorageClassVariables(
            spirv,
            SpirvStorageClass.Workgroup) != 0;

    private static int CountWorkgroupVariables(byte[] spirv)
        => CountStorageClassVariables(
            spirv,
            SpirvStorageClass.Workgroup);

    private static uint GetSingleWorkgroupArrayLength(byte[] spirv)
    {
        var instructions = EnumerateInstructions(spirv).ToArray();
        var variable = Assert.Single(instructions, instruction =>
            instruction.Opcode == SpirvOp.Variable &&
            instruction.Operands.Length >= 3 &&
            instruction.Operands[2] == (uint)SpirvStorageClass.Workgroup);
        var pointerType = Assert.Single(instructions, instruction =>
            instruction.Opcode == SpirvOp.TypePointer &&
            instruction.Operands.Length >= 3 &&
            instruction.Operands[0] == variable.Operands[0]);
        var arrayType = Assert.Single(instructions, instruction =>
            instruction.Opcode == SpirvOp.TypeArray &&
            instruction.Operands.Length >= 3 &&
            instruction.Operands[0] == pointerType.Operands[2]);
        var length = Assert.Single(instructions, instruction =>
            instruction.Opcode == SpirvOp.Constant &&
            instruction.Operands.Length >= 3 &&
            instruction.Operands[1] == arrayType.Operands[2]);
        return length.Operands[2];
    }

    private static int CountStorageClassVariables(
        byte[] spirv,
        SpirvStorageClass storageClass)
    {
        var count = 0;
        foreach (var instruction in EnumerateInstructions(spirv))
        {
            if (instruction.Opcode == SpirvOp.Variable &&
                instruction.Operands.Length >= 3 &&
                instruction.Operands[2] == (uint)storageClass)
            {
                count++;
            }
        }

        return count;
    }

    private static IEnumerable<ParsedInstruction> EnumerateInstructions(byte[] spirv)
    {
        for (var offset = 5 * sizeof(uint);
             offset + sizeof(uint) <= spirv.Length;)
        {
            var header = BinaryPrimitives.ReadUInt32LittleEndian(
                spirv.AsSpan(offset, sizeof(uint)));
            var wordCount = Math.Max((int)(header >> 16), 1);
            var operands = new uint[wordCount - 1];
            for (var index = 0; index < operands.Length; index++)
            {
                operands[index] = BinaryPrimitives.ReadUInt32LittleEndian(
                    spirv.AsSpan(
                        offset + ((index + 1) * sizeof(uint)),
                        sizeof(uint)));
            }

            yield return new ParsedInstruction((SpirvOp)(ushort)header, operands);
            offset += wordCount * sizeof(uint);
        }
    }

    private readonly record struct ParsedInstruction(
        SpirvOp Opcode,
        uint[] Operands);
}
