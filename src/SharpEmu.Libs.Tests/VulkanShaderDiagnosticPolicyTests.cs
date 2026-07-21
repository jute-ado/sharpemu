// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.VideoOut;
using SharpEmu.Libs.Gpu;
using SharpEmu.ShaderCompiler.Vulkan;
using Xunit;

namespace SharpEmu.Libs.Tests;

public sealed class VulkanShaderDiagnosticPolicyTests
{
    [Theory]
    [InlineData(7u, true)]
    [InlineData(17u, true)]
    [InlineData(4u, false)]
    public void ProsperoAndLegacyRectangleListsAreRecognized(
        uint primitiveType,
        bool expected)
    {
        Assert.Equal(
            expected,
            VulkanVideoPresenter.IsRectangleListPrimitive(primitiveType));
    }

    [Theory]
    [InlineData(7u, 3u, 4u)]
    [InlineData(17u, 3u, 4u)]
    [InlineData(4u, 3u, 3u)]
    public void NonIndexedRectangleListsSynthesizeFourthVertex(
        uint primitiveType,
        uint requestedVertexCount,
        uint expectedVertexCount)
    {
        Assert.Equal(
            expectedVertexCount,
            GuestPrimitivePolicy.GetDrawVertexCount(
                primitiveType,
                requestedVertexCount,
                indexed: false));
    }

    [Theory]
    [InlineData(false, false, false, false, false, false)]
    [InlineData(false, false, false, false, true, true)]
    [InlineData(false, false, false, true, false, true)]
    [InlineData(false, false, true, false, false, true)]
    [InlineData(true, true, false, false, false, true)]
    [InlineData(false, true, false, false, false, false)]
    public void SolidFragmentOverrideCombinesGlobalTitleTargetAndShaderScopes(
        bool isTitleDraw,
        bool forceTitleDraw,
        bool forceAll,
        bool targetMatched,
        bool shaderMatched,
        bool expected)
    {
        Assert.Equal(
            expected,
            VulkanVideoPresenter.ShouldForceSolidFragmentOverride(
                isTitleDraw,
                forceTitleDraw,
                forceAll,
                targetMatched,
                shaderMatched));
    }

    [Fact]
    public void ArrayCopyFragmentSamplesRequestedBindingWithLayerCoordinate()
    {
        var spirv = SpirvFixedShaders.CreateArrayCopyFragment(binding: 2);
        var instructions = ReadInstructions(spirv).ToArray();

        Assert.Contains(
            instructions,
            instruction =>
                (ushort)instruction[0] == (ushort)SpirvOp.TypeImage &&
                instruction[3] == (uint)SpirvImageDim.Dim2D &&
                instruction[5] == 1u);
        Assert.Contains(
            instructions,
            instruction =>
                (ushort)instruction[0] == (ushort)SpirvOp.Decorate &&
                instruction[2] == (uint)SpirvDecoration.Binding &&
                instruction[3] == 2u);
        Assert.Contains(
            instructions,
            instruction =>
                (ushort)instruction[0] ==
                    (ushort)SpirvOp.ImageSampleExplicitLod);
    }

    [Fact]
    public void ArrayFetchFragmentFetchesRequestedBindingAtFragmentCoordinate()
    {
        var spirv = SpirvFixedShaders.CreateArrayFetchFragment(binding: 2);
        var instructions = ReadInstructions(spirv).ToArray();

        Assert.Contains(
            instructions,
            instruction =>
                (ushort)instruction[0] == (ushort)SpirvOp.Decorate &&
                instruction[2] == (uint)SpirvDecoration.Binding &&
                instruction[3] == 2u);
        Assert.Contains(
            instructions,
            instruction =>
                (ushort)instruction[0] == (ushort)SpirvOp.Decorate &&
                instruction[2] == (uint)SpirvDecoration.BuiltIn &&
                instruction[3] == (uint)SpirvBuiltIn.FragCoord);
        Assert.Contains(
            instructions,
            instruction =>
                (ushort)instruction[0] == (ushort)SpirvOp.ImageFetch);
    }

    private static IEnumerable<uint[]> ReadInstructions(byte[] spirv)
    {
        for (var offset = 5 * sizeof(uint); offset < spirv.Length;)
        {
            var firstWord = BitConverter.ToUInt32(spirv, offset);
            var wordCount = checked((int)(firstWord >> 16));
            Assert.True(wordCount > 0);
            yield return Enumerable.Range(0, wordCount)
                .Select(index => BitConverter.ToUInt32(
                    spirv,
                    offset + index * sizeof(uint)))
                .ToArray();
            offset += wordCount * sizeof(uint);
        }
    }
}
