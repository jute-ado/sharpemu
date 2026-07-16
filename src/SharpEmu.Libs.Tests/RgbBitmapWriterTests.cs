// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using SharpEmu.Libs.VideoOut;
using Xunit;

namespace SharpEmu.Libs.Tests;

public sealed class RgbBitmapWriterTests
{
    [Fact]
    public void WritesTopDownBgrRowsWithFourBytePadding()
    {
        var path = Path.Combine(
            Path.GetTempPath(),
            $"sharpemu-rgb-{Guid.NewGuid():N}.bmp");
        try
        {
            RgbBitmapWriter.Write(
                path,
                width: 2,
                height: 2,
                [
                    0x10, 0x20, 0x30,
                    0x40, 0x50, 0x60,
                    0x70, 0x80, 0x90,
                    0xA0, 0xB0, 0xC0,
                ]);

            var bytes = File.ReadAllBytes(path);
            Assert.Equal((byte)'B', bytes[0]);
            Assert.Equal((byte)'M', bytes[1]);
            Assert.Equal(
                bytes.Length,
                BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(2)));
            Assert.Equal(
                2,
                BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(0x12)));
            Assert.Equal(
                -2,
                BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(0x16)));
            Assert.Equal(
                0,
                BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(0x1E)));
            Assert.Equal(
                [0x00, 0x00, 0x00, 0x00],
                bytes.AsSpan(0x06, 4).ToArray());
            Assert.Equal(
                [
                    0x30, 0x20, 0x10,
                    0x60, 0x50, 0x40,
                    0x00, 0x00,
                    0x90, 0x80, 0x70,
                    0xC0, 0xB0, 0xA0,
                    0x00, 0x00,
                ],
                bytes.AsSpan(54).ToArray());
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void RejectsMismatchedPixelData()
    {
        Assert.Throws<ArgumentException>(
            () => RgbBitmapWriter.Write(
                "unused.bmp",
                width: 2,
                height: 1,
                [0x00, 0x00, 0x00]));
    }
}
