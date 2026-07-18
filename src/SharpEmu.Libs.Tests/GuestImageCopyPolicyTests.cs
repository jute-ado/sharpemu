// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.Gpu;
using Xunit;

namespace SharpEmu.Libs.Tests;

public sealed class GuestImageCopyPolicyTests
{
    [Fact]
    public void AcceptsDistinctImagesWithIdenticalStorage()
    {
        Assert.True(
            GuestImageCopyPolicy.CanCopy(
                sourceAddress: 0x1000,
                sourceWidth: 1920,
                sourceHeight: 1080,
                sourceByteCount: 1920 * 1080 * 4,
                sourceFormat: 44,
                destinationAddress: 0x2000,
                destinationWidth: 1920,
                destinationHeight: 1080,
                destinationByteCount: 1920 * 1080 * 4,
                destinationFormat: 44));
    }

    [Theory]
    [InlineData(0UL, 0x2000UL, 1920U, 1080U, 8294400UL, 44UL)]
    [InlineData(0x1000UL, 0UL, 1920U, 1080U, 8294400UL, 44UL)]
    [InlineData(0x1000UL, 0x1000UL, 1920U, 1080U, 8294400UL, 44UL)]
    [InlineData(0x1000UL, 0x2000UL, 0U, 1080U, 8294400UL, 44UL)]
    [InlineData(0x1000UL, 0x2000UL, 1920U, 0U, 8294400UL, 44UL)]
    [InlineData(0x1000UL, 0x2000UL, 1280U, 1080U, 8294400UL, 44UL)]
    [InlineData(0x1000UL, 0x2000UL, 1920U, 720U, 8294400UL, 44UL)]
    [InlineData(0x1000UL, 0x2000UL, 1920U, 1080U, 0UL, 44UL)]
    [InlineData(0x1000UL, 0x2000UL, 1920U, 1080U, 4147200UL, 44UL)]
    [InlineData(0x1000UL, 0x2000UL, 1920U, 1080U, 8294400UL, 37UL)]
    public void RejectsCopiesThatCannotPreserveImageStorage(
        ulong sourceAddress,
        ulong destinationAddress,
        uint destinationWidth,
        uint destinationHeight,
        ulong destinationByteCount,
        ulong destinationFormat)
    {
        Assert.False(
            GuestImageCopyPolicy.CanCopy(
                sourceAddress,
                sourceWidth: 1920,
                sourceHeight: 1080,
                sourceByteCount: 8294400,
                sourceFormat: 44,
                destinationAddress,
                destinationWidth,
                destinationHeight,
                destinationByteCount,
                destinationFormat));
    }
}
