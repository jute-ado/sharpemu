// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.Libs.Agc;

/// <summary>
/// Keeps the AGC-to-backend raw tiled upload seam conservative. Unsupported
/// candidates continue through the established CPU detile path.
/// </summary>
internal static class AgcGpuDetilePolicy
{
    public static bool IsEnabled(string? value) =>
        string.Equals(value, "1", StringComparison.Ordinal);

    public static bool TryCreateSingleLayerParameters(
        bool enabled,
        bool backendSupportsTiledUploads,
        bool hasElementLayout,
        bool baseMipInTail,
        bool isStorage,
        bool isArrayed,
        bool isThreeDimensional,
        bool isCube,
        uint tileMode,
        int bytesPerElement,
        int elementsWide,
        int elementsHigh,
        int tiledByteCount,
        out DetileParams parameters)
    {
        parameters = default;
        if (!enabled ||
            !backendSupportsTiledUploads ||
            !hasElementLayout ||
            baseMipInTail ||
            isStorage ||
            isArrayed ||
            isThreeDimensional ||
            isCube ||
            !TryCreateParameters(
                tileMode,
                bytesPerElement,
                elementsWide,
                elementsHigh,
                tiledByteCount,
                out parameters))
        {
            return false;
        }

        return true;
    }

    public static bool TryCreateArrayLayerParameters(
        bool enabled,
        bool backendSupportsTiledUploads,
        bool hasElementLayout,
        bool baseMipInTail,
        bool isStorage,
        bool isArrayed,
        bool isThreeDimensional,
        bool isCube,
        uint layers,
        uint tileMode,
        int bytesPerElement,
        int elementsWide,
        int elementsHigh,
        int tiledBytesPerLayer,
        int tiledSourceByteCount,
        out DetileParams parameters)
    {
        parameters = default;
        if (!enabled ||
            !backendSupportsTiledUploads ||
            !hasElementLayout ||
            baseMipInTail ||
            isStorage ||
            !isArrayed ||
            isThreeDimensional ||
            isCube ||
            layers <= 1)
        {
            return false;
        }

        try
        {
            if (checked((long)tiledBytesPerLayer * layers) !=
                tiledSourceByteCount)
            {
                return false;
            }
        }
        catch (OverflowException)
        {
            return false;
        }

        return TryCreateParameters(
            tileMode,
            bytesPerElement,
            elementsWide,
            elementsHigh,
            tiledBytesPerLayer,
            out parameters);
    }

    private static bool TryCreateParameters(
        uint tileMode,
        int bytesPerElement,
        int elementsWide,
        int elementsHigh,
        int tiledByteCount,
        out DetileParams parameters)
    {
        parameters = default;
        if (bytesPerElement is not (4 or 8 or 16) ||
            elementsWide <= 0 ||
            elementsHigh <= 0 ||
            tiledByteCount <= 0)
        {
            return false;
        }

        var candidate = GnmTiling.GetDetileParams(
            tileMode,
            bytesPerElement,
            elementsWide,
            elementsHigh);
        if (candidate.Equation is not (
                DetileEquation.ExactXor or DetileEquation.BlockTable) ||
            candidate.BlockHeight <= 0 ||
            candidate.BlockBytes <= 0 ||
            candidate.BlocksPerRow <= 0)
        {
            return false;
        }

        try
        {
            var blockRows = checked(
                (elementsHigh + candidate.BlockHeight - 1) /
                candidate.BlockHeight);
            var requiredTiledBytes = checked(
                candidate.BlocksPerRow *
                blockRows *
                candidate.BlockBytes);
            var outputBytes = checked(
                elementsWide *
                elementsHigh *
                bytesPerElement);
            if (requiredTiledBytes != tiledByteCount ||
                outputBytes > tiledByteCount)
            {
                return false;
            }
        }
        catch (OverflowException)
        {
            return false;
        }

        parameters = candidate;
        return true;
    }
}
