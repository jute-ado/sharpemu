// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.Libs.Gpu;

internal static class GuestImageCopyPolicy
{
    public static bool CanCopy(
        ulong sourceAddress,
        uint sourceWidth,
        uint sourceHeight,
        ulong sourceByteCount,
        ulong sourceFormat,
        ulong destinationAddress,
        uint destinationWidth,
        uint destinationHeight,
        ulong destinationByteCount,
        ulong destinationFormat) =>
        sourceAddress != 0 &&
        destinationAddress != 0 &&
        sourceAddress != destinationAddress &&
        sourceWidth != 0 &&
        sourceHeight != 0 &&
        sourceWidth == destinationWidth &&
        sourceHeight == destinationHeight &&
        sourceByteCount != 0 &&
        sourceByteCount == destinationByteCount &&
        sourceFormat == destinationFormat;
}
