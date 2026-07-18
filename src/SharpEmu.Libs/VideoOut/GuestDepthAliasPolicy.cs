// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.Gpu;
using Silk.NET.Vulkan;

namespace SharpEmu.Libs.VideoOut;

internal enum GuestDepthAliasKind
{
    None,
    ExactSample,
    AttachmentFeedback,
    Incompatible,
}

internal readonly record struct GuestDepthAliasSurface(
    ulong Address,
    uint Width,
    uint Height,
    uint GuestFormat);

internal static class GuestDepthAliasPolicy
{
    internal static GuestDepthAliasKind Classify(
        GuestDepthAliasSurface depth,
        GuestDrawTexture texture,
        Format textureFormat,
        bool depthIsAttached)
    {
        if (depth.Address == 0 || texture.Address != depth.Address)
        {
            return GuestDepthAliasKind.None;
        }

        if (depth.GuestFormat != 3 ||
            textureFormat != Format.R32Sfloat ||
            texture.IsStorage ||
            texture.MipLevel != 0 ||
            texture.MipLevels != 1 ||
            texture.DstSelect is not (0 or 0xFAC) ||
            texture.Width != depth.Width ||
            texture.Height != depth.Height)
        {
            return GuestDepthAliasKind.Incompatible;
        }

        return depthIsAttached
            ? GuestDepthAliasKind.AttachmentFeedback
            : GuestDepthAliasKind.ExactSample;
    }
}
