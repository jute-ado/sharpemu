// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.Gpu;
using SharpEmu.Libs.VideoOut;
using Xunit;

namespace SharpEmu.Libs.Tests.VideoOut;

public sealed class VideoOutCompressionPolicyTests
{
    private const ulong AlignedMetadataAddress = 0x9000_0100;

    [Fact]
    public void UncompressedRegistrationRequiresEmptyDccState()
    {
        Assert.Equal(
            GuestDisplayCompression.Uncompressed,
            VideoOutCompressionPolicy.Classify(0, 0, 0, 0));
        Assert.Equal(
            GuestDisplayCompression.Unsupported,
            VideoOutCompressionPolicy.Classify(
                0,
                AlignedMetadataAddress,
                0,
                0));
        Assert.Equal(
            GuestDisplayCompression.Unsupported,
            VideoOutCompressionPolicy.Classify(0, 0, 0x48, 0));
        Assert.Equal(
            GuestDisplayCompression.Unsupported,
            VideoOutCompressionPolicy.Classify(0, 0, 0, 1));
    }

    [Theory]
    [InlineData(0x48u, 1)]
    [InlineData(0x208u, 2)]
    public void CompressedRegistrationRecognizesSupportedDccControls(
        uint dccControl,
        int expected)
    {
        Assert.Equal(
            (GuestDisplayCompression)expected,
            VideoOutCompressionPolicy.Classify(
                1,
                AlignedMetadataAddress,
                dccControl,
                0));
    }

    [Theory]
    [InlineData(0UL, 0x48u, 0UL)]
    [InlineData(0x90000101UL, 0x48u, 0UL)]
    [InlineData(0x90000100UL, 0x204u, 0UL)]
    [InlineData(0x90000100UL, 0x48u, 1UL)]
    public void CompressedRegistrationRejectsUnsafeMetadata(
        ulong metadataAddress,
        uint dccControl,
        ulong dccClearColor)
    {
        Assert.Equal(
            GuestDisplayCompression.Unsupported,
            VideoOutCompressionPolicy.Classify(
                1,
                metadataAddress,
                dccControl,
                dccClearColor));
    }

    [Fact]
    public void UnknownCategoryIsUnsupported()
    {
        Assert.Equal(
            GuestDisplayCompression.Unsupported,
            VideoOutCompressionPolicy.Classify(
                2,
                AlignedMetadataAddress,
                0x48,
                0));
    }

    [Theory]
    [InlineData(0, true)]
    [InlineData(1, false)]
    [InlineData(2, false)]
    [InlineData(3, false)]
    public void OnlyUncompressedSurfacesCanSeedFromGuestBytes(
        int compression,
        bool expected)
    {
        Assert.Equal(
            expected,
            VideoOutCompressionPolicy.CanSeedFromGuestMemory(
                (GuestDisplayCompression)compression));
    }
}
