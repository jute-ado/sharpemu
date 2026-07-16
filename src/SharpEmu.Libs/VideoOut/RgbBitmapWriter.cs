// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;

namespace SharpEmu.Libs.VideoOut;

internal static class RgbBitmapWriter
{
    private const int HeaderSize = 54;

    public static void Write(
        string path,
        uint width,
        uint height,
        ReadOnlySpan<byte> rgb)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (width == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(width));
        }
        if (height == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(height));
        }

        var sourceStride = checked((int)width * 3);
        var expectedLength = checked(sourceStride * (int)height);
        if (rgb.Length != expectedLength)
        {
            throw new ArgumentException(
                "RGB data length does not match the bitmap dimensions.",
                nameof(rgb));
        }

        var rowStride = checked((sourceStride + 3) & ~3);
        var pixelBytes = checked(rowStride * (int)height);
        var fileSize = checked(HeaderSize + pixelBytes);
        using var stream = File.Create(path);
        Span<byte> header = stackalloc byte[HeaderSize];
        header.Clear();
        header[0] = (byte)'B';
        header[1] = (byte)'M';
        BinaryPrimitives.WriteUInt32LittleEndian(
            header[0x02..],
            (uint)fileSize);
        BinaryPrimitives.WriteUInt32LittleEndian(header[0x0A..], HeaderSize);
        BinaryPrimitives.WriteUInt32LittleEndian(header[0x0E..], 40);
        BinaryPrimitives.WriteInt32LittleEndian(header[0x12..], (int)width);
        BinaryPrimitives.WriteInt32LittleEndian(header[0x16..], -(int)height);
        BinaryPrimitives.WriteUInt16LittleEndian(header[0x1A..], 1);
        BinaryPrimitives.WriteUInt16LittleEndian(header[0x1C..], 24);
        BinaryPrimitives.WriteUInt32LittleEndian(
            header[0x22..],
            (uint)pixelBytes);
        stream.Write(header);

        var row = new byte[rowStride];
        for (var y = 0; y < (int)height; y++)
        {
            row.AsSpan().Clear();
            var source = rgb.Slice(y * sourceStride, sourceStride);
            for (var x = 0; x < (int)width; x++)
            {
                row[(x * 3) + 0] = source[(x * 3) + 2];
                row[(x * 3) + 1] = source[(x * 3) + 1];
                row[(x * 3) + 2] = source[(x * 3) + 0];
            }

            stream.Write(row);
        }
    }
}
