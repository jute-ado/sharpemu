// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using Silk.NET.Vulkan;

namespace SharpEmu.Libs.VideoOut;

internal readonly record struct GuestDepthFormatPolicy(
    uint GuestFormat,
    uint BytesPerElement,
    Format TextureFormat,
    Format AttachmentFormat)
{
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
                AttachmentFormat: Format.D16Unorm),
            3 => new GuestDepthFormatPolicy(
                GuestFormat: 3,
                BytesPerElement: sizeof(float),
                TextureFormat: Format.R32Sfloat,
                AttachmentFormat: Format.D32Sfloat),
            _ => default,
        };
        return policy.GuestFormat != 0;
    }
}
