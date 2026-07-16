// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using SharpEmu.HLE;
using SharpEmu.Libs.Audio;
using Xunit;

namespace SharpEmu.Libs.Tests;

public sealed class AudioOut2CompatibilityTests
{
    private const ulong BufferAddress = 0x1000;
    private const ulong ParameterAddress = 0x2000;
    private const ulong ContextMemoryAddress = 0x3000;
    private const byte Canary = 0xA5;

    [Fact]
    public void ContextResetParamWritesExactStructureWithoutTouchingCanaries()
    {
        var memory = new FakeGuestMemory();
        var allocation = FilledBuffer(0x50);
        memory.AddRegion(BufferAddress, allocation);
        var context = CreateContext(memory);
        context[CpuRegister.Rdi] = BufferAddress + 8;

        AssertSuccess(AudioOut2Exports.AudioOut2ContextResetParam(context), context);

        Assert.All(allocation.AsSpan(0, 8).ToArray(), value => Assert.Equal(Canary, value));
        var parameter = allocation.AsSpan(8, 0x30);
        Assert.Equal(0x30U, BinaryPrimitives.ReadUInt32LittleEndian(parameter[0x00..]));
        Assert.Equal(2U, BinaryPrimitives.ReadUInt32LittleEndian(parameter[0x04..]));
        Assert.Equal(48_000U, BinaryPrimitives.ReadUInt32LittleEndian(parameter[0x08..]));
        Assert.Equal(0x400U, BinaryPrimitives.ReadUInt32LittleEndian(parameter[0x0C..]));
        Assert.All(parameter[0x10..].ToArray(), value => Assert.Equal(0, value));
        Assert.All(allocation.AsSpan(0x38).ToArray(), value => Assert.Equal(Canary, value));
    }

    [Fact]
    public void ContextResetParamReportsPointerErrorsInBothReturnChannels()
    {
        var context = CreateContext(new FakeGuestMemory());

        AssertError(
            AudioOut2Exports.AudioOut2ContextResetParam(context),
            context,
            OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);

        context[CpuRegister.Rdi] = ParameterAddress;
        AssertError(
            AudioOut2Exports.AudioOut2ContextResetParam(context),
            context,
            OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
    }

    [Fact]
    public void ContextQueryMemoryWritesExactSizeWithoutTouchingCanaries()
    {
        var memory = new FakeGuestMemory();
        var allocation = FilledBuffer(24);
        memory.AddRegion(BufferAddress, allocation);
        var context = CreateContext(memory);
        context[CpuRegister.Rdi] = ParameterAddress;
        context[CpuRegister.Rsi] = BufferAddress + 8;

        AssertSuccess(AudioOut2Exports.AudioOut2ContextQueryMemory(context), context);

        Assert.All(allocation.AsSpan(0, 8).ToArray(), value => Assert.Equal(Canary, value));
        Assert.Equal(
            0x1_0000UL,
            BinaryPrimitives.ReadUInt64LittleEndian(allocation.AsSpan(8, 8)));
        Assert.All(allocation.AsSpan(16).ToArray(), value => Assert.Equal(Canary, value));
    }

    [Theory]
    [InlineData(0, BufferAddress)]
    [InlineData(ParameterAddress, 0)]
    public void ContextQueryMemoryRejectsNullPointers(ulong parameter, ulong output)
    {
        var context = CreateContext(new FakeGuestMemory());
        context[CpuRegister.Rdi] = parameter;
        context[CpuRegister.Rsi] = output;

        AssertError(
            AudioOut2Exports.AudioOut2ContextQueryMemory(context),
            context,
            OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
    }

    [Fact]
    public void ContextQueryMemoryReportsUnmappedOutput()
    {
        var context = CreateContext(new FakeGuestMemory());
        context[CpuRegister.Rdi] = ParameterAddress;
        context[CpuRegister.Rsi] = BufferAddress;

        AssertError(
            AudioOut2Exports.AudioOut2ContextQueryMemory(context),
            context,
            OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
    }

    [Fact]
    public void ContextCreateWritesUniqueHandlesWithoutTouchingCanaries()
    {
        var memory = new FakeGuestMemory();
        var allocation = FilledBuffer(24);
        memory.AddRegion(BufferAddress, allocation);
        var context = CreateContext(memory);
        ConfigureContextCreate(context, memorySize: 0x1_0000, BufferAddress + 8);

        AssertSuccess(AudioOut2Exports.AudioOut2ContextCreate(context), context);
        var firstHandle = BinaryPrimitives.ReadUInt64LittleEndian(allocation.AsSpan(8, 8));
        Assert.NotEqual(0UL, firstHandle);

        AssertSuccess(AudioOut2Exports.AudioOut2ContextCreate(context), context);
        var secondHandle = BinaryPrimitives.ReadUInt64LittleEndian(allocation.AsSpan(8, 8));
        Assert.True(secondHandle > firstHandle);
        Assert.All(allocation.AsSpan(0, 8).ToArray(), value => Assert.Equal(Canary, value));
        Assert.All(allocation.AsSpan(16).ToArray(), value => Assert.Equal(Canary, value));
    }

    [Fact]
    public void ContextCreateRejectsMemorySmallerThanQueriedRequirement()
    {
        var memory = new FakeGuestMemory();
        memory.AddRegion(BufferAddress, new byte[8]);
        var context = CreateContext(memory);
        ConfigureContextCreate(context, memorySize: 0xFFFF, BufferAddress);

        AssertError(
            AudioOut2Exports.AudioOut2ContextCreate(context),
            context,
            OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
    }

    [Theory]
    [InlineData(0, ContextMemoryAddress, 0x1_0000, BufferAddress)]
    [InlineData(ParameterAddress, 0, 0x1_0000, BufferAddress)]
    [InlineData(ParameterAddress, ContextMemoryAddress, 0, BufferAddress)]
    [InlineData(ParameterAddress, ContextMemoryAddress, 0x1_0000, 0)]
    public void ContextCreateRejectsNullRequiredArguments(
        ulong parameter,
        ulong memory,
        ulong memorySize,
        ulong output)
    {
        var context = CreateContext(new FakeGuestMemory());
        context[CpuRegister.Rdi] = parameter;
        context[CpuRegister.Rsi] = memory;
        context[CpuRegister.Rdx] = memorySize;
        context[CpuRegister.Rcx] = output;

        AssertError(
            AudioOut2Exports.AudioOut2ContextCreate(context),
            context,
            OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
    }

    [Fact]
    public void ContextCreateReportsUnmappedOutput()
    {
        var context = CreateContext(new FakeGuestMemory());
        ConfigureContextCreate(context, memorySize: 0x1_0000, BufferAddress);

        AssertError(
            AudioOut2Exports.AudioOut2ContextCreate(context),
            context,
            OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
    }

    [Fact]
    public void PortCreateWritesEncodedHandleWithoutTouchingCanaries()
    {
        var memory = new FakeGuestMemory();
        var allocation = FilledBuffer(24);
        memory.AddRegion(BufferAddress, allocation);
        var context = CreateContext(memory);
        ConfigurePortCreate(context, type: 2, BufferAddress + 8);

        AssertSuccess(AudioOut2Exports.AudioOut2PortCreate(context), context);

        var handle = BinaryPrimitives.ReadUInt64LittleEndian(allocation.AsSpan(8, 8));
        Assert.Equal(0x2000_0000UL, handle & 0xFF00_0000UL);
        Assert.Equal(2UL, (handle >> 16) & 0xFF);
        Assert.All(allocation.AsSpan(0, 8).ToArray(), value => Assert.Equal(Canary, value));
        Assert.All(allocation.AsSpan(16).ToArray(), value => Assert.Equal(Canary, value));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(256)]
    public void PortCreateRejectsUnsupportedTypes(int type)
    {
        var context = CreateContext(new FakeGuestMemory());
        ConfigurePortCreate(context, type, BufferAddress);

        AssertError(
            AudioOut2Exports.AudioOut2PortCreate(context),
            context,
            OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
    }

    [Theory]
    [InlineData(0, BufferAddress, ContextMemoryAddress)]
    [InlineData(ParameterAddress, 0, ContextMemoryAddress)]
    [InlineData(ParameterAddress, BufferAddress, 0)]
    public void PortCreateRejectsNullRequiredPointers(
        ulong parameter,
        ulong output,
        ulong contextHandle)
    {
        var context = CreateContext(new FakeGuestMemory());
        context[CpuRegister.Rdi] = 0;
        context[CpuRegister.Rsi] = parameter;
        context[CpuRegister.Rdx] = output;
        context[CpuRegister.Rcx] = contextHandle;

        AssertError(
            AudioOut2Exports.AudioOut2PortCreate(context),
            context,
            OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
    }

    [Fact]
    public void PortCreateReportsUnmappedOutput()
    {
        var context = CreateContext(new FakeGuestMemory());
        ConfigurePortCreate(context, type: 0, BufferAddress);

        AssertError(
            AudioOut2Exports.AudioOut2PortCreate(context),
            context,
            OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
    }

    [Theory]
    [InlineData(0x2000_0001UL, 1, 2)]
    [InlineData(0x2002_0001UL, 0x40, 1)]
    public void PortGetStateWritesExactLayoutAndPreservesTail(
        ulong handle,
        ushort expectedOutput,
        byte expectedChannels)
    {
        var memory = new FakeGuestMemory();
        var allocation = FilledBuffer(0x30);
        memory.AddRegion(BufferAddress, allocation);
        var context = CreateContext(memory);
        context[CpuRegister.Rdi] = handle;
        context[CpuRegister.Rsi] = BufferAddress;

        AssertSuccess(AudioOut2Exports.AudioOut2PortGetState(context), context);

        Assert.Equal(expectedOutput, BinaryPrimitives.ReadUInt16LittleEndian(allocation));
        Assert.Equal(expectedChannels, allocation[0x02]);
        Assert.Equal(0, allocation[0x03]);
        Assert.Equal(-1, BinaryPrimitives.ReadInt16LittleEndian(allocation.AsSpan(0x04)));
        Assert.All(allocation.AsSpan(0x06, 0x1A).ToArray(), value => Assert.Equal(0, value));
        Assert.All(allocation.AsSpan(0x20).ToArray(), value => Assert.Equal(Canary, value));
    }

    [Theory]
    [InlineData(0, BufferAddress)]
    [InlineData(0x2000_0001UL, 0)]
    public void PortGetStateRejectsNullRequiredArguments(ulong handle, ulong output)
    {
        var context = CreateContext(new FakeGuestMemory());
        context[CpuRegister.Rdi] = handle;
        context[CpuRegister.Rsi] = output;

        AssertError(
            AudioOut2Exports.AudioOut2PortGetState(context),
            context,
            OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
    }

    [Fact]
    public void PortGetStateReportsUnmappedOutput()
    {
        var context = CreateContext(new FakeGuestMemory());
        context[CpuRegister.Rdi] = 0x2000_0001;
        context[CpuRegister.Rsi] = BufferAddress;

        AssertError(
            AudioOut2Exports.AudioOut2PortGetState(context),
            context,
            OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
    }

    [Fact]
    public void GetSpeakerInfoWritesExactLayoutAndPreservesTail()
    {
        var memory = new FakeGuestMemory();
        var allocation = FilledBuffer(0x50);
        memory.AddRegion(BufferAddress, allocation);
        var context = CreateContext(memory);
        context[CpuRegister.Rdi] = BufferAddress;

        AssertSuccess(AudioOut2Exports.AudioOut2GetSpeakerInfo(context), context);

        Assert.Equal(1U, BinaryPrimitives.ReadUInt32LittleEndian(allocation));
        Assert.Equal(2U, BinaryPrimitives.ReadUInt32LittleEndian(allocation.AsSpan(4)));
        Assert.Equal(48_000U, BinaryPrimitives.ReadUInt32LittleEndian(allocation.AsSpan(8)));
        Assert.All(allocation.AsSpan(0x0C, 0x34).ToArray(), value => Assert.Equal(0, value));
        Assert.All(allocation.AsSpan(0x40).ToArray(), value => Assert.Equal(Canary, value));
    }

    [Fact]
    public void GetSpeakerInfoReportsNullAndUnmappedOutputs()
    {
        var context = CreateContext(new FakeGuestMemory());

        AssertError(
            AudioOut2Exports.AudioOut2GetSpeakerInfo(context),
            context,
            OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);

        context[CpuRegister.Rdi] = BufferAddress;
        AssertError(
            AudioOut2Exports.AudioOut2GetSpeakerInfo(context),
            context,
            OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(255)]
    [InlineData(1000)]
    public void UserCreateAcceptsKnownUserIdsAndWritesHandle(int userId)
    {
        var memory = new FakeGuestMemory();
        var output = new byte[8];
        memory.AddRegion(BufferAddress, output);
        var context = CreateContext(memory);
        context[CpuRegister.Rdi] = unchecked((ulong)userId);
        context[CpuRegister.Rsi] = BufferAddress;

        AssertSuccess(AudioOut2Exports.AudioOut2UserCreate(context), context);
        Assert.NotEqual(0UL, BinaryPrimitives.ReadUInt64LittleEndian(output));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(2)]
    [InlineData(254)]
    [InlineData(256)]
    public void UserCreateRejectsUnknownUserIds(int userId)
    {
        var context = CreateContext(new FakeGuestMemory());
        context[CpuRegister.Rdi] = unchecked((ulong)userId);
        context[CpuRegister.Rsi] = BufferAddress;

        AssertError(
            AudioOut2Exports.AudioOut2UserCreate(context),
            context,
            OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
    }

    [Fact]
    public void UserCreateReportsNullAndUnmappedOutputs()
    {
        var context = CreateContext(new FakeGuestMemory());
        context[CpuRegister.Rdi] = 1;

        AssertError(
            AudioOut2Exports.AudioOut2UserCreate(context),
            context,
            OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);

        context[CpuRegister.Rsi] = BufferAddress;
        AssertError(
            AudioOut2Exports.AudioOut2UserCreate(context),
            context,
            OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
    }

    [Theory]
    [InlineData("g2tViFIohHE", "sceAudioOut2Initialize")]
    [InlineData("t5YrizufpQc", "sceAudioOut2ContextResetParam")]
    [InlineData("pDmme7Bgm6E", "sceAudioOut2ContextQueryMemory")]
    [InlineData("0x6o1VVAYSY", "sceAudioOut2ContextCreate")]
    [InlineData("JK2wamZPzwM", "sceAudioOut2PortCreate")]
    [InlineData("gatEUKG+Ea4", "sceAudioOut2PortGetState")]
    [InlineData("DImz2Ft9E2g", "sceAudioOut2GetSpeakerInfo")]
    [InlineData("xywYcRB7nbQ", "sceAudioOut2UserCreate")]
    public void ExportMetadataIsExact(string nid, string exportName)
    {
        ExportMetadataAssert.Exact(
            nid,
            exportName,
            "libSceAudioOut2",
            Generation.Gen5);
    }

    private static CpuContext CreateContext(FakeGuestMemory memory)
        => new(memory, Generation.Gen5);

    private static void ConfigureContextCreate(
        CpuContext context,
        ulong memorySize,
        ulong outputAddress)
    {
        context[CpuRegister.Rdi] = ParameterAddress;
        context[CpuRegister.Rsi] = ContextMemoryAddress;
        context[CpuRegister.Rdx] = memorySize;
        context[CpuRegister.Rcx] = outputAddress;
    }

    private static void ConfigurePortCreate(
        CpuContext context,
        int type,
        ulong outputAddress)
    {
        context[CpuRegister.Rdi] = unchecked((ulong)type);
        context[CpuRegister.Rsi] = ParameterAddress;
        context[CpuRegister.Rdx] = outputAddress;
        context[CpuRegister.Rcx] = 1;
    }

    private static byte[] FilledBuffer(int length)
    {
        var buffer = new byte[length];
        Array.Fill(buffer, Canary);
        return buffer;
    }

    private static void AssertSuccess(int result, CpuContext context)
    {
        Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_OK, result);
        Assert.Equal(0UL, context[CpuRegister.Rax]);
    }

    private static void AssertError(
        int result,
        CpuContext context,
        OrbisGen2Result expected)
    {
        Assert.Equal((int)expected, result);
        Assert.Equal(unchecked((ulong)(int)expected), context[CpuRegister.Rax]);
    }
}
