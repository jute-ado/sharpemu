// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.VideoOut;
using Xunit;

namespace SharpEmu.Libs.Tests.VideoOut;

public sealed class VulkanGuestImageByteCountTests
{
    [Fact]
    public void InitialDataReservationAcceptsOnlyTheFirstPendingSeed()
    {
        const ulong address = 0xF000_0000_1234UL;
        Assert.True(VulkanVideoPresenter.GuestImageWantsInitialData(address));

        VulkanVideoPresenter.ProvideGuestImageInitialData(
            address,
            [1, 2, 3, 4]);

        Assert.False(VulkanVideoPresenter.GuestImageWantsInitialData(address));
    }

    [Theory]
    [InlineData(10u, 642u, 362u, 929616UL)]
    [InlineData(12u, 642u, 362u, 1859232UL)]
    [InlineData(13u, 2u, 2u, 48UL)]
    public void UsesGuestSurfaceTexelSize(
        uint format,
        uint width,
        uint height,
        ulong expected)
    {
        Assert.Equal(
            expected,
            VulkanVideoPresenter.GetGuestImageByteCount(format, width, height));
    }

    [Theory]
    [InlineData(169u, 4u, 4u, 8UL)]
    [InlineData(175u, 4u, 4u, 8UL)]
    [InlineData(173u, 5u, 5u, 64UL)]
    public void UsesCompressedBlockExtent(
        uint format,
        uint width,
        uint height,
        ulong expected)
    {
        Assert.Equal(
            expected,
            VulkanVideoPresenter.GetGuestImageByteCount(format, width, height));
    }

    [Theory]
    [InlineData(10u, 0u, 1u)]
    [InlineData(10u, 1u, 0u)]
    [InlineData(14u, uint.MaxValue, uint.MaxValue)]
    public void RejectsEmptyOrOverflowingImages(
        uint format,
        uint width,
        uint height)
    {
        Assert.Equal(
            0UL,
            VulkanVideoPresenter.GetGuestImageByteCount(format, width, height));
    }
}
