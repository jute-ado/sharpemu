// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.Agc;
using System.Buffers.Binary;
using Xunit;

namespace SharpEmu.Libs.Tests;

public sealed class GnmTilingTests
{
    [Theory]
    [InlineData(1u, 1, 1, 1, 256ul)]
    [InlineData(5u, 1, 1, 4, 4096ul)]
    [InlineData(9u, 8, 8, 8, 65536ul)]
    [InlineData(9u, 257, 256, 1, 131072ul)]
    public void TryGetTiledByteCount_AllocatesWholeSwizzleBlocks(
        uint swizzleMode,
        int elementsWide,
        int elementsHigh,
        int bytesPerElement,
        ulong expected)
    {
        Assert.True(GnmTiling.TryGetTiledByteCount(
            swizzleMode,
            elementsWide,
            elementsHigh,
            bytesPerElement,
            out var actual));
        Assert.Equal(expected, actual);
    }

    [Theory]
    [InlineData(1u, 64, 64, 1, 7u, 2_304ul, 6_400ul)]
    [InlineData(9u, 4_096, 4_096, 4, 13u, 22_413_312ul, 89_522_176ul)]
    public void TryGetBaseMipPlacement_SumsSmallestFirstChainBeforeBaseMip(
        uint swizzleMode,
        int width,
        int height,
        int bytesPerElement,
        uint mipLevels,
        ulong expectedOffset,
        ulong expectedChainSliceBytes)
    {
        Assert.True(GnmTiling.TryGetBaseMipPlacement(
            swizzleMode,
            width,
            height,
            bytesPerElement,
            mipLevels,
            out var byteOffset,
            out var inMipTail,
            out var tailElementX,
            out var tailElementY,
            out var chainSliceBytes));

        Assert.Equal(expectedOffset, byteOffset);
        Assert.False(inMipTail);
        Assert.Equal(0, tailElementX);
        Assert.Equal(0, tailElementY);
        Assert.Equal(expectedChainSliceBytes, chainSliceBytes);
    }

    [Fact]
    public void TryGetBaseMipPlacement_LocatesBaseMipInsideSixtyFourKiBTail()
    {
        Assert.True(GnmTiling.TryGetBaseMipPlacement(
            swizzleMode: 9,
            elementsWide: 4,
            elementsHigh: 4,
            bytesPerElement: 4,
            resourceMipLevels: 4,
            out var byteOffset,
            out var inMipTail,
            out var tailElementX,
            out var tailElementY,
            out var chainSliceBytes));

        Assert.Equal(0ul, byteOffset);
        Assert.True(inMipTail);
        Assert.Equal(64, tailElementX);
        Assert.Equal(0, tailElementY);
        Assert.Equal(65_536ul, chainSliceBytes);
    }

    [Theory]
    [InlineData(0u, 2u)]
    [InlineData(9u, 1u)]
    public void TryGetBaseMipPlacement_RejectsLinearOrSingleLevelResources(
        uint swizzleMode,
        uint mipLevels)
    {
        Assert.False(GnmTiling.TryGetBaseMipPlacement(
            swizzleMode,
            elementsWide: 256,
            elementsHigh: 256,
            bytesPerElement: 4,
            mipLevels,
            out _,
            out _,
            out _,
            out _,
            out _));
    }

    [Fact]
    public void TryDetile_RejectsTruncatedSwizzleBlockWithoutChangingOutput()
    {
        var tiled = new byte[255];
        var linear = Enumerable.Repeat((byte)0xA5, 16 * 16).ToArray();
        var original = linear.ToArray();

        Assert.False(GnmTiling.TryDetile(tiled, linear, 1, 16, 16, 1));
        Assert.Equal(original, linear);
    }

    [Fact]
    public void TryDetile_Standard256ByteMode_ProducesRowMajorElements()
    {
        var tiled = Enumerable.Range(0, 256).Select(value => (byte)value).ToArray();
        var linear = new byte[16 * 16];

        Assert.True(GnmTiling.TryDetile(tiled, linear, 1, 16, 16, 1));

        Assert.Equal(new byte[] { 0, 1, 4, 5 }, linear.AsSpan(0, 4).ToArray());
        Assert.Equal(new byte[] { 2, 3, 6, 7 }, linear.AsSpan(16, 4).ToArray());
    }

    [Fact]
    public void TryDetile_ZOrder4KMode_UsesYAsTheLowMortonBit()
    {
        var tiled = new byte[4096];
        tiled[0] = 0x10;
        tiled[1] = 0x11;
        tiled[2] = 0x12;
        tiled[3] = 0x13;
        var linear = new byte[64 * 64];

        Assert.True(GnmTiling.TryDetile(tiled, linear, 4, 64, 64, 1));

        Assert.Equal(0x10, linear[0]);
        Assert.Equal(0x12, linear[1]);
        Assert.Equal(0x11, linear[64]);
        Assert.Equal(0x13, linear[65]);
    }

    [Fact]
    public void TryDetile_Standard64K16BitMode_MatchesKnownSdkMapping()
    {
        const int width = 256;
        const int height = 128;
        const int bytesPerElement = 2;
        var tiled = new byte[64 * 1024];
        for (var index = 0; index < tiled.Length / bytesPerElement; index++)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(
                tiled.AsSpan(index * bytesPerElement, bytesPerElement),
                checked((ushort)index));
        }

        var linear = new byte[tiled.Length];

        Assert.True(GnmTiling.TryDetile(
            tiled,
            linear,
            swizzleMode: 9,
            elementsWide: width,
            elementsHigh: height,
            bytesPerElement));

        Assert.Equal((ushort)0, ReadElement(linear, width, 0, 0));
        Assert.Equal((ushort)1, ReadElement(linear, width, 1, 0));
        Assert.Equal((ushort)2, ReadElement(linear, width, 2, 0));
        Assert.Equal((ushort)4, ReadElement(linear, width, 4, 0));
        Assert.Equal((ushort)64, ReadElement(linear, width, 8, 0));
        Assert.Equal((ushort)8, ReadElement(linear, width, 0, 1));
        Assert.Equal((ushort)16, ReadElement(linear, width, 0, 2));
        Assert.Equal((ushort)32, ReadElement(linear, width, 0, 4));
    }

    [Theory]
    [InlineData(0u)]
    [InlineData(12u)]
    [InlineData(28u)]
    public void TryDetile_UnsupportedOrLinearModesLeaveOutputUntouched(uint swizzleMode)
    {
        var tiled = new byte[65536];
        var linear = Enumerable.Repeat((byte)0x5A, 16).ToArray();
        var original = linear.ToArray();

        Assert.False(GnmTiling.TryDetile(tiled, linear, swizzleMode, 4, 4, 1));
        Assert.Equal(original, linear);
    }

    private static ushort ReadElement(byte[] linear, int width, int x, int y)
    {
        const int bytesPerElement = 2;
        var offset = checked((y * width + x) * bytesPerElement);
        return BinaryPrimitives.ReadUInt16LittleEndian(
            linear.AsSpan(offset, bytesPerElement));
    }
}
