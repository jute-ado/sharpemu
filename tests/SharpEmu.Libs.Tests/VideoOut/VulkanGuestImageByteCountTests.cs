// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.VideoOut;
using SharpEmu.Libs.Gpu;
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

    [Fact]
    public void UncompressedRegisteredDisplayCanSeedItsFirstNativeImage()
    {
        const ulong address = 0xF100_0000_0000UL;
        VulkanVideoPresenter.RegisterKnownDisplayBuffer(
            new GuestDisplayBuffer(
                address,
                MetadataAddress: 0,
                Format: 56,
                GuestDisplayCompression.Uncompressed,
                DccControl: 0,
                DccClearColor: 0));

        Assert.True(VulkanVideoPresenter.GuestImageWantsInitialData(address));
    }

    [Theory]
    [InlineData(1, 0x48u)]
    [InlineData(2, 0x208u)]
    public void CompressedRegisteredDisplayRejectsLinearCpuSeed(
        int compression,
        uint dccControl)
    {
        const ulong address = 0xF200_0000_0000UL;
        VulkanVideoPresenter.RegisterKnownDisplayBuffer(
            new GuestDisplayBuffer(
                address,
                MetadataAddress: 0xF300_0000_0100UL,
                Format: 56,
                (GuestDisplayCompression)compression,
                dccControl,
                DccClearColor: 0));

        Assert.False(VulkanVideoPresenter.GuestImageWantsInitialData(address));
        VulkanVideoPresenter.ProvideGuestImageInitialData(
            address,
            [1, 2, 3, 4]);
        Assert.False(VulkanVideoPresenter.GuestImageWantsInitialData(address));
    }

    [Fact]
    public void UnregisteredCompressedDisplayNoLongerBlocksCpuSeed()
    {
        const ulong address = 0xF400_0000_0000UL;
        VulkanVideoPresenter.RegisterKnownDisplayBuffer(
            new GuestDisplayBuffer(
                address,
                MetadataAddress: 0xF500_0000_0100UL,
                Format: 56,
                GuestDisplayCompression.Dcc256_64_64,
                DccControl: 0x208,
                DccClearColor: 0));
        Assert.False(VulkanVideoPresenter.GuestImageWantsInitialData(address));

        VulkanVideoPresenter.UnregisterKnownDisplayBuffer(address);

        Assert.True(VulkanVideoPresenter.GuestImageWantsInitialData(address));
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
