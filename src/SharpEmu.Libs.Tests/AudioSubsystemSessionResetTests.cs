// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using SharpEmu.HLE;
using SharpEmu.Libs.Audio;
using SharpEmu.Libs.Ngs2;
using Xunit;

namespace SharpEmu.Libs.Tests;

[CollectionDefinition(AudioSubsystemSessionStateCollection.Name, DisableParallelization = true)]
public sealed class AudioSubsystemSessionStateCollection
{
    public const string Name = "Audio subsystem session state";
}

[Collection(AudioSubsystemSessionStateCollection.Name)]
public sealed class AudioSubsystemSessionResetTests
{
    private const ulong AudioOut2ParameterAddress = 0x1000;
    private const ulong AudioOut2HandleAddress = 0x2000;
    private const ulong AcmHandleAddress = 0x3000;
    private const ulong NgsContextAddress = 0x4000;
    private const ulong NgsHandleAddress = 0x5000;
    private const ulong NgsHandle = 0x1234_0000;
    private const ulong FmodSystem = 0x2000_0000;

    [Fact]
    public void ResetRuntimeStateInvalidatesRegistriesAndRestartsTheirState()
    {
        var memory = CreateMemory();
        var context = new CpuContext(memory, Generation.Gen5);

        AudioSubsystemLifecycle.ResetRuntimeState();
        try
        {
            var audioOut2 = CreateAudioOut2Context(context);
            var acm = CreateAcmContext(context);
            var ngs = CreateNgsSystem(context);
            Assert.Equal(2UL, audioOut2);
            Assert.Equal(1U, acm);
            Assert.Equal(NgsHandle, ngs);

            CallFmodSetOutput(context, 1);
            CallFmodSetOutput(context, 2);
            Assert.True(memory.TryWrite(FmodSystem + 0x08, [0xA5]));
            CallFmodSetOutput(context, 3);
            var initialized = new byte[1];
            Assert.True(memory.TryRead(FmodSystem + 0x08, initialized));
            Assert.Equal(0xA5, initialized[0]);

            AudioSubsystemLifecycle.ResetRuntimeState();

            context[CpuRegister.Rdi] = audioOut2;
            Assert.NotEqual(0, AudioOut2Exports.AudioOut2ContextDestroy(context));
            context[CpuRegister.Rdi] = acm;
            Assert.NotEqual(0, AcmExports.AcmContextDestroy(context));
            context[CpuRegister.Rdi] = ngs;
            Assert.NotEqual(0, Ngs2Exports.Ngs2SystemDestroy(context));

            CallFmodSetOutput(context, 4);
            Assert.True(memory.TryRead(FmodSystem + 0x08, initialized));
            Assert.Equal(0, initialized[0]);

            Assert.Equal(2UL, CreateAudioOut2Context(context));
            Assert.Equal(1U, CreateAcmContext(context));
            Assert.Equal(NgsHandle, CreateNgsSystem(context));
        }
        finally
        {
            AudioSubsystemLifecycle.ResetRuntimeState();
        }
    }

    private static FakeGuestMemory CreateMemory()
    {
        var memory = new FakeGuestMemory();
        var parameter = new byte[0x40];
        BinaryPrimitives.WriteUInt32LittleEndian(parameter.AsSpan(0x00), 256);
        BinaryPrimitives.WriteUInt32LittleEndian(parameter.AsSpan(0x04), 256);
        BinaryPrimitives.WriteUInt32LittleEndian(parameter.AsSpan(0x0C), 4);
        BinaryPrimitives.WriteUInt32LittleEndian(parameter.AsSpan(0x10), 512);
        BinaryPrimitives.WriteUInt32LittleEndian(parameter.AsSpan(0x14), 1);
        memory.AddRegion(AudioOut2ParameterAddress, parameter);
        memory.AddRegion(AudioOut2HandleAddress, new byte[sizeof(ulong)]);
        memory.AddRegion(AcmHandleAddress, new byte[sizeof(uint)]);
        memory.AddRegion(NgsContextAddress, BitConverter.GetBytes(NgsHandle));
        memory.AddRegion(NgsHandleAddress, new byte[sizeof(ulong)]);
        memory.AddRegion(FmodSystem + 0x08, new byte[1]);
        memory.AddRegion(FmodSystem + 0x116D0, new byte[8]);
        return memory;
    }

    private static ulong CreateAudioOut2Context(CpuContext context)
    {
        context[CpuRegister.Rdi] = AudioOut2ParameterAddress;
        context[CpuRegister.Rsi] = 0x1000_0000;
        context[CpuRegister.Rdx] = 0x10000 + (4 * 0x590UL);
        context[CpuRegister.Rcx] = AudioOut2HandleAddress;
        Assert.Equal(0, AudioOut2Exports.AudioOut2ContextCreate(context));
        Assert.True(context.TryReadUInt64(AudioOut2HandleAddress, out var handle));
        return handle;
    }

    private static uint CreateAcmContext(CpuContext context)
    {
        context[CpuRegister.Rdi] = AcmHandleAddress;
        Assert.Equal(0, AcmExports.AcmContextCreate(context));
        Assert.True(context.TryReadUInt32(AcmHandleAddress, out var handle));
        return handle;
    }

    private static ulong CreateNgsSystem(CpuContext context)
    {
        context[CpuRegister.Rsi] = NgsContextAddress;
        context[CpuRegister.Rdx] = NgsHandleAddress;
        Assert.Equal(0, Ngs2Exports.Ngs2SystemCreate(context));
        Assert.True(context.TryReadUInt64(NgsHandleAddress, out var handle));
        return handle;
    }

    private static void CallFmodSetOutput(CpuContext context, int output)
    {
        context[CpuRegister.Rdi] = FmodSystem;
        context[CpuRegister.Rsi] = unchecked((ulong)output);
        Assert.Equal(0, FmodCompatExports.FmodSystemSetOutput(context));
    }
}
