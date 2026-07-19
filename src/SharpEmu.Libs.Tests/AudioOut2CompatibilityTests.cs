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
    private const ulong ContextHandleAddress = 0x20000;
    private const ulong ContextParameterAddress = 0x21000;
    private const ulong PortHandleAddress = 0x22000;
    private const ulong AttributeAddress = 0x23000;
    private const ulong AttributeValueAddress = 0x24000;
    private const ulong ContextMemorySize = 0x11640;
    private const int AudioOut2ErrorNotReady = unchecked((int)0x80268008);
    private const byte Canary = 0xA5;

    [Fact]
    public void ContextResetParamWritesExactStructureWithoutTouchingCanaries()
    {
        var memory = new FakeGuestMemory();
        var allocation = FilledBuffer(0x60);
        memory.AddRegion(BufferAddress, allocation);
        var context = CreateContext(memory);
        context[CpuRegister.Rdi] = BufferAddress + 8;

        AssertSuccess(AudioOut2Exports.AudioOut2ContextResetParam(context), context);

        Assert.All(allocation.AsSpan(0, 8).ToArray(), value => Assert.Equal(Canary, value));
        var parameter = allocation.AsSpan(8, 0x40);
        Assert.Equal(256U, BinaryPrimitives.ReadUInt32LittleEndian(parameter[0x00..]));
        Assert.Equal(256U, BinaryPrimitives.ReadUInt32LittleEndian(parameter[0x04..]));
        Assert.Equal(0U, BinaryPrimitives.ReadUInt32LittleEndian(parameter[0x08..]));
        Assert.Equal(4U, BinaryPrimitives.ReadUInt32LittleEndian(parameter[0x0C..]));
        Assert.Equal(512U, BinaryPrimitives.ReadUInt32LittleEndian(parameter[0x10..]));
        Assert.Equal(1U, BinaryPrimitives.ReadUInt32LittleEndian(parameter[0x14..]));
        Assert.All(parameter[0x18..].ToArray(), value => Assert.Equal(0, value));
        Assert.All(allocation.AsSpan(0x48).ToArray(), value => Assert.Equal(Canary, value));
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
        var parameter = CreateContextParameter(queueDepth: 6);
        memory.AddRegion(BufferAddress, allocation);
        memory.AddRegion(ParameterAddress, parameter);
        var context = CreateContext(memory);
        context[CpuRegister.Rdi] = ParameterAddress;
        context[CpuRegister.Rsi] = BufferAddress + 8;

        AssertSuccess(AudioOut2Exports.AudioOut2ContextQueryMemory(context), context);

        Assert.All(allocation.AsSpan(0, 8).ToArray(), value => Assert.Equal(Canary, value));
        Assert.Equal(
            0x1_2160UL,
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
        memory.AddRegion(ParameterAddress, CreateContextParameter());
        var context = CreateContext(memory);
        ConfigureContextCreate(context, ContextMemorySize, BufferAddress + 8);

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
        memory.AddRegion(ParameterAddress, CreateContextParameter());
        var context = CreateContext(memory);
        ConfigureContextCreate(context, ContextMemorySize - 1, BufferAddress);

        AssertError(
            AudioOut2Exports.AudioOut2ContextCreate(context),
            context,
            OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
    }

    [Theory]
    [InlineData(0, ContextMemoryAddress, ContextMemorySize, BufferAddress)]
    [InlineData(ParameterAddress, 0, ContextMemorySize, BufferAddress)]
    [InlineData(ParameterAddress, ContextMemoryAddress, 0, BufferAddress)]
    [InlineData(ParameterAddress, ContextMemoryAddress, ContextMemorySize, 0)]
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
        var memory = new FakeGuestMemory();
        memory.AddRegion(ParameterAddress, CreateContextParameter());
        var context = CreateContext(memory);
        ConfigureContextCreate(context, ContextMemorySize, BufferAddress);

        AssertError(
            AudioOut2Exports.AudioOut2ContextCreate(context),
            context,
            OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
    }

    [Fact]
    public void PortCreateUsesContextFirstAbiAndPreservesOutputCanaries()
    {
        var memory = new FakeGuestMemory();
        var allocation = FilledBuffer(24);
        memory.AddRegion(BufferAddress, allocation);
        memory.AddRegion(ParameterAddress, CreatePortParameter(portType: 2));
        var contextHandle = CreateAudioContext(memory);
        var context = CreateContext(memory);
        ConfigurePortCreate(context, contextHandle, BufferAddress + 8);

        AssertSuccess(AudioOut2Exports.AudioOut2PortCreate(context), context);

        var handle = BinaryPrimitives.ReadUInt64LittleEndian(allocation.AsSpan(8, 8));
        Assert.NotEqual(0UL, handle);
        Assert.All(allocation.AsSpan(0, 8).ToArray(), value => Assert.Equal(Canary, value));
        Assert.All(allocation.AsSpan(16).ToArray(), value => Assert.Equal(Canary, value));
    }

    [Fact]
    public void PortCreateAcceptsObjectPortType()
    {
        var memory = new FakeGuestMemory();
        memory.AddRegion(ParameterAddress, CreatePortParameter(portType: 0x0100));
        memory.AddRegion(BufferAddress, new byte[8]);
        var contextHandle = CreateAudioContext(memory);
        var context = CreateContext(memory);
        ConfigurePortCreate(context, contextHandle, BufferAddress);

        AssertSuccess(AudioOut2Exports.AudioOut2PortCreate(context), context);
    }

    [Theory]
    [InlineData(0, ParameterAddress, BufferAddress)]
    [InlineData(1, 0, BufferAddress)]
    [InlineData(1, ParameterAddress, 0)]
    public void PortCreateRejectsNullRequiredArguments(
        ulong contextHandle,
        ulong parameter,
        ulong output)
    {
        var context = CreateContext(new FakeGuestMemory());
        context[CpuRegister.Rdi] = contextHandle;
        context[CpuRegister.Rsi] = parameter;
        context[CpuRegister.Rdx] = output;

        AssertError(
            AudioOut2Exports.AudioOut2PortCreate(context),
            context,
            OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
    }

    [Fact]
    public void PortCreateReportsUnmappedOutput()
    {
        var memory = new FakeGuestMemory();
        memory.AddRegion(ParameterAddress, CreatePortParameter(portType: 0));
        var contextHandle = CreateAudioContext(memory);
        var context = CreateContext(memory);
        ConfigurePortCreate(context, contextHandle, BufferAddress);

        AssertError(
            AudioOut2Exports.AudioOut2PortCreate(context),
            context,
            OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
    }

    [Theory]
    [InlineData(0, 0x0001U, 1, 2)]
    [InlineData(2, 0x0101U, 0x40, 1)]
    [InlineData(0, 0x0801U, 1, 8)]
    public void PortGetStateWritesExactLayoutAndPreservesTail(
        ushort portType,
        uint dataFormat,
        ushort expectedOutput,
        byte expectedChannels)
    {
        var memory = new FakeGuestMemory();
        var allocation = FilledBuffer(0x50);
        memory.AddRegion(BufferAddress, allocation);
        var handle = CreateAudioPort(memory, portType, dataFormat);
        var context = CreateContext(memory);
        context[CpuRegister.Rdi] = handle;
        context[CpuRegister.Rsi] = BufferAddress;

        AssertSuccess(AudioOut2Exports.AudioOut2PortGetState(context), context);

        Assert.Equal(expectedOutput, BinaryPrimitives.ReadUInt16LittleEndian(allocation));
        Assert.Equal(expectedChannels, allocation[0x02]);
        Assert.Equal(0, allocation[0x03]);
        Assert.Equal(127, BinaryPrimitives.ReadInt16LittleEndian(allocation.AsSpan(0x04)));
        Assert.All(allocation.AsSpan(0x06, 0x3A).ToArray(), value => Assert.Equal(0, value));
        Assert.All(allocation.AsSpan(0x40).ToArray(), value => Assert.Equal(Canary, value));
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
        var memory = new FakeGuestMemory();
        var handle = CreateAudioPort(memory, portType: 0);
        var context = CreateContext(memory);
        context[CpuRegister.Rdi] = handle;
        context[CpuRegister.Rsi] = BufferAddress;

        AssertError(
            AudioOut2Exports.AudioOut2PortGetState(context),
            context,
            OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
    }

    [Fact]
    public void PortSetAttributesReadsPcmDescriptor()
    {
        var memory = new FakeGuestMemory();
        var handle = CreateAudioPort(memory, portType: 0);
        var attribute = new byte[0x18];
        BinaryPrimitives.WriteUInt64LittleEndian(
            attribute.AsSpan(0x08),
            AttributeValueAddress);
        BinaryPrimitives.WriteUInt64LittleEndian(attribute.AsSpan(0x10), 8);
        memory.AddRegion(AttributeAddress, attribute);
        var pcm = new byte[8];
        BinaryPrimitives.WriteUInt64LittleEndian(pcm, BufferAddress);
        memory.AddRegion(AttributeValueAddress, pcm);
        var context = CreateContext(memory);
        context[CpuRegister.Rdi] = handle;
        context[CpuRegister.Rsi] = AttributeAddress;
        context[CpuRegister.Rdx] = 1;

        AssertSuccess(AudioOut2Exports.AudioOut2PortSetAttributes(context), context);
    }

    [Fact]
    public void PortSetAttributesReportsInvalidAndUnmappedInputs()
    {
        var memory = new FakeGuestMemory();
        var handle = CreateAudioPort(memory, portType: 0);
        var context = CreateContext(memory);
        context[CpuRegister.Rdi] = handle;
        context[CpuRegister.Rdx] = 1;

        AssertError(
            AudioOut2Exports.AudioOut2PortSetAttributes(context),
            context,
            OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);

        context[CpuRegister.Rsi] = AttributeAddress;
        AssertError(
            AudioOut2Exports.AudioOut2PortSetAttributes(context),
            context,
            OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
    }

    [Fact]
    public void ContextAdvanceAcceptsCreatedContext()
    {
        var memory = new FakeGuestMemory();
        var handle = CreateAudioContext(memory, queueDepth: 6);
        var context = CreateContext(memory);
        context[CpuRegister.Rdi] = handle;

        AssertSuccess(AudioOut2Exports.AudioOut2ContextAdvance(context), context);
    }

    [Fact]
    public void ContextQueueLevelWritesConfiguredCapacityAndPreservesCanaries()
    {
        var memory = new FakeGuestMemory();
        var allocation = FilledBuffer(24);
        memory.AddRegion(BufferAddress, allocation);
        var handle = CreateAudioContext(memory, queueDepth: 6);
        var context = CreateContext(memory);
        context[CpuRegister.Rdi] = handle;
        context[CpuRegister.Rsi] = BufferAddress + 8;
        context[CpuRegister.Rdx] = BufferAddress + 12;

        AssertSuccess(AudioOut2Exports.AudioOut2ContextGetQueueLevel(context), context);

        Assert.Equal(0U, BinaryPrimitives.ReadUInt32LittleEndian(allocation.AsSpan(8)));
        Assert.Equal(6U, BinaryPrimitives.ReadUInt32LittleEndian(allocation.AsSpan(12)));
        Assert.All(allocation.AsSpan(0, 8).ToArray(), value => Assert.Equal(Canary, value));
        Assert.All(allocation.AsSpan(16).ToArray(), value => Assert.Equal(Canary, value));
    }

    [Fact]
    public void ContextQueueLevelAllowsEitherOptionalOutput()
    {
        var memory = new FakeGuestMemory();
        var output = new byte[4];
        memory.AddRegion(BufferAddress, output);
        var handle = CreateAudioContext(memory);
        var context = CreateContext(memory);
        context[CpuRegister.Rdi] = handle;
        context[CpuRegister.Rsi] = 0;
        context[CpuRegister.Rdx] = BufferAddress;

        AssertSuccess(AudioOut2Exports.AudioOut2ContextGetQueueLevel(context), context);
        Assert.Equal(4U, BinaryPrimitives.ReadUInt32LittleEndian(output));

        context[CpuRegister.Rsi] = BufferAddress;
        context[CpuRegister.Rdx] = 0;

        AssertSuccess(AudioOut2Exports.AudioOut2ContextGetQueueLevel(context), context);
        Assert.Equal(0U, BinaryPrimitives.ReadUInt32LittleEndian(output));
    }

    [Fact]
    public void ContextPushAndAdvanceShareOneQueueState()
    {
        var memory = new FakeGuestMemory();
        var queueLevel = new byte[8];
        memory.AddRegion(BufferAddress, queueLevel);
        var handle = CreateAudioContext(memory, queueDepth: 2);
        var context = CreateContext(memory);
        context[CpuRegister.Rdi] = handle;
        context[CpuRegister.Rsi] = 0;

        AssertSuccess(AudioOut2Exports.AudioOut2ContextPush(context), context);
        AssertSuccess(AudioOut2Exports.AudioOut2ContextPush(context), context);
        Assert.Equal(
            AudioOut2ErrorNotReady,
            AudioOut2Exports.AudioOut2ContextPush(context));
        Assert.Equal(unchecked((ulong)AudioOut2ErrorNotReady), context[CpuRegister.Rax]);

        context[CpuRegister.Rsi] = BufferAddress;
        context[CpuRegister.Rdx] = BufferAddress + 4;
        AssertSuccess(AudioOut2Exports.AudioOut2ContextGetQueueLevel(context), context);
        Assert.Equal(2U, BinaryPrimitives.ReadUInt32LittleEndian(queueLevel));
        Assert.Equal(0U, BinaryPrimitives.ReadUInt32LittleEndian(queueLevel.AsSpan(4)));

        AssertSuccess(AudioOut2Exports.AudioOut2ContextAdvance(context), context);
        AssertSuccess(AudioOut2Exports.AudioOut2ContextGetQueueLevel(context), context);
        Assert.Equal(1U, BinaryPrimitives.ReadUInt32LittleEndian(queueLevel));
        Assert.Equal(1U, BinaryPrimitives.ReadUInt32LittleEndian(queueLevel.AsSpan(4)));
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

    [Fact]
    public void MasteringInitAcceptsCurrentFlags()
    {
        var context = CreateContext(new FakeGuestMemory());
        context[CpuRegister.Rdi] = 1;

        AssertSuccess(AudioOut2Exports.AudioOut2MasteringInit(context), context);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void MasteringSetParamAcceptsValidOutputs(uint output)
    {
        var memory = new FakeGuestMemory();
        var parameter = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(parameter, 7);
        memory.AddRegion(ParameterAddress, parameter);
        var context = CreateContext(memory);
        context[CpuRegister.Rdi] = ParameterAddress;
        context[CpuRegister.Rsi] = output;
        context[CpuRegister.Rdx] = 0x10;

        AssertSuccess(AudioOut2Exports.AudioOut2MasteringSetParam(context), context);
    }

    [Fact]
    public void MasteringSetParamReportsInvalidAndUnmappedInputs()
    {
        var context = CreateContext(new FakeGuestMemory());

        AssertError(
            AudioOut2Exports.AudioOut2MasteringSetParam(context),
            context,
            OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);

        context[CpuRegister.Rdi] = ParameterAddress;
        AssertError(
            AudioOut2Exports.AudioOut2MasteringSetParam(context),
            context,
            OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);

        var memory = new FakeGuestMemory();
        memory.AddRegion(ParameterAddress, new byte[4]);
        context = CreateContext(memory);
        context[CpuRegister.Rdi] = ParameterAddress;
        context[CpuRegister.Rsi] = 3;
        AssertError(
            AudioOut2Exports.AudioOut2MasteringSetParam(context),
            context,
            OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
    }

    [Theory]
    [InlineData("g2tViFIohHE", "sceAudioOut2Initialize")]
    [InlineData("t5YrizufpQc", "sceAudioOut2ContextResetParam")]
    [InlineData("pDmme7Bgm6E", "sceAudioOut2ContextQueryMemory")]
    [InlineData("0x6o1VVAYSY", "sceAudioOut2ContextCreate")]
    [InlineData("PE2zHMqLSHs", "sceAudioOut2ContextAdvance")]
    [InlineData("aII9h5nli9U", "sceAudioOut2ContextPush")]
    [InlineData("R7d0F1g2qsU", "sceAudioOut2ContextGetQueueLevel")]
    [InlineData("JK2wamZPzwM", "sceAudioOut2PortCreate")]
    [InlineData("8XTArSPyWHk", "sceAudioOut2PortSetAttributes")]
    [InlineData("gatEUKG+Ea4", "sceAudioOut2PortGetState")]
    [InlineData("DImz2Ft9E2g", "sceAudioOut2GetSpeakerInfo")]
    [InlineData("xywYcRB7nbQ", "sceAudioOut2UserCreate")]
    [InlineData("XHl38ZNknbs", "sceAudioOut2MasteringInit")]
    [InlineData("v8iOE+j8a5o", "sceAudioOut2MasteringSetParam")]
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
        ulong contextHandle,
        ulong outputAddress)
    {
        context[CpuRegister.Rdi] = contextHandle;
        context[CpuRegister.Rsi] = ParameterAddress;
        context[CpuRegister.Rdx] = outputAddress;
        context[CpuRegister.Rcx] = 0;
    }

    private static ulong CreateAudioContext(
        FakeGuestMemory memory,
        uint queueDepth = 4)
    {
        var output = new byte[8];
        memory.AddRegion(
            ContextParameterAddress,
            CreateContextParameter(queueDepth));
        memory.AddRegion(ContextHandleAddress, output);
        var context = CreateContext(memory);
        context[CpuRegister.Rdi] = ContextParameterAddress;
        context[CpuRegister.Rsi] = ContextMemoryAddress;
        context[CpuRegister.Rdx] = 0x10000UL + (queueDepth * 0x590UL);
        context[CpuRegister.Rcx] = ContextHandleAddress;

        AssertSuccess(AudioOut2Exports.AudioOut2ContextCreate(context), context);
        return BinaryPrimitives.ReadUInt64LittleEndian(output);
    }

    private static ulong CreateAudioPort(
        FakeGuestMemory memory,
        ushort portType,
        uint dataFormat = 1)
    {
        var contextHandle = CreateAudioContext(memory);
        var output = new byte[8];
        memory.AddRegion(
            ParameterAddress,
            CreatePortParameter(portType, dataFormat));
        memory.AddRegion(PortHandleAddress, output);
        var context = CreateContext(memory);
        ConfigurePortCreate(context, contextHandle, PortHandleAddress);

        AssertSuccess(AudioOut2Exports.AudioOut2PortCreate(context), context);
        return BinaryPrimitives.ReadUInt64LittleEndian(output);
    }

    private static byte[] CreateContextParameter(uint queueDepth = 4)
    {
        var parameter = new byte[0x40];
        BinaryPrimitives.WriteUInt32LittleEndian(parameter.AsSpan(0x00), 256);
        BinaryPrimitives.WriteUInt32LittleEndian(parameter.AsSpan(0x04), 256);
        BinaryPrimitives.WriteUInt32LittleEndian(parameter.AsSpan(0x0C), queueDepth);
        BinaryPrimitives.WriteUInt32LittleEndian(parameter.AsSpan(0x10), 512);
        BinaryPrimitives.WriteUInt32LittleEndian(parameter.AsSpan(0x14), 1);
        return parameter;
    }

    private static byte[] CreatePortParameter(
        ushort portType,
        uint dataFormat = 1)
    {
        var parameter = new byte[0x40];
        BinaryPrimitives.WriteUInt16LittleEndian(parameter, portType);
        BinaryPrimitives.WriteUInt32LittleEndian(
            parameter.AsSpan(0x04),
            dataFormat);
        BinaryPrimitives.WriteUInt32LittleEndian(parameter.AsSpan(0x08), 48_000);
        return parameter;
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
