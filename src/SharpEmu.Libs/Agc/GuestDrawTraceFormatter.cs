// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using SharpEmu.Libs.Gpu;

namespace SharpEmu.Libs.Agc;

internal static class GuestDrawTraceFormatter
{
    private const int MaximumVertices = 8;
    private const int MaximumRecordBytes = 32;

    public static string Format(
        int matchingDraw,
        ulong exportShaderAddress,
        ulong pixelShaderAddress,
        uint primitiveType,
        uint vertexCount,
        uint instanceCount,
        GuestIndexBuffer? indexBuffer,
        IReadOnlyList<GuestDrawTexture> textures,
        IReadOnlyList<GuestMemoryBuffer> globalMemoryBuffers,
        IReadOnlyList<GuestVertexBuffer> vertexBuffers,
        IReadOnlyList<GuestRenderTarget> renderTargets,
        GuestRenderState renderState)
    {
        var text = new StringBuilder();
        text.Append(
            CultureInfo.InvariantCulture,
            $"agc.draw_vertex_trace match={matchingDraw} " +
            $"es=0x{exportShaderAddress:X16} ps=0x{pixelShaderAddress:X16} " +
            $"prim=0x{primitiveType:X} vertices={vertexCount} " +
            $"instances={instanceCount} indexed={indexBuffer is not null} " +
            $"buffers={vertexBuffers.Count} targets={renderTargets.Count}");

        for (var targetIndex = 0;
            targetIndex < renderTargets.Count;
            targetIndex++)
        {
            var target = renderTargets[targetIndex];
            text.Append(
                CultureInfo.InvariantCulture,
                $"\n  target[{targetIndex}]=0x{target.Address:X16} " +
                $"{target.Width}x{target.Height} " +
                $"fmt={target.Format}/{target.NumberType}");
        }
        AppendRenderState(text, renderState);
        AppendTextures(text, textures);
        AppendGlobalMemoryBuffers(text, globalMemoryBuffers);

        var tracedVertexCount = Math.Min(
            checked((int)Math.Min(vertexCount, int.MaxValue)),
            MaximumVertices);
        Span<uint> vertexIndices = stackalloc uint[MaximumVertices];
        var selectedVertexCount = SelectVertexIndices(
            indexBuffer,
            tracedVertexCount,
            vertexIndices);

        if (indexBuffer is not null)
        {
            text.Append("\n  indices=");
            if (selectedVertexCount == 0)
            {
                text.Append("unavailable");
            }
            else
            {
                for (var index = 0; index < selectedVertexCount; index++)
                {
                    if (index != 0)
                    {
                        text.Append(',');
                    }
                    text.Append(
                        vertexIndices[index].ToString(
                            CultureInfo.InvariantCulture));
                }
            }
        }

        for (var bufferIndex = 0;
            bufferIndex < vertexBuffers.Count;
            bufferIndex++)
        {
            var buffer = vertexBuffers[bufferIndex];
            text.Append(
                CultureInfo.InvariantCulture,
                $"\n  buffer[{bufferIndex}] loc={buffer.Location} " +
                $"base=0x{buffer.BaseAddress:X16} " +
                $"fmt={buffer.DataFormat}/{buffer.NumberFormat}" +
                $"x{buffer.ComponentCount} stride={buffer.Stride} " +
                $"offset={buffer.OffsetBytes} bytes={buffer.Data.Length}");

            for (var selectedIndex = 0;
                selectedIndex < selectedVertexCount;
                selectedIndex++)
            {
                AppendVertexRecord(
                    text,
                    buffer,
                    vertexIndices[selectedIndex]);
            }
        }

        return text.ToString();
    }

    private static void AppendGlobalMemoryBuffers(
        StringBuilder text,
        IReadOnlyList<GuestMemoryBuffer> buffers)
    {
        for (var bufferIndex = 0;
            bufferIndex < buffers.Count;
            bufferIndex++)
        {
            var buffer = buffers[bufferIndex];
            ulong fingerprint = 14695981039346656037UL;
            foreach (var value in buffer.Data)
            {
                fingerprint = (fingerprint ^ value) * 1099511628211UL;
            }

            text.Append(
                CultureInfo.InvariantCulture,
                $"\n  global[{bufferIndex}]=0x{buffer.BaseAddress:X16} " +
                $"bytes={buffer.Data.Length} hash=0x{fingerprint:X16}");
            var floatCount = Math.Min(
                buffer.Data.Length / sizeof(float),
                32);
            if (floatCount == 0)
            {
                continue;
            }

            text.Append(" f32=(");
            for (var valueIndex = 0;
                valueIndex < floatCount;
                valueIndex++)
            {
                if (valueIndex != 0)
                {
                    text.Append(',');
                }

                var bits = BinaryPrimitives.ReadInt32LittleEndian(
                    buffer.Data.AsSpan(
                        valueIndex * sizeof(float),
                        sizeof(float)));
                text.Append(
                    BitConverter.Int32BitsToSingle(bits).ToString(
                        "R",
                        CultureInfo.InvariantCulture));
            }
            text.Append(')');
        }
    }

    private static void AppendTextures(
        StringBuilder text,
        IReadOnlyList<GuestDrawTexture> textures)
    {
        Span<byte> channelMinimums = stackalloc byte[4];
        Span<byte> channelMaximums = stackalloc byte[4];
        Span<long> channelNonzeroCounts = stackalloc long[4];
        for (var textureIndex = 0;
            textureIndex < textures.Count;
            textureIndex++)
        {
            var texture = textures[textureIndex];
            var nonzeroBytes = 0L;
            ulong fingerprint = 14695981039346656037UL;
            foreach (var value in texture.RgbaPixels)
            {
                nonzeroBytes += value == 0 ? 0 : 1;
                fingerprint = (fingerprint ^ value) * 1099511628211UL;
            }
            channelMinimums.Fill(byte.MaxValue);
            channelMaximums.Clear();
            channelNonzeroCounts.Clear();
            var rgbaByteCount = texture.RgbaPixels.Length -
                texture.RgbaPixels.Length % 4;
            for (var byteIndex = 0;
                byteIndex < rgbaByteCount;
                byteIndex++)
            {
                var channel = byteIndex & 3;
                var value = texture.RgbaPixels[byteIndex];
                channelMinimums[channel] = Math.Min(
                    channelMinimums[channel],
                    value);
                channelMaximums[channel] = Math.Max(
                    channelMaximums[channel],
                    value);
                channelNonzeroCounts[channel] += value == 0 ? 0 : 1;
            }

            text.Append(
                CultureInfo.InvariantCulture,
                $"\n  texture[{textureIndex}]=0x{texture.Address:X16} " +
                $"{texture.Width}x{texture.Height} " +
                $"fmt={texture.Format}/{texture.NumberType} " +
                $"tile={texture.TileMode} pitch={texture.Pitch} " +
                $"mip={texture.MipLevel}/{texture.MipLevels} " +
                $"storage={texture.IsStorage} fallback={texture.IsFallback} " +
                $"bytes={texture.RgbaPixels.Length} " +
                $"nonzero={nonzeroBytes} hash=0x{fingerprint:X16}");
            if (rgbaByteCount != 0)
            {
                var pixelCount = rgbaByteCount / 4;
                text.Append(
                    CultureInfo.InvariantCulture,
                    $" rgba=R:{channelMinimums[0]}-{channelMaximums[0]}/" +
                    $"{channelNonzeroCounts[0]}/{pixelCount}," +
                    $"G:{channelMinimums[1]}-{channelMaximums[1]}/" +
                    $"{channelNonzeroCounts[1]}/{pixelCount}," +
                    $"B:{channelMinimums[2]}-{channelMaximums[2]}/" +
                    $"{channelNonzeroCounts[2]}/{pixelCount}," +
                    $"A:{channelMinimums[3]}-{channelMaximums[3]}/" +
                    $"{channelNonzeroCounts[3]}/{pixelCount}");
            }
        }
    }

    private static void AppendRenderState(
        StringBuilder text,
        GuestRenderState renderState)
    {
        if (renderState.Scissor is { } scissor)
        {
            text.Append(
                CultureInfo.InvariantCulture,
                $"\n  scissor={scissor.X},{scissor.Y} " +
                $"{scissor.Width}x{scissor.Height}");
        }
        else
        {
            text.Append("\n  scissor=none");
        }

        if (renderState.Viewport is { } viewport)
        {
            text.Append(
                CultureInfo.InvariantCulture,
                $"\n  viewport={viewport.X:R},{viewport.Y:R} " +
                $"{viewport.Width:R}x{viewport.Height:R} " +
                $"depth={viewport.MinDepth:R}..{viewport.MaxDepth:R}");
        }
        else
        {
            text.Append("\n  viewport=none");
        }

        for (var blendIndex = 0;
            blendIndex < renderState.Blends.Count;
            blendIndex++)
        {
            var blend = renderState.Blends[blendIndex];
            text.Append(
                CultureInfo.InvariantCulture,
                $"\n  blend[{blendIndex}] enabled={blend.Enable} " +
                $"color={blend.ColorSrcFactor},{blend.ColorDstFactor}," +
                $"{blend.ColorFunc} alpha={blend.AlphaSrcFactor}," +
                $"{blend.AlphaDstFactor},{blend.AlphaFunc} " +
                $"separate={blend.SeparateAlphaBlend} " +
                $"write=0x{blend.WriteMask:X}");
        }
    }

    private static int SelectVertexIndices(
        GuestIndexBuffer? indexBuffer,
        int requestedCount,
        Span<uint> destination)
    {
        if (requestedCount <= 0)
        {
            return 0;
        }

        if (indexBuffer is null)
        {
            for (var index = 0; index < requestedCount; index++)
            {
                destination[index] = (uint)index;
            }
            return requestedCount;
        }

        var indexSize = indexBuffer.Is32Bit ? sizeof(uint) : sizeof(ushort);
        var availableCount = indexBuffer.Data.Length / indexSize;
        var count = Math.Min(requestedCount, availableCount);
        for (var index = 0; index < count; index++)
        {
            var source = indexBuffer.Data.AsSpan(index * indexSize, indexSize);
            destination[index] = indexBuffer.Is32Bit
                ? BinaryPrimitives.ReadUInt32LittleEndian(source)
                : BinaryPrimitives.ReadUInt16LittleEndian(source);
        }
        return count;
    }

    private static void AppendVertexRecord(
        StringBuilder text,
        GuestVertexBuffer buffer,
        uint vertexIndex)
    {
        var stride = buffer.Stride != 0
            ? buffer.Stride
            : Math.Max(buffer.ComponentCount, 1) * sizeof(float);
        var baseOffset = (ulong)buffer.OffsetBytes + (ulong)vertexIndex * stride;
        if (baseOffset >= (ulong)buffer.Data.Length)
        {
            text.Append(
                CultureInfo.InvariantCulture,
                $"\n    v{vertexIndex}: unavailable");
            return;
        }

        var availableBytes = buffer.Data.Length - checked((int)baseOffset);
        var attributeBytes = GetAttributeByteCount(
            buffer.DataFormat,
            buffer.ComponentCount);
        var recordByteCount = Math.Min(
            availableBytes,
            Math.Min(
                MaximumRecordBytes,
                checked((int)Math.Max(attributeBytes, 1))));
        var record = buffer.Data.AsSpan(
            checked((int)baseOffset),
            recordByteCount);
        text.Append(
            CultureInfo.InvariantCulture,
            $"\n    v{vertexIndex}: raw={Convert.ToHexString(record)}");

        if (buffer.NumberFormat == 7 &&
            IsFloat32DataFormat(buffer.DataFormat) &&
            record.Length >= buffer.ComponentCount * sizeof(float))
        {
            text.Append(" f32=(");
            for (var component = 0u;
                component < buffer.ComponentCount;
                component++)
            {
                if (component != 0)
                {
                    text.Append(',');
                }

                var bits = BinaryPrimitives.ReadInt32LittleEndian(
                    record.Slice(
                        checked((int)component * sizeof(float)),
                        sizeof(float)));
                text.Append(
                    BitConverter.Int32BitsToSingle(bits).ToString(
                        "R",
                        CultureInfo.InvariantCulture));
            }
            text.Append(')');
        }
    }

    private static uint GetAttributeByteCount(
        uint dataFormat,
        uint componentCount) =>
        dataFormat switch
        {
            1 => 1,
            2 => 2,
            3 => 2,
            4 => 4,
            5 => 4,
            6 => 4,
            7 => 4,
            8 => 4,
            9 => 4,
            10 => 4,
            11 => 8,
            12 => 8,
            13 => 12,
            14 => 16,
            16 => 2,
            17 => 2,
            19 => 2,
            34 => 4,
            _ => Math.Max(componentCount, 1) * sizeof(float),
        };

    private static bool IsFloat32DataFormat(uint dataFormat) =>
        dataFormat is 4 or 11 or 13 or 14;
}
