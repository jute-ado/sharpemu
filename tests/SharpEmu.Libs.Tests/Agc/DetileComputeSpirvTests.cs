// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using SharpEmu.ShaderCompiler.Vulkan;
using Xunit;

namespace SharpEmu.Libs.Tests.Agc;

public sealed class DetileComputeSpirvTests
{
    private const uint SpirvMagic = 0x0723_0203;
    private const uint SpirvVersion15 = 0x0001_0500;
    private const ushort OpEntryPoint = 15;
    private const ushort OpExecutionMode = 16;
    private const ushort OpTypeRuntimeArray = 29;
    private const ushort OpTypeStruct = 30;
    private const ushort OpFunction = 54;
    private const ushort OpFunctionEnd = 56;
    private const ushort OpDecorate = 71;
    private const ushort OpMemberDecorate = 72;
    private const uint ExecutionModelGlCompute = 5;
    private const uint ExecutionModeLocalSize = 17;
    private const uint DecorationBinding = 33;
    private const uint DecorationDescriptorSet = 34;
    private const uint DecorationOffset = 35;

    [Fact]
    public void CreateDetileComputeEmitsWellFormedComputeModule()
    {
        var spirv = SpirvFixedShaders.CreateDetileCompute();

        Assert.Equal(0, spirv.Length % sizeof(uint));
        var words = new uint[spirv.Length / sizeof(uint)];
        for (var index = 0; index < words.Length; index++)
        {
            words[index] = BinaryPrimitives.ReadUInt32LittleEndian(
                spirv.AsSpan(index * sizeof(uint)));
        }

        Assert.True(words.Length > 5);
        Assert.Equal(SpirvMagic, words[0]);
        Assert.Equal(SpirvVersion15, words[1]);
        Assert.True(words[3] > 1);

        var sawComputeEntry = false;
        var sawLocalSize = false;
        var sawRuntimeArray = false;
        var functionCount = 0;
        var functionEndCount = 0;
        uint pushConstantStruct = 0;
        var bindings = new List<uint>();
        var descriptorSets = new List<uint>();
        var memberOffsets = new List<(uint Struct, uint Member, uint Offset)>();
        var offset = 5;
        while (offset < words.Length)
        {
            var instruction = words[offset];
            var wordCount = (int)(instruction >> 16);
            var opcode = (ushort)instruction;
            Assert.True(wordCount >= 1);
            Assert.True(offset + wordCount <= words.Length);

            switch (opcode)
            {
                case OpEntryPoint
                    when words[offset + 1] == ExecutionModelGlCompute:
                    sawComputeEntry = true;
                    break;
                case OpExecutionMode
                    when wordCount >= 6 &&
                         words[offset + 2] == ExecutionModeLocalSize &&
                         words[offset + 3] == 8 &&
                         words[offset + 4] == 8 &&
                         words[offset + 5] == 1:
                    sawLocalSize = true;
                    break;
                case OpTypeRuntimeArray:
                    sawRuntimeArray = true;
                    break;
                case OpTypeStruct when wordCount == 13:
                    pushConstantStruct = words[offset + 1];
                    break;
                case OpFunction:
                    functionCount++;
                    break;
                case OpFunctionEnd:
                    functionEndCount++;
                    break;
                case OpDecorate when wordCount == 4:
                    if (words[offset + 2] == DecorationBinding)
                    {
                        bindings.Add(words[offset + 3]);
                    }
                    else if (words[offset + 2] == DecorationDescriptorSet)
                    {
                        descriptorSets.Add(words[offset + 3]);
                    }

                    break;
                case OpMemberDecorate
                    when wordCount == 5 &&
                         words[offset + 3] == DecorationOffset:
                    memberOffsets.Add((
                        words[offset + 1],
                        words[offset + 2],
                        words[offset + 4]));
                    break;
            }

            offset += wordCount;
        }

        Assert.Equal(words.Length, offset);
        Assert.True(sawComputeEntry);
        Assert.True(sawLocalSize);
        Assert.True(sawRuntimeArray);
        Assert.NotEqual(0u, pushConstantStruct);
        Assert.Equal([0u, 1u, 2u, 3u], bindings.Order());
        Assert.Equal([0u, 0u, 0u, 0u], descriptorSets.Order());
        Assert.Equal(
            Enumerable.Range(0, 11).Select(index => (uint)(index * sizeof(uint))),
            memberOffsets
                .Where(item => item.Struct == pushConstantStruct)
                .OrderBy(item => item.Member)
                .Select(item => item.Offset));
        Assert.Equal(1, functionCount);
        Assert.Equal(1, functionEndCount);
    }
}
