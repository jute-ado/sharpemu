// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using SharpEmu.HLE;
using SharpEmu.Libs.AvPlayer;
using Xunit;

namespace SharpEmu.Libs.Tests.AvPlayer;

public sealed class AvPlayerFrameLayoutTests
{
    private const ulong MemoryBase = 0x1_0000_0000;
    private const ulong InfoAddress = MemoryBase + 0x100;
    private const ulong BufferAddress = MemoryBase + 0x1000;
    private const ulong Handle = 0xA0_0000_0002;

    [Fact]
    public void LayoutSeparatesVisibleWidthFromSixtyFourBytePitch()
    {
        var layout = AvPlayerExports.GetVideoFrameLayout(1919, 1079);

        Assert.Equal(1920, layout.Width);
        Assert.Equal(1088, layout.Height);
        Assert.Equal(1920, layout.Pitch);
        Assert.Equal(3_133_440, layout.BufferSize);

        var narrowLayout = AvPlayerExports.GetVideoFrameLayout(66, 18);
        Assert.Equal(80, narrowLayout.Width);
        Assert.Equal(32, narrowLayout.Height);
        Assert.Equal(128, narrowLayout.Pitch);
        Assert.Equal(6_144, narrowLayout.BufferSize);
    }

    [Fact]
    public void Nv12CopyUsesPitchedPlanesAndExtendsBottomRows()
    {
        const int width = 6;
        const int height = 4;
        var layout = AvPlayerExports.GetVideoFrameLayout(width, height);
        var source = new byte[width * height * 3 / 2];
        for (var row = 0; row < height; row++)
        {
            source.AsSpan(row * width, width).Fill((byte)(0x10 + row));
        }

        var sourceChromaOffset = width * height;
        for (var row = 0; row < height / 2; row++)
        {
            source.AsSpan(sourceChromaOffset + (row * width), width)
                .Fill((byte)(0x80 + row));
        }

        var destination = Enumerable.Repeat((byte)0xA5, layout.BufferSize).ToArray();
        Assert.True(
            AvPlayerExports.TryCopyNv12Frame(
                source,
                destination,
                width,
                height));

        AssertRow(destination, 0, layout.Pitch, width, 0x10);
        AssertRow(destination, 3, layout.Pitch, width, 0x13);
        AssertRow(destination, 4, layout.Pitch, width, 0x13);
        AssertRow(destination, layout.Height - 1, layout.Pitch, width, 0x13);

        var chromaOffset = layout.Pitch * layout.Height;
        AssertRow(destination, chromaOffset, 0, layout.Pitch, width, 0x80);
        AssertRow(destination, chromaOffset, 1, layout.Pitch, width, 0x81);
        AssertRow(destination, chromaOffset, 2, layout.Pitch, width, 0x81);
        AssertRow(
            destination,
            chromaOffset,
            (layout.Height / 2) - 1,
            layout.Pitch,
            width,
            0x81);
    }

    [Fact]
    public void Nv12CopyRejectsTruncatedBuffersWithoutChangingDestination()
    {
        var layout = AvPlayerExports.GetVideoFrameLayout(6, 4);
        var destination = Enumerable.Repeat((byte)0xA5, layout.BufferSize).ToArray();

        Assert.False(
            AvPlayerExports.TryCopyNv12Frame(
                new byte[(6 * 4 * 3 / 2) - 1],
                destination,
                6,
                4));
        Assert.All(destination, value => Assert.Equal(0xA5, value));
    }

    [Theory]
    [InlineData(false, 40)]
    [InlineData(true, 104)]
    public void VideoFrameWritesAlignedMetadataAndPitchSizedData(
        bool extended,
        int infoSize)
    {
        const int width = 66;
        const int height = 18;
        var layout = AvPlayerExports.GetVideoFrameLayout(width, height);
        var memory = new FakeCpuMemory(MemoryBase, 0x4000);
        var context = new CpuContext(memory, Generation.Gen5);
        var rawFrame = Enumerable.Repeat(
            (byte)0x5A,
            width * height * 3 / 2).ToArray();
        AvPlayerExports.RegisterPlayerForTest(
            Handle,
            width,
            height,
            durationMilliseconds: 1000);

        try
        {
            Assert.True(
                AvPlayerExports.WriteVideoFrameForTest(
                    context,
                    Handle,
                    InfoAddress,
                    BufferAddress,
                    rawFrame,
                    extended));

            var info = new byte[infoSize];
            Assert.True(memory.TryRead(InfoAddress, info));
            Assert.Equal(
                (uint)layout.Width,
                BinaryPrimitives.ReadUInt32LittleEndian(info.AsSpan(24)));
            Assert.Equal(
                (uint)layout.Height,
                BinaryPrimitives.ReadUInt32LittleEndian(info.AsSpan(28)));
            if (extended)
            {
                Assert.Equal(
                    (uint)(layout.Pitch - width),
                    BinaryPrimitives.ReadUInt32LittleEndian(info.AsSpan(48)));
                Assert.Equal(
                    (uint)(layout.Height - height),
                    BinaryPrimitives.ReadUInt32LittleEndian(info.AsSpan(56)));
                Assert.Equal(
                    (uint)layout.Pitch,
                    BinaryPrimitives.ReadUInt32LittleEndian(info.AsSpan(60)));
            }

            var frame = new byte[layout.BufferSize];
            Assert.True(memory.TryRead(BufferAddress, frame));
            Assert.Equal(0x5A, frame[0]);
            Assert.Equal(0, frame[width]);
            Assert.Equal(
                0x5A,
                frame[(layout.Height - 1) * layout.Pitch]);
            Assert.Equal(
                0x5A,
                frame[layout.Pitch * layout.Height]);
            Assert.Equal(layout.BufferSize, frame.Length);
        }
        finally
        {
            AvPlayerExports.RemovePlayerForTest(Handle);
        }
    }

    private static void AssertRow(
        byte[] frame,
        int row,
        int pitch,
        int width,
        byte expected) =>
        AssertRow(frame, 0, row, pitch, width, expected);

    private static void AssertRow(
        byte[] frame,
        int planeOffset,
        int row,
        int pitch,
        int width,
        byte expected)
    {
        var rowBytes = frame.AsSpan(planeOffset + (row * pitch), pitch);
        Assert.All(rowBytes[..width].ToArray(), value => Assert.Equal(expected, value));
        Assert.All(rowBytes[width..].ToArray(), value => Assert.Equal(0, value));
    }
}
