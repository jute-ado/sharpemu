// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using SharpEmu.Libs.VideoOut;
using Silk.NET.Vulkan;
using Xunit;

namespace SharpEmu.Libs.Tests.VideoOut;

public sealed class PresentedGuestImageRgbConversionTests
{
    [Fact]
    public void ConvertsA2R10G10B10UnormToEightBitRgb()
    {
        Span<byte> source = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(
            source,
            Pack(a: 3, first: 1023, green: 512, last: 0));

        var converted = VulkanVideoPresenter.TryConvertGuestImageToRgb(
            Format.A2R10G10B10UnormPack32,
            width: 1,
            height: 1,
            source,
            out var rgb);

        Assert.True(converted);
        Assert.Equal([255, 128, 0], rgb);
    }

    [Fact]
    public void ConvertsA2B10G10R10UnormToEightBitRgb()
    {
        Span<byte> source = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(
            source,
            Pack(a: 3, first: 1023, green: 512, last: 0));

        var converted = VulkanVideoPresenter.TryConvertGuestImageToRgb(
            Format.A2B10G10R10UnormPack32,
            width: 1,
            height: 1,
            source,
            out var rgb);

        Assert.True(converted);
        Assert.Equal([0, 128, 255], rgb);
    }

    [Fact]
    public void RejectsPackedInputWithTheWrongByteCount()
    {
        var converted = VulkanVideoPresenter.TryConvertGuestImageToRgb(
            Format.A2R10G10B10UnormPack32,
            width: 2,
            height: 1,
            [0, 0, 0, 0],
            out var rgb);

        Assert.False(converted);
        Assert.Empty(rgb);
    }

    [Fact]
    public void KeepsExistingEightBitRgbaChannelOrder()
    {
        var converted = VulkanVideoPresenter.TryConvertGuestImageToRgb(
            Format.R8G8B8A8Unorm,
            width: 2,
            height: 1,
            [1, 2, 3, 255, 254, 253, 252, 0],
            out var rgb);

        Assert.True(converted);
        Assert.Equal([1, 2, 3, 254, 253, 252], rgb);
    }

    [Fact]
    public void RejectsUnsupportedGuestImageFormat()
    {
        var converted = VulkanVideoPresenter.TryConvertGuestImageToRgb(
            Format.R16G16B16A16Sfloat,
            width: 1,
            height: 1,
            new byte[8],
            out var rgb);

        Assert.False(converted);
        Assert.Empty(rgb);
    }

    private static uint Pack(
        uint a,
        uint first,
        uint green,
        uint last) =>
        a << 30 | first << 20 | green << 10 | last;
}
