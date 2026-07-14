// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using SharpEmu.Libs.Audio;
using Xunit;

namespace SharpEmu.Libs.Tests;

public sealed class AudioOutCompatibilityTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    public void OpenRejectsBufferLayoutsLargerThanManagedBuffers(int format)
    {
        var context = CreateOpenContext(uint.MaxValue, format);
        var result = AudioOutExports.AudioOutOpen(context);

        try
        {
            Assert.Equal(
                (int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT,
                result);
            Assert.Equal(
                unchecked((ulong)(int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT),
                context[CpuRegister.Rax]);
        }
        finally
        {
            if (result > 0)
            {
                context[CpuRegister.Rdi] = unchecked((ulong)result);
                _ = AudioOutExports.AudioOutClose(context);
            }
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(255)]
    [InlineData(257)]
    [InlineData(2_049)]
    [InlineData(uint.MaxValue)]
    public void OpenRejectsUnsupportedBufferLengths(uint bufferLength)
    {
        AssertOpenReturnsInvalidArgument(CreateOpenContext(bufferLength, format: 1));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(44_100)]
    [InlineData(96_000)]
    [InlineData(uint.MaxValue)]
    public void OpenRejectsUnsupportedSampleFrequencies(uint frequency)
    {
        var context = CreateOpenContext(bufferLength: 256, format: 1);
        context[CpuRegister.R8] = frequency;

        AssertOpenReturnsInvalidArgument(context);
    }

    [Theory]
    [InlineData(0, 256, 1, 2, false, 512)]
    [InlineData(1, 512, 2, 2, false, 2_048)]
    [InlineData(2, 1_024, 8, 2, false, 16_384)]
    [InlineData(3, 2_048, 1, 4, true, 8_192)]
    [InlineData(4, 256, 2, 4, true, 2_048)]
    [InlineData(5, 512, 8, 4, true, 16_384)]
    [InlineData(6, 1_024, 8, 2, false, 16_384)]
    [InlineData(7, 2_048, 8, 4, true, 65_536)]
    public void BufferLayoutDescribesEverySupportedFormat(
        int format,
        uint bufferLength,
        int expectedChannels,
        int expectedBytesPerSample,
        bool expectedIsFloat,
        int expectedBufferByteLength)
    {
        var success = AudioOutExports.TryGetBufferLayout(
            format,
            bufferLength,
            out var channels,
            out var bytesPerSample,
            out var isFloat,
            out var bufferByteLength);

        Assert.True(success);
        Assert.Equal(expectedChannels, channels);
        Assert.Equal(expectedBytesPerSample, bytesPerSample);
        Assert.Equal(expectedIsFloat, isFloat);
        Assert.Equal(expectedBufferByteLength, bufferByteLength);
    }

    [Theory]
    [InlineData(8)]
    [InlineData(255)]
    [InlineData(int.MaxValue)]
    public void BufferLayoutRejectsUnsupportedFormats(int format)
    {
        Assert.False(AudioOutExports.TryGetBufferLayout(
            format,
            bufferLength: 256,
            out _,
            out _,
            out _,
            out _));
    }

    private static CpuContext CreateOpenContext(uint bufferLength, int format)
    {
        var context = new CpuContext(new FakeGuestMemory(), Generation.Gen5);
        context[CpuRegister.Rdi] = 1;
        context[CpuRegister.Rsi] = 0;
        context[CpuRegister.Rcx] = bufferLength;
        context[CpuRegister.R8] = 48_000;
        context[CpuRegister.R9] = unchecked((ulong)format);
        return context;
    }

    private static void AssertOpenReturnsInvalidArgument(CpuContext context)
    {
        var result = AudioOutExports.AudioOutOpen(context);

        try
        {
            Assert.Equal(
                (int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT,
                result);
            Assert.Equal(
                unchecked((ulong)(int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT),
                context[CpuRegister.Rax]);
        }
        finally
        {
            if (result > 0)
            {
                context[CpuRegister.Rdi] = unchecked((ulong)result);
                _ = AudioOutExports.AudioOutClose(context);
            }
        }
    }
}
