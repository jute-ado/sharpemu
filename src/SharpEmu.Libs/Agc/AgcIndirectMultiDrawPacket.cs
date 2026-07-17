// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;

namespace SharpEmu.Libs.Agc;

internal readonly record struct AgcIndirectMultiDrawPacket(
    ulong ArgumentAddress,
    uint DrawCount,
    uint Stride,
    bool Indexed)
{
    private const uint MaximumDrawCount = 65_536;

    public static bool TryRead(
        CpuContext context,
        ulong packetAddress,
        uint packetLength,
        ulong indirectArgumentBaseAddress,
        bool indexed,
        out AgcIndirectMultiDrawPacket packet)
    {
        packet = default;
        if (packetAddress == 0 ||
            packetAddress > ulong.MaxValue - 32 ||
            packetLength < 10 ||
            indirectArgumentBaseAddress == 0 ||
            !context.TryReadUInt32(packetAddress + 4, out var dataOffset) ||
            !context.TryReadUInt32(packetAddress + 16, out var control) ||
            !context.TryReadUInt32(packetAddress + 20, out var maximumDrawCount) ||
            !context.TryReadUInt32(packetAddress + 24, out var countAddressLow) ||
            !context.TryReadUInt32(packetAddress + 28, out var countAddressHigh) ||
            !context.TryReadUInt32(packetAddress + 32, out var stride) ||
            maximumDrawCount > MaximumDrawCount ||
            stride < (indexed ? 20u : 16u) ||
            (stride & 3) != 0 ||
            dataOffset > ulong.MaxValue - indirectArgumentBaseAddress)
        {
            return false;
        }

        var drawCount = maximumDrawCount;
        var countIsIndirect = (control & (1u << 30)) != 0;
        if (countIsIndirect)
        {
            var countAddress =
                countAddressLow | ((ulong)countAddressHigh << 32);
            if (countAddress == 0 ||
                !context.TryReadUInt32(countAddress, out drawCount))
            {
                return false;
            }

            drawCount = Math.Min(drawCount, maximumDrawCount);
        }

        packet = new(
            indirectArgumentBaseAddress + dataOffset,
            drawCount,
            stride,
            indexed);
        return true;
    }

    public bool TryReadArguments(
        CpuContext context,
        uint drawIndex,
        out AgcIndirectDrawArguments arguments)
    {
        arguments = default;
        if (drawIndex >= DrawCount)
        {
            return false;
        }

        var byteOffset = (ulong)drawIndex * Stride;
        return AgcIndirectDrawArguments.TryRead(
            context,
            ArgumentAddress,
            byteOffset,
            Indexed,
            out arguments);
    }
}
