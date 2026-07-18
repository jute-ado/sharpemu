// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using SharpEmu.Libs.VideoOut;
using Silk.NET.Vulkan;
using Xunit;

namespace SharpEmu.Libs.Tests;

public sealed class GuestDepthFormatPolicyTests
{
    [Theory]
    [InlineData(
        1u,
        2u,
        (int)Format.R16Unorm,
        (int)Format.D16Unorm,
        (int)Format.D16UnormS8Uint)]
    [InlineData(
        3u,
        4u,
        (int)Format.R32Sfloat,
        (int)Format.D32Sfloat,
        (int)Format.D32SfloatS8Uint)]
    public void SupportedDepthFormatsHaveOneNativePolicy(
        uint guestFormat,
        uint bytesPerElement,
        int textureFormat,
        int attachmentFormat,
        int firstStencilAttachmentFormat)
    {
        Assert.True(GuestDepthFormatPolicy.TryResolve(
            guestFormat,
            out var policy));
        Assert.Equal(guestFormat, policy.GuestFormat);
        Assert.Equal(bytesPerElement, policy.BytesPerElement);
        Assert.Equal((Format)textureFormat, policy.TextureFormat);
        Assert.Equal((Format)attachmentFormat, policy.AttachmentFormat);
        Assert.True(policy.TryGetAttachmentCandidate(
            hasStencil: false,
            index: 0,
            out var depthCandidate));
        Assert.Equal((Format)attachmentFormat, depthCandidate);
        Assert.False(policy.TryGetAttachmentCandidate(
            hasStencil: false,
            index: 1,
            out _));
        Assert.True(policy.TryGetAttachmentCandidate(
            hasStencil: true,
            index: 0,
            out var stencilCandidate));
        Assert.Equal((Format)firstStencilAttachmentFormat, stencilCandidate);
    }

    [Theory]
    [InlineData(0u)]
    [InlineData(2u)]
    [InlineData(4u)]
    [InlineData(uint.MaxValue)]
    public void UnknownDepthFormatsHaveNoFallbackPolicy(uint guestFormat)
    {
        Assert.False(GuestDepthFormatPolicy.TryResolve(
            guestFormat,
            out var policy));
        Assert.Equal(default, policy);
    }

    [Fact]
    public void Z16StencilCandidatesPreferNativeThenPortablePromotions()
    {
        Assert.True(GuestDepthFormatPolicy.TryResolve(1, out var policy));
        var expected = new[]
        {
            Format.D16UnormS8Uint,
            Format.D24UnormS8Uint,
            Format.D32SfloatS8Uint,
        };

        for (var index = 0; index < expected.Length; index++)
        {
            Assert.True(policy.TryGetAttachmentCandidate(
                hasStencil: true,
                index,
                out var candidate));
            Assert.Equal(expected[index], candidate);
        }
        Assert.False(policy.TryGetAttachmentCandidate(
            hasStencil: true,
            expected.Length,
            out _));
    }

    [Theory]
    [InlineData((int)Format.D16UnormS8Uint)]
    [InlineData((int)Format.D16Unorm)]
    public void NativeZ16UploadIsUnchanged(int attachmentFormat)
    {
        Assert.True(GuestDepthFormatPolicy.TryResolve(1, out var policy));
        byte[] source = [0x34, 0x12, 0xcd, 0xab];

        Assert.True(policy.TryConvertDepthUpload(
            (Format)attachmentFormat,
            source,
            out var converted));

        Assert.Equal(source, converted);
        Assert.NotSame(source, converted);
    }

    [Fact]
    public void Z16UploadPromotesExactlyToD24TransferPlane()
    {
        Assert.True(GuestDepthFormatPolicy.TryResolve(1, out var policy));
        byte[] source = [0x00, 0x00, 0x00, 0x80, 0xff, 0xff];

        Assert.True(policy.TryConvertDepthUpload(
            Format.D24UnormS8Uint,
            source,
            out var converted));

        Assert.Equal(12, converted.Length);
        Assert.Equal(
            0u,
            BinaryPrimitives.ReadUInt32LittleEndian(converted));
        Assert.Equal(
            0x00800080u,
            BinaryPrimitives.ReadUInt32LittleEndian(converted.AsSpan(4)));
        Assert.Equal(
            0x00ffffffu,
            BinaryPrimitives.ReadUInt32LittleEndian(converted.AsSpan(8)));
    }

    [Fact]
    public void Z16UploadPromotesExactlyToD32FloatTransferPlane()
    {
        Assert.True(GuestDepthFormatPolicy.TryResolve(1, out var policy));
        byte[] source = [0x00, 0x00, 0x00, 0x80, 0xff, 0xff];

        Assert.True(policy.TryConvertDepthUpload(
            Format.D32SfloatS8Uint,
            source,
            out var converted));

        Assert.Equal(0f, ReadSingle(converted, 0));
        Assert.Equal(32768f / 65535f, ReadSingle(converted, 4));
        Assert.Equal(1f, ReadSingle(converted, 8));
    }

    [Fact]
    public void UploadConversionRejectsMismatchedOrTruncatedInput()
    {
        Assert.True(GuestDepthFormatPolicy.TryResolve(1, out var policy));

        Assert.False(policy.TryConvertDepthUpload(
            Format.D32Sfloat,
            [0, 0],
            out _));
        Assert.False(policy.TryConvertDepthUpload(
            Format.D24UnormS8Uint,
            [0],
            out _));
        Assert.False(policy.TryConvertDepthUpload(
            Format.D24UnormS8Uint,
            [],
            out _));
    }

    private static float ReadSingle(byte[] values, int offset) =>
        BitConverter.Int32BitsToSingle(
            BinaryPrimitives.ReadInt32LittleEndian(values.AsSpan(offset)));
}
