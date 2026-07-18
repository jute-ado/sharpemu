// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using SharpEmu.Libs.VideoOut;
using System.Buffers.Binary;
using Xunit;

namespace SharpEmu.Libs.Tests.VideoOut;

public sealed class VideoOutBufferAttributeTests
{
    private const ulong MemoryBase = 0x1_0000_0000;
    private const ulong AttributeAddress = MemoryBase + 0x100;

    [Fact]
    public void Attribute2ParserPreservesDccFields()
    {
        var memory = new FakeCpuMemory(MemoryBase, 0x1000);
        var context = new CpuContext(memory, Generation.Gen5);
        Span<byte> bytes = stackalloc byte[0x50];
        BinaryPrimitives.WriteUInt32LittleEndian(
            bytes[0x04..0x08],
            7);
        BinaryPrimitives.WriteUInt32LittleEndian(
            bytes[0x0C..0x10],
            1920);
        BinaryPrimitives.WriteUInt32LittleEndian(
            bytes[0x10..0x14],
            1080);
        BinaryPrimitives.WriteUInt64LittleEndian(
            bytes[0x18..0x20],
            8);
        BinaryPrimitives.WriteUInt64LittleEndian(
            bytes[0x20..0x28],
            0x8100_0000_0000_0000);
        BinaryPrimitives.WriteUInt64LittleEndian(
            bytes[0x28..0x30],
            0x1122_3344_5566_7788);
        BinaryPrimitives.WriteUInt32LittleEndian(
            bytes[0x30..0x34],
            0x208);
        Assert.True(memory.TryWrite(AttributeAddress, bytes));

        Assert.True(
            VideoOutExports.TryReadBufferAttribute(
                context,
                AttributeAddress,
                attribute2: true,
                out var attribute));
        Assert.Equal(0x8100_0000_0000_0000UL, attribute.PixelFormat);
        Assert.Equal(7u, attribute.TilingMode);
        Assert.Equal(1920u, attribute.Width);
        Assert.Equal(1080u, attribute.Height);
        Assert.Equal(1920u, attribute.PitchInPixel);
        Assert.Equal(8UL, attribute.Option);
        Assert.Equal(0x208u, attribute.DccControl);
        Assert.Equal(
            0x1122_3344_5566_7788UL,
            attribute.DccClearColor);
    }

    [Fact]
    public void Attribute2ParserRequiresTheCompleteStructure()
    {
        var memory = new FakeCpuMemory(MemoryBase, 0x140);
        var context = new CpuContext(memory, Generation.Gen5);

        Assert.False(
            VideoOutExports.TryReadBufferAttribute(
                context,
                AttributeAddress,
                attribute2: true,
                out _));
    }

    [Fact]
    public void Buffers2EntryParserPreservesDataAndMetadataAddresses()
    {
        var memory = new FakeCpuMemory(MemoryBase, 0x1000);
        var context = new CpuContext(memory, Generation.Gen5);
        Span<byte> entry = stackalloc byte[0x20];
        BinaryPrimitives.WriteUInt64LittleEndian(
            entry[0x00..0x08],
            0x2_0000_0000);
        BinaryPrimitives.WriteUInt64LittleEndian(
            entry[0x08..0x10],
            0x3_0000_0100);
        BinaryPrimitives.WriteUInt64LittleEndian(
            entry[0x10..0x18],
            0xAAAA_AAAA_AAAA_AAAA);
        BinaryPrimitives.WriteUInt64LittleEndian(
            entry[0x18..0x20],
            0xBBBB_BBBB_BBBB_BBBB);
        Assert.True(memory.TryWrite(AttributeAddress, entry));

        Assert.True(
            VideoOutExports.TryReadBuffersEntry(
                context,
                AttributeAddress,
                out var dataAddress,
                out var metadataAddress));
        Assert.Equal(0x2_0000_0000UL, dataAddress);
        Assert.Equal(0x3_0000_0100UL, metadataAddress);
    }

    [Fact]
    public void Buffers2EntryParserRequiresTheCompleteEntry()
    {
        var memory = new FakeCpuMemory(MemoryBase, 0x118);
        var context = new CpuContext(memory, Generation.Gen5);

        Assert.False(
            VideoOutExports.TryReadBuffersEntry(
                context,
                AttributeAddress,
                out _,
                out _));
    }
}
