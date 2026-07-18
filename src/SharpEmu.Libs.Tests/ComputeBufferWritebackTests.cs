// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using SharpEmu.Libs.Gpu;
using SharpEmu.Libs.VideoOut;
using SharpEmu.ShaderCompiler;
using Xunit;

namespace SharpEmu.Libs.Tests;

public sealed class ComputeBufferWritebackTests
{
    private const ulong BufferAddress = 0x2000_0000;

    [Theory]
    [InlineData("GlobalStoreDword")]
    [InlineData("GlobalAtomicAdd")]
    public void GlobalWritesAreMarkedWritable(string opcode)
    {
        var control = new Gen5GlobalMemoryControl(
            DwordCount: 1,
            VectorAddress: 0,
            VectorData: 1,
            VectorDestination: 0,
            ScalarAddress: 0,
            OffsetBytes: 0,
            Glc: false,
            Slc: false);
        var evaluation = Evaluate(
            opcode,
            control,
            [
                unchecked((uint)BufferAddress),
                (uint)(BufferAddress >> 32),
            ]);

        Assert.True(Assert.Single(evaluation.GlobalMemoryBindings).Writable);
    }

    [Theory]
    [InlineData("BufferStoreDword")]
    [InlineData("BufferAtomicAdd")]
    public void DescriptorBufferWritesAreMarkedWritable(string opcode)
    {
        var control = new Gen5BufferMemoryControl(
            DwordCount: 1,
            VectorAddress: 0,
            VectorData: 1,
            ScalarResource: 0,
            OffsetBytes: 0,
            IndexEnabled: false,
            OffsetEnabled: false,
            Glc: false,
            Slc: false);
        var evaluation = Evaluate(
            opcode,
            control,
            [
                unchecked((uint)BufferAddress),
                (uint)(BufferAddress >> 32),
                16,
                0,
            ]);

        Assert.True(Assert.Single(evaluation.GlobalMemoryBindings).Writable);
    }

    [Fact]
    public void GlobalLoadThenStoreUpgradesTheCanonicalBindingToWritable()
    {
        var control = new Gen5GlobalMemoryControl(
            DwordCount: 1,
            VectorAddress: 0,
            VectorData: 1,
            VectorDestination: 0,
            ScalarAddress: 0,
            OffsetBytes: 0,
            Glc: false,
            Slc: false);
        var evaluation = Evaluate(
            [
                ("GlobalLoadDword", control),
                ("GlobalStoreDword", control),
            ],
            [
                unchecked((uint)BufferAddress),
                (uint)(BufferAddress >> 32),
            ]);

        var binding = Assert.Single(evaluation.GlobalMemoryBindings);
        Assert.True(binding.Writable);
        Assert.Equal(2, binding.InstructionPcs.Count);
    }

    [Fact]
    public void DescriptorLoadThenAtomicUpgradesTheCanonicalBindingToWritable()
    {
        var control = new Gen5BufferMemoryControl(
            DwordCount: 1,
            VectorAddress: 0,
            VectorData: 1,
            ScalarResource: 0,
            OffsetBytes: 0,
            IndexEnabled: false,
            OffsetEnabled: false,
            Glc: false,
            Slc: false);
        var evaluation = Evaluate(
            [
                ("BufferLoadDword", control),
                ("BufferAtomicAdd", control),
            ],
            [
                unchecked((uint)BufferAddress),
                (uint)(BufferAddress >> 32),
                16,
                0,
            ]);

        var binding = Assert.Single(evaluation.GlobalMemoryBindings);
        Assert.True(binding.Writable);
        Assert.Equal(2, binding.InstructionPcs.Count);
    }

    [Theory]
    [InlineData("GlobalLoadDword")]
    [InlineData("BufferLoadDword")]
    public void LoadOnlyBindingsRemainReadOnly(string opcode)
    {
        Gen5InstructionControl control;
        IReadOnlyList<uint> scalarRegisters;
        if (opcode.StartsWith("Global", StringComparison.Ordinal))
        {
            control = new Gen5GlobalMemoryControl(
                DwordCount: 1,
                VectorAddress: 0,
                VectorData: 1,
                VectorDestination: 0,
                ScalarAddress: 0,
                OffsetBytes: 0,
                Glc: false,
                Slc: false);
            scalarRegisters =
            [
                unchecked((uint)BufferAddress),
                (uint)(BufferAddress >> 32),
            ];
        }
        else
        {
            control = new Gen5BufferMemoryControl(
                DwordCount: 1,
                VectorAddress: 0,
                VectorData: 1,
                ScalarResource: 0,
                OffsetBytes: 0,
                IndexEnabled: false,
                OffsetEnabled: false,
                Glc: false,
                Slc: false);
            scalarRegisters =
            [
                unchecked((uint)BufferAddress),
                (uint)(BufferAddress >> 32),
                16,
                0,
            ];
        }

        var evaluation = Evaluate(opcode, control, scalarRegisters);

        Assert.False(Assert.Single(evaluation.GlobalMemoryBindings).Writable);
    }

    [Fact]
    public void ReadOnlyBuffersDoNotMakeAComputeDispatchObservable()
    {
        Assert.False(
            VulkanVideoPresenter.HasComputeOutput(
                [],
                [new GuestMemoryBuffer(BufferAddress, new byte[16])]));
        Assert.True(
            VulkanVideoPresenter.HasComputeOutput(
                [],
                [
                    new GuestMemoryBuffer(
                        BufferAddress,
                        new byte[16],
                        Writable: true),
                ]));
    }

    [Fact]
    public void StorageImagesStillMakeAComputeDispatchObservable()
    {
        Assert.True(
            VulkanVideoPresenter.HasComputeOutput(
                [
                    new GuestDrawTexture(
                        Address: BufferAddress,
                        Width: 1,
                        Height: 1,
                        Format: 10,
                        NumberType: 0,
                        RgbaPixels: [],
                        IsFallback: false,
                        IsStorage: true),
                ],
                []));
    }

    [Fact]
    public void RejectedGuestWorkCannotBeWaitedOn()
    {
        Assert.False(
            VulkanVideoPresenter.WaitForGuestWork(
                workSequence: 0,
                TimeSpan.Zero));
    }

    [Fact]
    public void WritebackPublishesOnlyShaderChangedBytes()
    {
        var original = new byte[] { 0, 1, 2, 3, 4, 5 };
        var guest = new FakeGuestMemory();
        guest.AddRegion(BufferAddress, original.ToArray());
        Assert.True(guest.TryWrite(BufferAddress + 1, [0xEE]));
        var output = original.ToArray();
        output[2] = 0xAA;
        output[3] = 0xBB;
        var buffer = new GuestMemoryBuffer(
            BufferAddress,
            original,
            Writable: true,
            guest);

        Assert.True(
            VulkanVideoPresenter.TryWriteBackGlobalBuffer(
                buffer,
                output,
                out var changedBytes));

        Assert.Equal(2, changedBytes);
        var actual = new byte[original.Length];
        Assert.True(guest.TryRead(BufferAddress, actual));
        Assert.Equal([0, 0xEE, 0xAA, 0xBB, 4, 5], actual);
    }

    [Fact]
    public void WritebackHandlesPageBoundariesWithoutClobberingLiveBytes()
    {
        var original = new byte[4098];
        var output = original.ToArray();
        output[4095] = 0xAA;
        output[4096] = 0xBB;
        var guest = new FakeGuestMemory();
        guest.AddRegion(BufferAddress, original.ToArray());
        Assert.True(guest.TryWrite(BufferAddress, [0x11]));
        Assert.True(guest.TryWrite(BufferAddress + 4097, [0x22]));
        var buffer = new GuestMemoryBuffer(
            BufferAddress,
            original,
            Writable: true,
            guest);

        Assert.True(
            VulkanVideoPresenter.TryWriteBackGlobalBuffer(
                buffer,
                output,
                out var changedBytes));

        Assert.Equal(2, changedBytes);
        var actual = new byte[original.Length];
        Assert.True(guest.TryRead(BufferAddress, actual));
        Assert.Equal(0x11, actual[0]);
        Assert.Equal(0xAA, actual[4095]);
        Assert.Equal(0xBB, actual[4096]);
        Assert.Equal(0x22, actual[4097]);
    }

    [Fact]
    public void ReadOnlyWritebackIsANoOp()
    {
        var original = new byte[] { 1, 2, 3, 4 };
        var guest = new FakeGuestMemory();
        guest.AddRegion(BufferAddress, original.ToArray());
        var buffer = new GuestMemoryBuffer(
            BufferAddress,
            original,
            Writable: false,
            guest);

        Assert.True(
            VulkanVideoPresenter.TryWriteBackGlobalBuffer(
                buffer,
                [9, 9, 9, 9],
                out var changedBytes));

        Assert.Equal(0, changedBytes);
        var actual = new byte[original.Length];
        Assert.True(guest.TryRead(BufferAddress, actual));
        Assert.Equal(original, actual);
    }

    [Fact]
    public void WritableWritebackRequiresACompleteGuestBacking()
    {
        var original = new byte[] { 1, 2, 3, 4 };
        var output = new byte[] { 9, 2, 3, 4 };
        var guest = new FakeGuestMemory();
        guest.AddRegion(BufferAddress, original.ToArray());

        Assert.False(
            VulkanVideoPresenter.TryWriteBackGlobalBuffer(
                new GuestMemoryBuffer(
                    BufferAddress,
                    original,
                    Writable: true),
                output,
                out _));
        Assert.False(
            VulkanVideoPresenter.TryWriteBackGlobalBuffer(
                new GuestMemoryBuffer(
                    BufferAddress,
                    original,
                    Writable: true,
                    guest),
                output.AsSpan(0, output.Length - 1),
                out _));
        Assert.False(
            VulkanVideoPresenter.TryWriteBackGlobalBuffer(
                new GuestMemoryBuffer(
                    BaseAddress: 0,
                    original,
                    Writable: true,
                    guest),
                output,
                out _));

        var actual = new byte[original.Length];
        Assert.True(guest.TryRead(BufferAddress, actual));
        Assert.Equal(original, actual);
    }

    private static Gen5ShaderEvaluation Evaluate(
        string opcode,
        Gen5InstructionControl control,
        IReadOnlyList<uint> scalarRegisters) =>
        Evaluate([(opcode, control)], scalarRegisters);

    private static Gen5ShaderEvaluation Evaluate(
        IReadOnlyList<(string Opcode, Gen5InstructionControl Control)> operations,
        IReadOnlyList<uint> scalarRegisters)
    {
        var instructions = new Gen5ShaderInstruction[operations.Count];
        for (var index = 0; index < operations.Count; index++)
        {
            var operation = operations[index];
            instructions[index] = new Gen5ShaderInstruction(
                Pc: checked((uint)(index * 8)),
                Encoding: operation.Control is Gen5GlobalMemoryControl
                    ? Gen5ShaderEncoding.Flat
                    : Gen5ShaderEncoding.Mubuf,
                operation.Opcode,
                Words: [],
                Sources: [],
                Destinations: [],
                operation.Control);
        }

        var state = new Gen5ShaderState(
            new Gen5ShaderProgram(BufferAddress + 0x1000, instructions),
            scalarRegisters,
            Metadata: null);
        var memory = new FakeGuestMemory();
        memory.AddRegion(BufferAddress, new byte[64]);
        var context = new CpuContext(memory, Generation.Gen5);

        Assert.True(
            Gen5ShaderScalarEvaluator.TryEvaluate(
                context,
                state,
                out var evaluation,
                out var error),
            error);
        return evaluation;
    }
}
