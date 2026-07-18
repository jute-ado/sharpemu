// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.Gpu;

namespace SharpEmu.Libs.VideoOut;

internal static class VideoOutCompressionPolicy
{
    internal const ulong UncompressedCategory = 0;
    internal const ulong CompressedCategory = 1;
    internal const uint DccControl256_256_0 = 0x48;
    internal const uint DccControl256_64_64 = 0x208;

    internal static GuestDisplayCompression Classify(
        ulong category,
        ulong metadataAddress,
        uint dccControl,
        ulong dccClearColor)
    {
        if (category == UncompressedCategory)
        {
            return metadataAddress == 0 &&
                dccControl == 0 &&
                dccClearColor == 0
                    ? GuestDisplayCompression.Uncompressed
                    : GuestDisplayCompression.Unsupported;
        }

        if (category != CompressedCategory ||
            metadataAddress == 0 ||
            (metadataAddress & 0xFF) != 0 ||
            dccClearColor != 0)
        {
            return GuestDisplayCompression.Unsupported;
        }

        return dccControl switch
        {
            DccControl256_256_0 =>
                GuestDisplayCompression.Dcc256_256_0,
            DccControl256_64_64 =>
                GuestDisplayCompression.Dcc256_64_64,
            _ => GuestDisplayCompression.Unsupported,
        };
    }

    internal static bool CanSeedFromGuestMemory(
        GuestDisplayCompression compression) =>
        compression == GuestDisplayCompression.Uncompressed;
}
