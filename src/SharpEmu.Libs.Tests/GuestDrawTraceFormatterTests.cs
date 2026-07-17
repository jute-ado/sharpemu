// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using SharpEmu.Libs.Agc;
using SharpEmu.Libs.Gpu;
using Xunit;

namespace SharpEmu.Libs.Tests;

public sealed class GuestDrawTraceFormatterTests
{
    [Fact]
    public void IndexedDrawUsesReferencedVertexRecords()
    {
        var vertexData = new byte[3 * sizeof(float)];
        WriteSingle(vertexData, 0, 0.25f);
        WriteSingle(vertexData, sizeof(float), 0.5f);
        WriteSingle(vertexData, 2 * sizeof(float), 0.75f);
        var indexData = new byte[2 * sizeof(ushort)];
        BinaryPrimitives.WriteUInt16LittleEndian(indexData, 2);
        BinaryPrimitives.WriteUInt16LittleEndian(
            indexData.AsSpan(sizeof(ushort)),
            0);
        var vertexBuffer = new GuestVertexBuffer(
            Location: 1,
            ComponentCount: 1,
            DataFormat: 4,
            NumberFormat: 7,
            BaseAddress: 0x1234000,
            Stride: sizeof(float),
            OffsetBytes: 0,
            Data: vertexData);
        var indexBuffer = new GuestIndexBuffer(indexData, Is32Bit: false);
        var target = new GuestRenderTarget(
            Address: 0xDE40000,
            Width: 1280,
            Height: 720,
            Format: 10,
            NumberType: 0);
        var texture = new GuestDrawTexture(
            Address: 0x2000,
            Width: 2,
            Height: 1,
            Format: 10,
            NumberType: 0,
            RgbaPixels: [0, 1, 2, 3, 4, 5, 6, 7],
            IsFallback: false,
            IsStorage: false,
            Pitch: 2);
        var globalMemory = new byte[2 * sizeof(float)];
        WriteSingle(globalMemory, 0, 1.5f);
        WriteSingle(globalMemory, sizeof(float), -2.25f);

        var trace = GuestDrawTraceFormatter.Format(
            matchingDraw: 2,
            exportShaderAddress: 0x5561D00,
            pixelShaderAddress: 0x5662100,
            primitiveType: 4,
            vertexCount: 2,
            instanceCount: 1,
            indexBuffer,
            [texture],
            [new GuestMemoryBuffer(0x3000, globalMemory)],
            [vertexBuffer],
            [target],
            new GuestRenderState(
                [
                    new GuestBlendState(
                        Enable: true,
                        ColorSrcFactor: 5,
                        ColorDstFactor: 6,
                        ColorFunc: 0,
                        AlphaSrcFactor: 1,
                        AlphaDstFactor: 0,
                        AlphaFunc: 0,
                        SeparateAlphaBlend: true,
                        WriteMask: 0xF),
                ],
                new GuestRect(0, 0, 1280, 720),
                new GuestViewport(0, 0, 1280, 720, 0, 1)),
            firstVertex: 5,
            vertexOffset: -3,
            firstInstance: 7);

        Assert.Contains(
            "match=2 es=0x0000000005561D00 ps=0x0000000005662100",
            trace,
            StringComparison.Ordinal);
        Assert.Contains(
            "first_vertex=5 vertex_offset=-3 first_instance=7",
            trace,
            StringComparison.Ordinal);
        Assert.Contains(
            "target[0]=0x000000000DE40000 1280x720 fmt=10/0",
            trace,
            StringComparison.Ordinal);
        Assert.Contains(
            "scissor=0,0 1280x720",
            trace,
            StringComparison.Ordinal);
        Assert.Contains(
            "viewport=0,0 1280x720 depth=0..1",
            trace,
            StringComparison.Ordinal);
        Assert.Contains(
            "blend[0] enabled=True color=5,6,0 alpha=1,0,0 " +
            "separate=True write=0xF",
            trace,
            StringComparison.Ordinal);
        Assert.Contains(
            "texture[0]=0x0000000000002000 2x1 fmt=10/0 " +
            "tile=0 pitch=2 mip=0/1 storage=False fallback=False bytes=8 " +
            "nonzero=7 hash=0x",
            trace,
            StringComparison.Ordinal);
        Assert.Contains(
            "rgba=R:0-4/1/2,G:1-5/2/2,B:2-6/2/2,A:3-7/2/2",
            trace,
            StringComparison.Ordinal);
        Assert.Contains(
            "global[0]=0x0000000000003000 bytes=8 hash=0x",
            trace,
            StringComparison.Ordinal);
        Assert.Contains("f32=(1.5,-2.25)", trace, StringComparison.Ordinal);
        Assert.Contains("indices=2,0", trace, StringComparison.Ordinal);
        Assert.Contains(
            "buffer[0] loc=1 base=0x0000000001234000 fmt=4/7x1",
            trace,
            StringComparison.Ordinal);
        Assert.Contains(
            "v2: raw=0000403F f32=(0.75)",
            trace,
            StringComparison.Ordinal);
        Assert.Contains(
            "v0: raw=0000803E f32=(0.25)",
            trace,
            StringComparison.Ordinal);
    }

    [Fact]
    public void OutOfRangeVertexIsReportedWithoutReadingPastBuffer()
    {
        var vertexBuffer = new GuestVertexBuffer(
            Location: 0,
            ComponentCount: 4,
            DataFormat: 14,
            NumberFormat: 7,
            BaseAddress: 0x1000,
            Stride: 16,
            OffsetBytes: 0,
            Data: new byte[16]);
        var indexData = new byte[sizeof(uint)];
        BinaryPrimitives.WriteUInt32LittleEndian(indexData, 99);

        var trace = GuestDrawTraceFormatter.Format(
            matchingDraw: 1,
            exportShaderAddress: 1,
            pixelShaderAddress: 2,
            primitiveType: 4,
            vertexCount: 1,
            instanceCount: 1,
            new GuestIndexBuffer(indexData, Is32Bit: true),
            [],
            [],
            [vertexBuffer],
            [],
            GuestRenderState.Default);

        Assert.Contains("indices=99", trace, StringComparison.Ordinal);
        Assert.Contains("v99: unavailable", trace, StringComparison.Ordinal);
    }

    private static void WriteSingle(
        Span<byte> destination,
        int offset,
        float value) =>
        BinaryPrimitives.WriteInt32LittleEndian(
            destination[offset..],
            BitConverter.SingleToInt32Bits(value));
}
