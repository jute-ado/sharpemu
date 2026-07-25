// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.Agc;
using Xunit;

namespace SharpEmu.Libs.Tests.Agc;

// TryDetile's exact-XOR fast path (PS5 swizzle modes 5/9/24/27) factors the
// AddrLib bit-interleave into independent per-column X and per-row Y terms so
// the inner loop is one array load and one XOR instead of a 16-bit interleave.
// These tests pin that the factored output stays byte-identical to the direct
// AddrLib address equation.
public sealed class GnmTilingDetileTests
{
    // Independent re-derivation of the 64 KiB RB+ R_X equation (swizzle mode 27,
    // 2 bytes/element) straight from the address-bit table, so the tiled source
    // layout does not depend on TryDetile's own internal factoring.
    private static readonly (uint XMask, uint YMask)[] RbPlus64KRenderX2Bpp =
    [
        (0, 0), (1u << 0, 0), (1u << 1, 0), (1u << 2, 0),
        (0, 1u << 0), (0, 1u << 1), (0, 1u << 2), (1u << 3, 0),
        (1u << 7, (1u << 4) | (1u << 7)), (1u << 4, 1u << 4), (1u << 6, 1u << 5), (1u << 5, 1u << 6),
        (0, 1u << 3), (1u << 6, 0), (1u << 7, 1u << 7), (1u << 8, 1u << 6),
    ];

    private static uint ReferenceOffset(uint x, uint y, (uint XMask, uint YMask)[] pattern)
    {
        uint offset = 0;
        for (var bit = 0; bit < pattern.Length; bit++)
        {
            var parity = (System.Numerics.BitOperations.PopCount(x & pattern[bit].XMask) +
                          System.Numerics.BitOperations.PopCount(y & pattern[bit].YMask)) & 1;
            offset |= (uint)parity << bit;
        }

        return offset;
    }

    [Theory]
    [InlineData(384, 200)]
    [InlineData(768, 512)]
    public void TryDetile_ExactXorMode27_MatchesReferenceAddressEquation(
        int elementsWide,
        int elementsHigh)
    {
        const uint swizzleMode = 27; // 64 KiB RB+ R_X
        const int bytesPerElement = 2;
        const int blockBytes = 65536;
        // SquareBlockDimensions(32768 elements): 15 bits split 8/7, x favored.
        const int blockWidth = 256;
        const int blockHeight = 128;
        var blocksPerRow = (elementsWide + blockWidth - 1) / blockWidth;
        var blocksPerColumn = (elementsHigh + blockHeight - 1) / blockHeight;

        // Lay out a tiled source where each element stores its own linear index,
        // placed at the byte address the AddrLib equation dictates. The tiled
        // buffer is sized by padded whole blocks (block addressing overshoots the
        // linear extent). A correct detile must recover ascending linear indices.
        var tiled = new byte[blocksPerRow * blocksPerColumn * blockBytes];
        for (var y = 0; y < elementsHigh; y++)
        {
            for (var x = 0; x < elementsWide; x++)
            {
                var blockIndex = (long)(y / blockHeight) * blocksPerRow + (x / blockWidth);
                // The equation yields a byte offset within the block (bit 0 is
                // Zero at 2bpp, keeping element writes 2-byte aligned).
                var sourceByte = (int)(blockIndex * blockBytes +
                    ReferenceOffset((uint)x, (uint)y, RbPlus64KRenderX2Bpp));
                var linearIndex = (ushort)(y * elementsWide + x);
                tiled[sourceByte] = (byte)linearIndex;
                tiled[sourceByte + 1] = (byte)(linearIndex >> 8);
            }
        }

        var linear = new byte[elementsWide * elementsHigh * bytesPerElement];
        var ok = GnmTiling.TryDetile(tiled, linear, swizzleMode, elementsWide, elementsHigh, bytesPerElement);

        Assert.True(ok);
        for (var i = 0; i < elementsWide * elementsHigh; i++)
        {
            var value = (ushort)(linear[i * 2] | (linear[i * 2 + 1] << 8));
            Assert.Equal((ushort)i, value);
        }
    }

    [Theory]
    [InlineData(27u, 2, 384, 200)]
    [InlineData(27u, 4, 256, 256)]
    [InlineData(9u, 4, 300, 300)]
    [InlineData(24u, 4, 128, 256)]
    [InlineData(5u, 4, 200, 120)]
    [InlineData(8u, 4, 128, 128)]
    [InlineData(1u, 4, 64, 64)]
    public void GetDetileParamsReproducesOptimizedCpuDetile(
        uint mode,
        int bytesPerElement,
        int width,
        int height)
    {
        var parameters = GnmTiling.GetDetileParams(
            mode,
            bytesPerElement,
            width,
            height);
        Assert.True(parameters.IsSupported);

        var blocksHigh =
            (height + parameters.BlockHeight - 1) / parameters.BlockHeight;
        var tiled = new byte[
            checked(parameters.BlocksPerRow * blocksHigh * parameters.BlockBytes)];
        for (var index = 0; index < tiled.Length; index++)
        {
            tiled[index] = (byte)((index * 31 + 7) & 0xFF);
        }

        var expected = new byte[checked(width * height * bytesPerElement)];
        Assert.True(
            GnmTiling.TryDetile(
                tiled,
                expected,
                mode,
                width,
                height,
                bytesPerElement));

        var actual = DetileViaParams(
            tiled,
            parameters,
            width,
            height,
            bytesPerElement);
        Assert.Equal(expected, actual);
    }

    [Theory]
    [InlineData(27u, 2, 384, 200)]
    [InlineData(27u, 4, 256, 256)]
    [InlineData(9u, 4, 300, 300)]
    [InlineData(24u, 4, 128, 256)]
    [InlineData(5u, 4, 200, 120)]
    [InlineData(8u, 4, 128, 128)]
    [InlineData(1u, 4, 64, 64)]
    [InlineData(27u, 8, 256, 256)]
    [InlineData(27u, 16, 128, 128)]
    [InlineData(9u, 8, 128, 96)]
    [InlineData(8u, 16, 64, 64)]
    public void DetileWithParamsMatchesOptimizedCpuDetile(
        uint mode,
        int bytesPerElement,
        int width,
        int height)
    {
        var parameters = GnmTiling.GetDetileParams(
            mode,
            bytesPerElement,
            width,
            height);
        Assert.True(parameters.IsSupported);

        var blocksHigh =
            (height + parameters.BlockHeight - 1) / parameters.BlockHeight;
        var tiled = new byte[
            checked(parameters.BlocksPerRow * blocksHigh * parameters.BlockBytes)];
        for (var index = 0; index < tiled.Length; index++)
        {
            tiled[index] = (byte)((index * 31 + 7) & 0xFF);
        }

        var expected = new byte[checked(width * height * bytesPerElement)];
        Assert.True(
            GnmTiling.TryDetile(
                tiled,
                expected,
                mode,
                width,
                height,
                bytesPerElement));

        var actual = new byte[expected.Length];
        Assert.True(GnmTiling.DetileWithParams(parameters, tiled, actual));
        Assert.Equal(expected, actual);
    }

    [Theory]
    [InlineData(27u, 4, 256, 256, 3)]
    [InlineData(9u, 4, 128, 96, 2)]
    [InlineData(24u, 4, 64, 128, 4)]
    public void DetileWithParamsKeepsArraySlicesIndependent(
        uint mode,
        int bytesPerElement,
        int width,
        int height,
        int layers)
    {
        var parameters = GnmTiling.GetDetileParams(
            mode,
            bytesPerElement,
            width,
            height);
        Assert.True(parameters.IsSupported);

        var blocksHigh =
            (height + parameters.BlockHeight - 1) / parameters.BlockHeight;
        var tiledSliceBytes = checked(
            parameters.BlocksPerRow * blocksHigh * parameters.BlockBytes);
        var linearSliceBytes = checked(width * height * bytesPerElement);
        var tiled = new byte[checked(tiledSliceBytes * layers)];
        for (var layer = 0; layer < layers; layer++)
        {
            for (var index = 0; index < tiledSliceBytes; index++)
            {
                tiled[layer * tiledSliceBytes + index] =
                    (byte)((index * 31 + 7 + layer * 101) & 0xFF);
            }
        }

        var expected = new byte[checked(linearSliceBytes * layers)];
        var actual = new byte[expected.Length];
        for (var layer = 0; layer < layers; layer++)
        {
            Assert.True(
                GnmTiling.TryDetile(
                    tiled.AsSpan(layer * tiledSliceBytes, tiledSliceBytes),
                    expected.AsSpan(layer * linearSliceBytes, linearSliceBytes),
                    mode,
                    width,
                    height,
                    bytesPerElement));
            Assert.True(
                GnmTiling.DetileWithParams(
                    parameters,
                    tiled.AsSpan(layer * tiledSliceBytes, tiledSliceBytes),
                    actual.AsSpan(layer * linearSliceBytes, linearSliceBytes)));
        }

        Assert.Equal(expected, actual);
    }

    [Theory]
    [InlineData(0u, 4)]
    [InlineData(27u, 3)]
    public void GetDetileParamsRejectsUnsupportedLayouts(
        uint mode,
        int bytesPerElement)
    {
        var parameters = GnmTiling.GetDetileParams(
            mode,
            bytesPerElement,
            elementsWide: 64,
            elementsHigh: 64);

        Assert.False(parameters.IsSupported);
    }

    [Fact]
    public void DetileWithParamsRejectsTruncatedStorageWithoutChangingOutput()
    {
        var parameters = GnmTiling.GetDetileParams(
            swizzleMode: 27,
            bytesPerElement: 4,
            elementsWide: 64,
            elementsHigh: 64);
        Assert.True(parameters.IsSupported);
        var output = Enumerable.Repeat((byte)0xA5, 64 * 64 * 4).ToArray();
        var before = output.ToArray();

        Assert.False(
            GnmTiling.DetileWithParams(
                parameters,
                new byte[parameters.BlockBytes - 1],
                output));
        Assert.Equal(before, output);
    }

    [Fact]
    public void DetileWithParamsRejectsSmallOutputWithoutChangingIt()
    {
        var parameters = GnmTiling.GetDetileParams(
            swizzleMode: 27,
            bytesPerElement: 4,
            elementsWide: 64,
            elementsHigh: 64);
        Assert.True(parameters.IsSupported);
        var tiled = new byte[parameters.BlockBytes];
        var output = Enumerable.Repeat((byte)0xA5, 64 * 64 * 4 - 1).ToArray();
        var before = output.ToArray();

        Assert.False(GnmTiling.DetileWithParams(parameters, tiled, output));
        Assert.Equal(before, output);
    }

    private static byte[] DetileViaParams(
        byte[] tiled,
        DetileParams parameters,
        int width,
        int height,
        int bytesPerElement)
    {
        var linear = new byte[checked(width * height * bytesPerElement)];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var blockX = x / parameters.BlockWidth;
                var blockY = y / parameters.BlockHeight;
                var inBlockX = x % parameters.BlockWidth;
                var inBlockY = y % parameters.BlockHeight;
                var inBlockByte = parameters.Equation == DetileEquation.ExactXor
                    ? parameters.XByteTerm[x & parameters.XMask] ^
                      parameters.YByteTerm[y & parameters.YMask]
                    : parameters.BlockTable[
                        inBlockY * parameters.BlockWidth + inBlockX] *
                      parameters.BytesPerElement;
                var sourceByte =
                    ((long)blockY * parameters.BlocksPerRow + blockX) *
                    parameters.BlockBytes +
                    inBlockByte;
                var destinationByte =
                    ((long)y * width + x) * bytesPerElement;
                if (sourceByte < 0 ||
                    sourceByte + bytesPerElement > tiled.Length)
                {
                    continue;
                }

                Array.Copy(
                    tiled,
                    sourceByte,
                    linear,
                    destinationByte,
                    bytesPerElement);
            }
        }

        return linear;
    }
}
