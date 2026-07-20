// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.VideoOut;
using Xunit;

namespace SharpEmu.Libs.Tests.VideoOut;

public sealed class VulkanPaddedUploadRowLengthTests
{
    [Theory]
    [InlineData(13u, 4u, 256ul, 208ul, 16u)]
    [InlineData(65u, 2u, 1024ul, 520ul, 128u)]
    public void RecognizedAlignedPitchReturnsSourceRowLength(
        uint width,
        uint height,
        ulong uploadBytes,
        ulong packedBytes,
        uint expectedRowLength)
    {
        Assert.Equal(
            expectedRowLength,
            VulkanVideoPresenter.TryGetPaddedUploadRowLength(
                width,
                height,
                uploadBytes,
                packedBytes));
    }

    [Theory]
    [InlineData(16u, 4u, 256ul, 256ul)]
    [InlineData(13u, 4u, 240ul, 208ul)]
    [InlineData(13u, 4u, 257ul, 208ul)]
    [InlineData(13u, 4u, 256ul, 207ul)]
    public void TightOrUnrecognizedLayoutsRemainRejected(
        uint width,
        uint height,
        ulong uploadBytes,
        ulong packedBytes)
    {
        Assert.Equal(
            0u,
            VulkanVideoPresenter.TryGetPaddedUploadRowLength(
                width,
                height,
                uploadBytes,
                packedBytes));
    }
}
