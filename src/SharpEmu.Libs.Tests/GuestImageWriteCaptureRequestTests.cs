// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.VideoOut;
using Xunit;

namespace SharpEmu.Libs.Tests;

public sealed class GuestImageWriteCaptureRequestTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    public void EmptyValue_DisablesCapture(string? value)
    {
        Assert.True(
            GuestImageWriteCaptureRequest.TryParse(
                value,
                out var request));
        Assert.False(request.IsEnabled);
    }

    [Theory]
    [InlineData("0x0000000000C490000@120", 0xC490000UL, 120)]
    [InlineData(" C490000 @ 1 ", 0xC490000UL, 1)]
    [InlineData("abcdef@42", 0xABCDEFUL, 42)]
    public void ValidValue_ParsesAddressAndWrite(
        string value,
        ulong expectedAddress,
        int expectedWrite)
    {
        Assert.True(
            GuestImageWriteCaptureRequest.TryParse(
                value,
                out var request));
        Assert.Equal(expectedAddress, request.Address);
        Assert.Equal(expectedWrite, request.Write);
        Assert.True(request.IsEnabled);
        Assert.True(request.Matches(expectedAddress, 1, 1));
        Assert.True(request.ShouldCapture(
            expectedAddress,
            1,
            1,
            expectedWrite));
        Assert.False(request.ShouldCapture(
            expectedAddress,
            1,
            1,
            expectedWrite + 1));
    }

    [Theory]
    [InlineData("1280x720@120")]
    [InlineData(" 1280 X 720 @ 1 ")]
    public void DimensionValue_MatchesStructurally(
        string value)
    {
        Assert.True(
            GuestImageWriteCaptureRequest.TryParse(
                value,
                out var request));
        Assert.Equal(0UL, request.Address);
        Assert.Equal(1280U, request.Width);
        Assert.Equal(720U, request.Height);
        Assert.True(request.Matches(0xABCDEF, 1280, 720));
        Assert.False(request.Matches(0xABCDEF, 1920, 1080));
    }

    [Theory]
    [InlineData("C490000")]
    [InlineData("@1")]
    [InlineData("C490000@")]
    [InlineData("0@1")]
    [InlineData("C490000@0")]
    [InlineData("C490000@-1")]
    [InlineData("0x@1")]
    [InlineData("0x0@1")]
    [InlineData("1280x0@1")]
    [InlineData("not-hex@1")]
    [InlineData("C490000@not-a-number")]
    public void InvalidValue_IsRejected(string value)
    {
        Assert.False(
            GuestImageWriteCaptureRequest.TryParse(
                value,
                out var request));
        Assert.False(request.IsEnabled);
    }
}
