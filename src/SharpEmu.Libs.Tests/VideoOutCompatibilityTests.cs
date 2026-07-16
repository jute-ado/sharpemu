// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using SharpEmu.Libs.VideoOut;
using Xunit;

namespace SharpEmu.Libs.Tests;

public sealed class VideoOutCompatibilityTests
{
    private const int OrbisVideoOutErrorInvalidHandle = unchecked((int)0x8029000B);

    [Fact]
    public void ConfigureOutputAcceptsAdvertisedModeForOpenPort()
    {
        var context = new CpuContext(new FakeGuestMemory(), Generation.Gen5);
        context[CpuRegister.Rdi] = 0;
        context[CpuRegister.Rsi] = 0;
        context[CpuRegister.Rdx] = 0;
        var handle = VideoOutExports.VideoOutOpen(context);
        Assert.True(handle > 0);

        try
        {
            context[CpuRegister.Rdi] = unchecked((ulong)handle);
            context[CpuRegister.Rsi] = 1;

            Assert.Equal(
                (int)OrbisGen2Result.ORBIS_GEN2_OK,
                VideoOutExports.VideoOutConfigureOutput(context));
        }
        finally
        {
            context[CpuRegister.Rdi] = unchecked((ulong)handle);
            _ = VideoOutExports.VideoOutClose(context);
        }
    }

    [Fact]
    public void ConfigureOutputRejectsUnknownPort()
    {
        var context = new CpuContext(new FakeGuestMemory(), Generation.Gen5);
        context[CpuRegister.Rdi] = int.MaxValue;
        context[CpuRegister.Rsi] = 1;

        Assert.Equal(
            OrbisVideoOutErrorInvalidHandle,
            VideoOutExports.VideoOutConfigureOutput(context));
    }

    [Fact]
    public void ConfigureOutputExportMetadataIsExact()
    {
        ExportMetadataAssert.Exact(
            "w0hLuNarQxY",
            "sceVideoOutConfigureOutput",
            "libSceVideoOut",
            Generation.Gen4 | Generation.Gen5);
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(1, 0)]
    public void OutputSupportIsLimitedToMainBus(int busType, int expected)
    {
        var context = new CpuContext(new FakeGuestMemory(), Generation.Gen5);
        context[CpuRegister.Rdi] = unchecked((ulong)busType);
        context[CpuRegister.Rsi] = uint.MaxValue;
        context[CpuRegister.Rdx] = uint.MaxValue;

        Assert.Equal(expected, VideoOutExports.VideoOutIsOutputSupported(context));
    }

    [Fact]
    public void OutputSupportExportMetadataIsExact()
    {
        ExportMetadataAssert.Exact(
            "Nv8c-Kb+DUM",
            "sceVideoOutIsOutputSupported",
            "libSceVideoOut",
            Generation.Gen4 | Generation.Gen5);
    }
}
