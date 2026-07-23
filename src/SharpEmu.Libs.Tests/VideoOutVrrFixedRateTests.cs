// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using SharpEmu.Libs.VideoOut;
using Xunit;

namespace SharpEmu.Libs.Tests;

public sealed class VideoOutVrrFixedRateTests
{
    private const int OrbisVideoOutErrorInvalidHandle = unchecked((int)0x8029000B);

    [Fact]
    public void PegToFixedRateTracksValidPort()
    {
        var (context, handle) = OpenPort();

        try
        {
            Assert.False(VideoOutExports.IsVrrPeggedToFixedRate(handle));

            context[CpuRegister.Rdi] = unchecked((ulong)handle);
            context[CpuRegister.Rsi] = 0;
            context[CpuRegister.Rdx] = 0;

            Assert.Equal(
                (int)OrbisGen2Result.ORBIS_GEN2_OK,
                VideoOutExports.VideoOutVrrPegToFixedRate(context));
            Assert.True(VideoOutExports.IsVrrPeggedToFixedRate(handle));
        }
        finally
        {
            ClosePort(context, handle);
        }
    }

    [Fact]
    public void UnpegFromFixedRateClearsTrackedPort()
    {
        var (context, handle) = OpenPort();

        try
        {
            context[CpuRegister.Rdi] = unchecked((ulong)handle);
            Assert.Equal(
                (int)OrbisGen2Result.ORBIS_GEN2_OK,
                VideoOutExports.VideoOutVrrPegToFixedRate(context));

            Assert.Equal(
                (int)OrbisGen2Result.ORBIS_GEN2_OK,
                VideoOutExports.VideoOutVrrUnpegFromFixedRate(context));
            Assert.False(VideoOutExports.IsVrrPeggedToFixedRate(handle));
        }
        finally
        {
            ClosePort(context, handle);
        }
    }

    [Fact]
    public void FixedRateTransitionsAreIdempotent()
    {
        var (context, handle) = OpenPort();

        try
        {
            context[CpuRegister.Rdi] = unchecked((ulong)handle);

            Assert.Equal(0, VideoOutExports.VideoOutVrrPegToFixedRate(context));
            Assert.Equal(0, VideoOutExports.VideoOutVrrPegToFixedRate(context));
            Assert.True(VideoOutExports.IsVrrPeggedToFixedRate(handle));

            Assert.Equal(0, VideoOutExports.VideoOutVrrUnpegFromFixedRate(context));
            Assert.Equal(0, VideoOutExports.VideoOutVrrUnpegFromFixedRate(context));
            Assert.False(VideoOutExports.IsVrrPeggedToFixedRate(handle));
        }
        finally
        {
            ClosePort(context, handle);
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(int.MaxValue)]
    [InlineData(-1)]
    public void FixedRateTransitionsRejectUnknownPort(int handle)
    {
        var context = new CpuContext(new FakeGuestMemory(), Generation.Gen5);
        context[CpuRegister.Rdi] = unchecked((ulong)handle);

        Assert.Equal(
            OrbisVideoOutErrorInvalidHandle,
            VideoOutExports.VideoOutVrrPegToFixedRate(context));
        Assert.Equal(
            OrbisVideoOutErrorInvalidHandle,
            VideoOutExports.VideoOutVrrUnpegFromFixedRate(context));
    }

    [Fact]
    public void PegToFixedRateExportMetadataIsExact()
    {
        ExportMetadataAssert.Exact(
            "5tRaBjtdTzY",
            "sceVideoOutVrrPegToFixedRate",
            "libSceVideoOut",
            Generation.Gen5);
    }

    [Fact]
    public void UnpegFromFixedRateExportMetadataIsExact()
    {
        ExportMetadataAssert.Exact(
            "T4ucGB8CsnM",
            "sceVideoOutVrrUnpegFromFixedRate",
            "libSceVideoOut",
            Generation.Gen5);
    }

    private static (CpuContext Context, int Handle) OpenPort()
    {
        var context = new CpuContext(new FakeGuestMemory(), Generation.Gen5);
        context[CpuRegister.Rdi] = 0;
        context[CpuRegister.Rsi] = 0;
        context[CpuRegister.Rdx] = 0;
        var handle = VideoOutExports.VideoOutOpen(context);
        Assert.True(handle > 0);
        return (context, handle);
    }

    private static void ClosePort(CpuContext context, int handle)
    {
        context[CpuRegister.Rdi] = unchecked((ulong)handle);
        Assert.Equal(0, VideoOutExports.VideoOutClose(context));
    }
}
