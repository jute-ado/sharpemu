// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.Agc;
using Xunit;

namespace SharpEmu.Libs.Tests;

public sealed class GuestDrawTraceRequestTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    public void EmptyValueDisablesTrace(string? value)
    {
        Assert.True(GuestDrawTraceRequest.TryParse(value, out var request));
        Assert.False(request.IsEnabled);
    }

    [Theory]
    [InlineData("0x5662100@4", 0x5662100UL, 4)]
    [InlineData(" 5561D00 @ 1 ", 0x5561D00UL, 1)]
    [InlineData("abcdef@64", 0xABCDEFUL, 64)]
    public void ValidValueSelectsEitherShader(
        string value,
        ulong shaderAddress,
        int drawLimit)
    {
        Assert.True(GuestDrawTraceRequest.TryParse(value, out var request));
        Assert.Equal(shaderAddress, request.ShaderAddress);
        Assert.Equal(drawLimit, request.DrawLimit);
        Assert.Equal(
            $"0x{shaderAddress:X}@{drawLimit}",
            request.ToString());
        Assert.True(request.Matches(shaderAddress, 0));
        Assert.True(request.Matches(0, shaderAddress));
        Assert.True(request.ShouldTrace(0, shaderAddress, drawLimit));
        Assert.False(request.ShouldTrace(0, shaderAddress, drawLimit + 1));
    }

    [Theory]
    [InlineData("5662100")]
    [InlineData("@1")]
    [InlineData("0@1")]
    [InlineData("0x0@1")]
    [InlineData("5662100@0")]
    [InlineData("5662100@65")]
    [InlineData("5662100@-1")]
    [InlineData("not-hex@1")]
    [InlineData("5662100@not-a-number")]
    public void InvalidValueIsRejected(string value)
    {
        Assert.False(GuestDrawTraceRequest.TryParse(value, out var request));
        Assert.False(request.IsEnabled);
    }
}
