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
        Assert.True(request.Matches(
            expectedAddress,
            1,
            1,
            pixelShaderAddress: 0));
        Assert.True(request.ShouldCapture(
            expectedAddress,
            1,
            1,
            pixelShaderAddress: 0,
            expectedWrite));
        Assert.False(request.ShouldCapture(
            expectedAddress,
            1,
            1,
            pixelShaderAddress: 0,
            expectedWrite + 1));
        Assert.Equal(
            $"0x{expectedAddress:X}@{expectedWrite}",
            request.ToString());
    }

    [Theory]
    [InlineData("1280x720@120", 120)]
    [InlineData(" 1280 X 720 @ 1 ", 1)]
    public void DimensionValue_MatchesStructurally(
        string value,
        int expectedWrite)
    {
        Assert.True(
            GuestImageWriteCaptureRequest.TryParse(
                value,
                out var request));
        Assert.Equal(0UL, request.Address);
        Assert.Equal(1280U, request.Width);
        Assert.Equal(720U, request.Height);
        Assert.True(request.Matches(
            0xABCDEF,
            1280,
            720,
            pixelShaderAddress: 0));
        Assert.False(request.Matches(
            0xABCDEF,
            1920,
            1080,
            pixelShaderAddress: 0));
        Assert.Equal(
            $"1280x720@{expectedWrite}",
            request.ToString());
    }

    [Theory]
    [InlineData(
        "1280x720,ps=0x55D4300@2",
        0xABCDEFUL,
        1280U,
        720U,
        0x55D4300UL)]
    [InlineData(
        " C490000 , PS = 5662100 @ 1 ",
        0xC490000UL,
        1U,
        1U,
        0x5662100UL)]
    public void PixelShaderValue_QualifiesTargetMatch(
        string value,
        ulong address,
        uint width,
        uint height,
        ulong pixelShaderAddress)
    {
        Assert.True(
            GuestImageWriteCaptureRequest.TryParse(
                value,
                out var request));
        Assert.Equal(pixelShaderAddress, request.PixelShaderAddress);
        Assert.True(request.Matches(
            address,
            width,
            height,
            pixelShaderAddress));
        Assert.False(request.Matches(
            address,
            width,
            height,
            pixelShaderAddress + 1));
        Assert.True(request.ShouldCapture(
            address,
            width,
            height,
            pixelShaderAddress,
            request.Write));
        Assert.Contains(
            $",ps=0x{pixelShaderAddress:X}@",
            request.ToString(),
            StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("1920x1080,sig=8988b48b@20", "8988B48B")]
    [InlineData(" C490000 , SIG = C30D590E5FB93951 @ 4 ", "C30D590E5FB93951")]
    public void PixelShaderSignature_QualifiesTargetWithoutAslrAddress(
        string value,
        string expectedSignature)
    {
        Assert.True(
            GuestImageWriteCaptureRequest.TryParse(value, out var request));
        Assert.Equal(expectedSignature, request.PixelShaderSignature);
        Assert.True(request.Matches(
            request.Address == 0 ? 0xABCDEFUL : request.Address,
            request.Width == 0 ? 1U : request.Width,
            request.Height == 0 ? 1U : request.Height,
            pixelShaderAddress: 0xDEADBEEF,
            pixelShaderSignature: expectedSignature.ToLowerInvariant()));
        Assert.False(request.Matches(
            request.Address == 0 ? 0xABCDEFUL : request.Address,
            request.Width == 0 ? 1U : request.Width,
            request.Height == 0 ? 1U : request.Height,
            pixelShaderAddress: 0xDEADBEEF,
            pixelShaderSignature: "00000000"));
        Assert.Contains(
            $",sig={expectedSignature}@",
            request.ToString(),
            StringComparison.Ordinal);
    }

    [Fact]
    public void FullPixelShaderSignature_MatchesPresenterSignaturePrefix()
    {
        const string fullSignature =
            "8988B48BAA3E2A6273D0F0D79B0934E280BAAA5F246158EE8F6679B9BD7959C8";
        Assert.True(
            GuestImageWriteCaptureRequest.TryParse(
                $"1920x1080,sig={fullSignature}@1",
                out var request));

        Assert.True(request.Matches(
            address: 0xABCDEF,
            width: 1920,
            height: 1080,
            pixelShaderAddress: 0xDEADBEEF,
            pixelShaderSignature: fullSignature[..16]));
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
    [InlineData("1280x720,ps=@1")]
    [InlineData("1280x720,ps=0@1")]
    [InlineData("1280x720,ps=not-hex@1")]
    [InlineData("1280x720,es=55D4300@1")]
    [InlineData("1280x720,sig=@1")]
    [InlineData("1280x720,sig=not-hex@1")]
    [InlineData("1280x720,sig=1234567@1")]
    [InlineData("1280x720,ps=55D4300,ps=5662100@1")]
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
