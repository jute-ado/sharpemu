// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.VideoOut;
using Xunit;

namespace SharpEmu.Libs.Tests;

public sealed class GuestImageMetricsTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(4)]
    [InlineData(8)]
    [InlineData(16)]
    public void CountsDistinctPixelValuesForSupportedWidths(int bytesPerPixel)
    {
        var pixels = new byte[bytesPerPixel * 4];
        pixels[bytesPerPixel] = 1;
        pixels[bytesPerPixel * 2] = 2;
        pixels[bytesPerPixel * 3] = 1;

        Assert.Equal(
            3,
            GuestImageMetrics.CountDistinctPixelValues(
                pixels,
                bytesPerPixel,
                64));
    }

    [Fact]
    public void StopsCountingAtConfiguredLimit()
    {
        byte[] pixels = [0, 1, 2, 3, 4, 5];

        Assert.Equal(
            3,
            GuestImageMetrics.CountDistinctPixelValues(pixels, 1, 3));
    }

    [Theory]
    [InlineData(0, 2)]
    [InlineData(2, 2)]
    [InlineData(4, 0)]
    public void RejectsInvalidPixelLayoutOrLimit(
        int bytesPerPixel,
        int limit)
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => GuestImageMetrics.CountDistinctPixelValues(
                new byte[4],
                bytesPerPixel,
                limit));
    }

    [Fact]
    public void RejectsPartialTrailingPixel()
    {
        Assert.Throws<ArgumentException>(
            () => GuestImageMetrics.CountDistinctPixelValues(
                new byte[5],
                4,
                2));
    }
}
