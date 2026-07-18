// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.Gpu;
using SharpEmu.Libs.Agc;
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
    uint GuestFormat,
    uint SwizzleMode = 0,
    ulong StencilAddress = 0,
    uint StencilSwizzleMode = 0,
    Format AttachmentFormat = Format.Undefined);

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

        if (!GuestDepthFormatPolicy.TryResolve(
                depth.GuestFormat,
                out var formatPolicy) ||
            textureFormat != formatPolicy.TextureFormat ||
            !formatPolicy.HasNativeDepthAspect(depth.AttachmentFormat) ||
            texture.IsStorage ||
            texture.MipLevel != 0 ||
            texture.MipLevels != 1 ||
            texture.DstSelect is not (0 or 0xFAC) ||
            texture.Width != depth.Width ||
            texture.Height != depth.Height ||
            texture.TileMode != 0 &&
            texture.TileMode != depth.SwizzleMode ||
            HasUnsafeStencilOverlap(depth, formatPolicy.BytesPerElement))
        {
            return GuestDepthAliasKind.Incompatible;
        }

        return depthIsAttached
            ? GuestDepthAliasKind.AttachmentFeedback
            : GuestDepthAliasKind.ExactSample;
    }

    private static bool HasUnsafeStencilOverlap(
        GuestDepthAliasSurface depth,
        uint depthBytesPerElement)
    {
        if (depth.StencilAddress == 0)
        {
            return false;
        }

        if (!TryGetSurfaceEnd(
                depth.Address,
                depth.Width,
                depth.Height,
                depthBytesPerElement,
                depth.SwizzleMode,
                out var depthEnd) ||
            !TryGetSurfaceEnd(
                depth.StencilAddress,
                depth.Width,
                depth.Height,
                sizeof(byte),
                depth.StencilSwizzleMode,
                out var stencilEnd))
        {
            return true;
        }

        return depth.Address < stencilEnd &&
            depth.StencilAddress < depthEnd;
    }

    private static bool TryGetSurfaceEnd(
        ulong address,
        uint width,
        uint height,
        uint bytesPerElement,
        uint swizzleMode,
        out ulong end)
    {
        end = 0;
        ulong byteCount;
        try
        {
            byteCount = checked((ulong)width * height * bytesPerElement);
        }
        catch (OverflowException)
        {
            return false;
        }

        if (swizzleMode != 0 &&
            (!GnmTiling.NeedsDetile(swizzleMode) ||
             width > int.MaxValue ||
             height > int.MaxValue ||
             !GnmTiling.TryGetTiledByteCount(
                 swizzleMode,
                 (int)width,
                 (int)height,
                 checked((int)bytesPerElement),
                 out byteCount)))
        {
            return false;
        }

        if (byteCount == 0 || address > ulong.MaxValue - byteCount)
        {
            return false;
        }

        end = address + byteCount;
        return true;
    }
}
