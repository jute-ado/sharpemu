// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.Agc;

namespace SharpEmu.Libs.VideoOut;

/// <summary>
/// Fully validated dimensions and push-constant values for one Vulkan detile
/// dispatch. Resource allocation and command recording consume this plan so
/// they cannot derive different buffer extents or layer strides.
/// </summary>
internal readonly record struct VulkanDetileDispatch(
    uint TexelWidth,
    uint TexelHeight,
    uint ElementsWide,
    uint ElementsHigh,
    uint BlockWidth,
    uint BlockHeight,
    uint BlockElements,
    uint BlocksPerRow,
    uint XMask,
    uint YMask,
    uint SourceSliceElements,
    uint Equation,
    uint UintsPerElement,
    uint GroupCountX,
    uint GroupCountY,
    uint GroupCountZ,
    ulong TiledBytesPerLayer,
    ulong OutputBytes);

/// <summary>
/// Validation and planning shared by the Vulkan detile resource and command
/// paths. Keeping this pure makes unsafe GPU buffer sizing independently
/// testable before any Vulkan object is allocated.
/// </summary>
internal static class VulkanDetilePass
{
    private const uint LocalSize = 8;

    /// <summary>
    /// The compute kernel copies whole 32-bit words and therefore supports
    /// 4-, 8-, and 16-byte elements. Sub-word elements remain on the CPU.
    /// </summary>
    public static bool Supports(in DetileParams parameters) =>
        parameters.Equation is DetileEquation.ExactXor or
            DetileEquation.BlockTable &&
        parameters.BytesPerElement is 4 or 8 or 16;

    /// <summary>
    /// Validates the backend-neutral address model and exact tiled source
    /// extent, then produces all dimensions needed by the Vulkan pass.
    /// </summary>
    public static bool TryCreateDispatch(
        int tiledByteLength,
        int texelWidth,
        int texelHeight,
        uint layers,
        in DetileParams parameters,
        out VulkanDetileDispatch dispatch)
    {
        dispatch = default;
        if (!Supports(parameters) ||
            tiledByteLength <= 0 ||
            texelWidth <= 0 ||
            texelHeight <= 0 ||
            layers == 0 ||
            !HasConsistentBlockGeometry(parameters) ||
            !HasValidEquationTables(parameters))
        {
            return false;
        }

        try
        {
            var blocksHigh = DivideRoundUp(
                checked((ulong)parameters.ElementsHigh),
                checked((ulong)parameters.BlockHeight));
            var tiledBytesPerLayer = checked(
                (ulong)parameters.BlocksPerRow *
                blocksHigh *
                (ulong)parameters.BlockBytes);
            var totalTiledBytes = checked(tiledBytesPerLayer * layers);
            if (totalTiledBytes != (ulong)tiledByteLength ||
                tiledBytesPerLayer % (uint)parameters.BytesPerElement != 0)
            {
                return false;
            }

            var sourceSliceElements =
                tiledBytesPerLayer / (uint)parameters.BytesPerElement;
            if (sourceSliceElements > uint.MaxValue)
            {
                return false;
            }

            var outputBytes = checked(
                (ulong)parameters.ElementsWide *
                (ulong)parameters.ElementsHigh *
                (uint)parameters.BytesPerElement *
                layers);
            var uintsPerElement =
                checked((uint)parameters.BytesPerElement / sizeof(uint));
            var invocationWidth = checked(
                (ulong)parameters.ElementsWide * uintsPerElement);
            var groupCountX = DivideRoundUp(invocationWidth, LocalSize);
            var groupCountY = DivideRoundUp(
                checked((ulong)parameters.ElementsHigh),
                LocalSize);
            if (groupCountX == 0 ||
                groupCountX > uint.MaxValue ||
                groupCountY == 0 ||
                groupCountY > uint.MaxValue)
            {
                return false;
            }

            dispatch = new VulkanDetileDispatch(
                checked((uint)texelWidth),
                checked((uint)texelHeight),
                checked((uint)parameters.ElementsWide),
                checked((uint)parameters.ElementsHigh),
                checked((uint)parameters.BlockWidth),
                checked((uint)parameters.BlockHeight),
                checked((uint)parameters.BlockElements),
                checked((uint)parameters.BlocksPerRow),
                checked((uint)parameters.XMask),
                checked((uint)parameters.YMask),
                checked((uint)sourceSliceElements),
                parameters.Equation == DetileEquation.BlockTable ? 1u : 0u,
                uintsPerElement,
                checked((uint)groupCountX),
                checked((uint)groupCountY),
                layers,
                tiledBytesPerLayer,
                outputBytes);
            return true;
        }
        catch (OverflowException)
        {
            dispatch = default;
            return false;
        }
    }

    private static bool HasConsistentBlockGeometry(
        in DetileParams parameters)
    {
        if (parameters.ElementsWide <= 0 ||
            parameters.ElementsHigh <= 0 ||
            parameters.BlockWidth <= 0 ||
            parameters.BlockHeight <= 0 ||
            parameters.BlockElements <= 0 ||
            parameters.BlockBytes <= 0 ||
            parameters.BlocksPerRow <= 0 ||
            parameters.XMask < 0 ||
            parameters.YMask < 0)
        {
            return false;
        }

        try
        {
            var blockElements = checked(
                parameters.BlockWidth * parameters.BlockHeight);
            var blockBytes = checked(
                parameters.BlockElements * parameters.BytesPerElement);
            var blocksPerRow = checked(
                (parameters.ElementsWide + parameters.BlockWidth - 1) /
                parameters.BlockWidth);
            return blockElements == parameters.BlockElements &&
                blockBytes == parameters.BlockBytes &&
                blocksPerRow == parameters.BlocksPerRow;
        }
        catch (OverflowException)
        {
            return false;
        }
    }

    private static bool HasValidEquationTables(
        in DetileParams parameters)
    {
        if (parameters.Equation == DetileEquation.BlockTable)
        {
            if (parameters.BlockTable is null ||
                parameters.BlockTable.Length != parameters.BlockElements)
            {
                return false;
            }

            foreach (var offset in parameters.BlockTable)
            {
                if ((uint)offset >= (uint)parameters.BlockElements)
                {
                    return false;
                }
            }

            return true;
        }

        if (parameters.XByteTerm is null ||
            parameters.YByteTerm is null ||
            parameters.XMask >= parameters.XByteTerm.Length ||
            parameters.YMask >= parameters.YByteTerm.Length ||
            !IsLowBitMask(parameters.XMask) ||
            !IsLowBitMask(parameters.YMask))
        {
            return false;
        }

        return HasValidByteTerms(
                parameters.XByteTerm,
                parameters.BytesPerElement,
                parameters.BlockBytes) &&
            HasValidByteTerms(
                parameters.YByteTerm,
                parameters.BytesPerElement,
                parameters.BlockBytes);
    }

    private static bool HasValidByteTerms(
        int[] terms,
        int bytesPerElement,
        int blockBytes)
    {
        foreach (var term in terms)
        {
            if (term < 0 ||
                term >= blockBytes ||
                term % bytesPerElement != 0)
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsLowBitMask(int mask) =>
        (mask & (mask + 1)) == 0;

    private static ulong DivideRoundUp(ulong value, ulong divisor) =>
        checked((value + divisor - 1) / divisor);
}
