// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using System.Diagnostics;

namespace SharpEmu.Libs.VideoOut;

internal static class GuestImageMetrics
{
    internal const int MaximumDistinctPixelValues = 65_536;

    internal static int CountDistinctPixelValues(
        ReadOnlySpan<byte> bytes,
        int bytesPerPixel,
        int limit)
    {
        if (bytesPerPixel is not (1 or 4 or 8 or 16))
        {
            throw new ArgumentOutOfRangeException(nameof(bytesPerPixel));
        }
        if (limit <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(limit));
        }
        if (bytes.Length % bytesPerPixel != 0)
        {
            throw new ArgumentException(
                "The image byte length must contain complete pixels.",
                nameof(bytes));
        }

        var values = new HashSet<PixelValue>(
            Math.Min(limit, bytes.Length / bytesPerPixel));
        for (var offset = 0; offset < bytes.Length; offset += bytesPerPixel)
        {
            var pixel = bytes.Slice(offset, bytesPerPixel);
            var value = bytesPerPixel switch
            {
                1 => new PixelValue(pixel[0], 0),
                4 => new PixelValue(
                    BinaryPrimitives.ReadUInt32LittleEndian(pixel),
                    0),
                8 => new PixelValue(
                    BinaryPrimitives.ReadUInt64LittleEndian(pixel),
                    0),
                16 => new PixelValue(
                    BinaryPrimitives.ReadUInt64LittleEndian(pixel),
                    BinaryPrimitives.ReadUInt64LittleEndian(pixel[8..])),
                _ => throw new UnreachableException(),
            };
            values.Add(value);
            if (values.Count == limit)
            {
                return limit;
            }
        }

        return values.Count;
    }

    private readonly record struct PixelValue(ulong Low, ulong High);
}
