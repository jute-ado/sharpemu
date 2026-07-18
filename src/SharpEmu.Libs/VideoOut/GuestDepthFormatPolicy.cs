// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using Silk.NET.Vulkan;

namespace SharpEmu.Libs.VideoOut;

internal readonly record struct GuestDepthFormatPolicy(
    uint GuestFormat,
    uint BytesPerElement,
    Format TextureFormat,
    Format AttachmentFormat,
    Format StencilAttachmentFormat0,
    Format StencilAttachmentFormat1,
    Format StencilAttachmentFormat2)
{
    internal bool HasNativeDepthAspect(Format attachmentFormat) =>
        (GuestFormat == 1 &&
         attachmentFormat is Format.D16Unorm or
             Format.D16UnormS8Uint) ||
        (GuestFormat == 3 &&
         attachmentFormat is Format.D32Sfloat or
             Format.D32SfloatS8Uint);

    internal bool TryGetAttachmentCandidate(
        bool hasStencil,
        int index,
        out Format format)
    {
        format = hasStencil
            ? index switch
            {
                0 => StencilAttachmentFormat0,
                1 => StencilAttachmentFormat1,
                2 => StencilAttachmentFormat2,
                _ => Format.Undefined,
            }
            : index == 0
                ? AttachmentFormat
                : Format.Undefined;
        return format != Format.Undefined;
    }

    internal bool TryConvertDepthUpload(
        Format attachmentFormat,
        ReadOnlySpan<byte> source,
        out byte[] converted)
    {
        converted = [];
        if (source.Length == 0 ||
            source.Length % BytesPerElement != 0)
        {
            return false;
        }

        if (HasNativeDepthAspect(attachmentFormat))
        {
            converted = source.ToArray();
            return true;
        }

        if (GuestFormat != 1 ||
            attachmentFormat is not (
                Format.D24UnormS8Uint or
                Format.D32SfloatS8Uint))
        {
            return false;
        }

        converted = new byte[checked(source.Length / sizeof(ushort) * sizeof(uint))];
        for (var sourceOffset = 0;
             sourceOffset < source.Length;
             sourceOffset += sizeof(ushort))
        {
            var value = BinaryPrimitives.ReadUInt16LittleEndian(
                source[sourceOffset..]);
            var encoded = attachmentFormat == Format.D24UnormS8Uint
                ? (uint)(((ulong)value * 0x00ff_ffffu + 0x7fffu) /
                    0xffffu)
                : (uint)BitConverter.SingleToInt32Bits(
                    value / 65535.0f);
            BinaryPrimitives.WriteUInt32LittleEndian(
                converted.AsSpan(sourceOffset * 2),
                encoded);
        }

        return true;
    }

    internal static bool TryResolve(
        uint guestFormat,
        out GuestDepthFormatPolicy policy)
    {
        policy = guestFormat switch
        {
            1 => new GuestDepthFormatPolicy(
                GuestFormat: 1,
                BytesPerElement: sizeof(ushort),
                TextureFormat: Format.R16Unorm,
                AttachmentFormat: Format.D16Unorm,
                StencilAttachmentFormat0: Format.D16UnormS8Uint,
                StencilAttachmentFormat1: Format.D24UnormS8Uint,
                StencilAttachmentFormat2: Format.D32SfloatS8Uint),
            3 => new GuestDepthFormatPolicy(
                GuestFormat: 3,
                BytesPerElement: sizeof(float),
                TextureFormat: Format.R32Sfloat,
                AttachmentFormat: Format.D32Sfloat,
                StencilAttachmentFormat0: Format.D32SfloatS8Uint,
                StencilAttachmentFormat1: Format.Undefined,
                StencilAttachmentFormat2: Format.Undefined),
            _ => default,
        };
        return policy.GuestFormat != 0;
    }
}
