// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using SharpEmu.ShaderCompiler;
using Xunit;

namespace SharpEmu.Libs.Tests.Agc;

public sealed class Gen5VertexInputEvaluationTests
{
    private const ulong MemoryBase = 0x1_0000_0000;
    private const ulong VertexBufferAddress = MemoryBase + 0x800;

    [Fact]
    public void IndexedVertexFetchFoldsStaticVectorOffset()
    {
        var moveOffset = new Gen5ShaderInstruction(
            0,
            Gen5ShaderEncoding.Vop1,
            "VMovB32",
            [],
            [Gen5Operand.Source(136)],
            [Gen5Operand.Vector(1)],
            null);
        var evaluation = Evaluate([moveOffset, CreateFetch(offsetEnabled: true)]);

        var input = Assert.Single(evaluation.VertexInputs!);
        Assert.Empty(evaluation.GlobalMemoryBindings);
        Assert.Equal(24u, input.OffsetBytes);
        Assert.Equal(VertexBufferAddress, input.BaseAddress);
        Assert.Equal(16u, input.Stride);
        Assert.Equal(6u, input.DataFormat);
        Assert.Equal(7u, input.NumberFormat);
    }

    [Fact]
    public void IndexedVertexFetchWithDynamicVectorOffsetRemainsGeneralBufferLoad()
    {
        var evaluation = Evaluate([CreateFetch(offsetEnabled: true)]);

        Assert.Empty(evaluation.VertexInputs!);
        Assert.Single(evaluation.GlobalMemoryBindings);
    }

    [Fact]
    public void InterveningVectorWriteInvalidatesStaticOffset()
    {
        var moveOffset = new Gen5ShaderInstruction(
            0,
            Gen5ShaderEncoding.Vop1,
            "VMovB32",
            [],
            [Gen5Operand.Source(136)],
            [Gen5Operand.Vector(1)],
            null);
        var overwrite = new Gen5ShaderInstruction(
            2,
            Gen5ShaderEncoding.Vop3,
            "VAddF32",
            [],
            [Gen5Operand.Vector(2), Gen5Operand.Vector(3)],
            [Gen5Operand.Vector(1), Gen5Operand.Vector(4)],
            null);
        var evaluation = Evaluate(
            [moveOffset, overwrite, CreateFetch(offsetEnabled: true)]);

        Assert.Empty(evaluation.VertexInputs!);
        Assert.Single(evaluation.GlobalMemoryBindings);
    }

    [Fact]
    public void IndexedVertexFetchWithoutVectorOffsetRemainsVertexInput()
    {
        var evaluation = Evaluate([CreateFetch(offsetEnabled: false)]);

        var input = Assert.Single(evaluation.VertexInputs!);
        Assert.Empty(evaluation.GlobalMemoryBindings);
        Assert.Equal(16u, input.OffsetBytes);
    }

    private static Gen5ShaderEvaluation Evaluate(
        IReadOnlyList<Gen5ShaderInstruction> instructions)
    {
        var end = new Gen5ShaderInstruction(
            12,
            Gen5ShaderEncoding.Sopp,
            "SEndpgm",
            [],
            [],
            [],
            null);
        var program = new Gen5ShaderProgram(
            0,
            [.. instructions, end]);
        var userData = new uint[]
        {
            unchecked((uint)VertexBufferAddress),
            (uint)(VertexBufferAddress >> 32) | (16u << 16),
            4,
            36u << 12,
        };
        var memory = new FakeCpuMemory(MemoryBase, 0x1000);
        Assert.True(memory.TryWrite(VertexBufferAddress, new byte[64]));
        var context = new CpuContext(memory, Generation.Gen5);
        var state = new Gen5ShaderState(program, userData, Metadata: null);

        Assert.True(
            Gen5ShaderScalarEvaluator.TryEvaluate(
                context,
                state,
                out var evaluation,
                out var error,
                resolveVertexInputs: true),
            error);
        return evaluation;
    }

    private static Gen5ShaderInstruction CreateFetch(bool offsetEnabled) =>
        new(
            4,
            Gen5ShaderEncoding.Mubuf,
            "BufferLoadFormatXy",
            [],
            [
                Gen5Operand.Vector(0),
                Gen5Operand.Scalar(0),
                Gen5Operand.Source(132),
            ],
            [Gen5Operand.Vector(8), Gen5Operand.Vector(9)],
            new Gen5BufferMemoryControl(
                DwordCount: 2,
                VectorAddress: 0,
                VectorData: 8,
                ScalarResource: 0,
                OffsetBytes: 12,
                IndexEnabled: true,
                OffsetEnabled: offsetEnabled,
                Glc: false,
                Slc: false));
}
