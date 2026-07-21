// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using SharpEmu.Libs.Ajm;
using SharpEmu.Libs.Codec;
using Xunit;

namespace SharpEmu.Libs.Tests;

[CollectionDefinition(MediaCodecSessionStateCollection.Name, DisableParallelization = true)]
public sealed class MediaCodecSessionStateCollection
{
    public const string Name = "Media codec session state";
}

[Collection(MediaCodecSessionStateCollection.Name)]
public sealed class MediaCodecSessionResetTests
{
    private const ulong AjmContextAddress = 0x1000;
    private const ulong VideoHandleAddress = 0x2000;

    [Fact]
    public void ResetRuntimeStateInvalidatesRegistriesAndRestartsIds()
    {
        var memory = new FakeGuestMemory();
        memory.AddRegion(AjmContextAddress, new byte[sizeof(uint)]);
        memory.AddRegion(VideoHandleAddress, new byte[sizeof(ulong)]);
        var context = new CpuContext(memory, Generation.Gen5);

        MediaCodecLifecycle.ResetRuntimeState();
        try
        {
            var ajmContext = CreateAjmContext(context);
            var videoDecoder = CreateVideoDecoder(context);
            var audioDecoder = CreateAudioDecoder(context);
            Assert.Equal(1U, ajmContext);
            Assert.Equal(2UL, videoDecoder);
            Assert.Equal(3UL, audioDecoder);

            MediaCodecLifecycle.ResetRuntimeState();

            context[CpuRegister.Rdi] = ajmContext;
            context[CpuRegister.Rsi] = 1;
            context[CpuRegister.Rdx] = 0;
            Assert.NotEqual(0, AjmExports.AjmModuleRegister(context));
            context[CpuRegister.Rdi] = videoDecoder;
            Assert.NotEqual(0, CodecExports.VideodecDecode(context));
            context[CpuRegister.Rdi] = audioDecoder;
            Assert.NotEqual(0, CodecExports.AudiodecDecode(context));

            Assert.Equal(1U, CreateAjmContext(context));
            Assert.Equal(2UL, CreateVideoDecoder(context));
            Assert.Equal(3UL, CreateAudioDecoder(context));
        }
        finally
        {
            MediaCodecLifecycle.ResetRuntimeState();
        }
    }

    private static uint CreateAjmContext(CpuContext context)
    {
        context[CpuRegister.Rdi] = 0;
        context[CpuRegister.Rsi] = AjmContextAddress;
        Assert.Equal(0, AjmExports.AjmInitialize(context));
        Assert.True(context.TryReadUInt32(AjmContextAddress, out var contextId));
        return contextId;
    }

    private static ulong CreateVideoDecoder(CpuContext context)
    {
        context[CpuRegister.Rdx] = VideoHandleAddress;
        Assert.Equal(0, CodecExports.VideodecCreateDecoder(context));
        Assert.True(context.TryReadUInt64(VideoHandleAddress, out var handle));
        return handle;
    }

    private static ulong CreateAudioDecoder(CpuContext context)
    {
        Assert.True(CodecExports.AudiodecCreateDecoder(context) >= 0);
        return context[CpuRegister.Rax];
    }
}
