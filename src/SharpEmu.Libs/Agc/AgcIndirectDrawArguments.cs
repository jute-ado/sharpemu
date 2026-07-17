// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;

namespace SharpEmu.Libs.Agc;

internal readonly record struct AgcIndirectDrawArguments(
    uint VertexCount,
    uint InstanceCount,
    uint FirstVertex,
    uint FirstIndex,
    int VertexOffset,
    uint FirstInstance)
{
    public static bool TryRead(
        CpuContext context,
        ulong argumentBufferAddress,
        uint byteOffset,
        bool indexed,
        out AgcIndirectDrawArguments arguments)
    {
        arguments = default;
        if (argumentBufferAddress == 0 ||
            (byteOffset & 3) != 0 ||
            byteOffset > ulong.MaxValue - argumentBufferAddress)
        {
            return false;
        }

        var address = argumentBufferAddress + byteOffset;
        var lastDwordOffset = indexed ? 16UL : 12UL;
        if (address > ulong.MaxValue - lastDwordOffset)
        {
            return false;
        }

        if (!context.TryReadUInt32(address, out var vertexCount) ||
            !context.TryReadUInt32(address + 4, out var instanceCount))
        {
            return false;
        }

        if (indexed)
        {
            if (!context.TryReadUInt32(address + 8, out var firstIndex) ||
                !context.TryReadUInt32(address + 12, out var vertexOffsetRaw) ||
                !context.TryReadUInt32(address + 16, out var firstInstance))
            {
                return false;
            }

            arguments = new(
                vertexCount,
                instanceCount,
                FirstVertex: 0,
                firstIndex,
                unchecked((int)vertexOffsetRaw),
                firstInstance);
            return true;
        }

        if (!context.TryReadUInt32(address + 8, out var firstVertex) ||
            !context.TryReadUInt32(address + 12, out var nonIndexedFirstInstance))
        {
            return false;
        }

        arguments = new(
            vertexCount,
            instanceCount,
            firstVertex,
            FirstIndex: 0,
            VertexOffset: 0,
            nonIndexedFirstInstance);
        return true;
    }
}
