// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;

namespace SharpEmu.Libs.Agc;

internal enum AgcCopyDataSourceKind
{
    Memory,
    Immediate,
}

internal readonly record struct AgcCopyDataPacket(
    AgcCopyDataSourceKind SourceKind,
    ulong SourceAddressOrImmediate,
    ulong DestinationAddress,
    bool Is64Bit)
{
    private const uint PacketDwords = 6;

    public static bool TryRead(
        CpuContext context,
        ulong packetAddress,
        uint packetLength,
        out AgcCopyDataPacket packet)
    {
        packet = default;
        if (packetAddress == 0 ||
            packetAddress > ulong.MaxValue - 20 ||
            packetLength != PacketDwords ||
            !context.TryReadUInt32(packetAddress + 4, out var control) ||
            !context.TryReadUInt32(packetAddress + 8, out var sourceLow) ||
            !context.TryReadUInt32(packetAddress + 12, out var sourceHigh) ||
            !context.TryReadUInt32(packetAddress + 16, out var destinationLow) ||
            !context.TryReadUInt32(packetAddress + 20, out var destinationHigh))
        {
            return false;
        }

        // AGC extends the four-bit source selector with control bit 30. The
        // destination selector is encoded without its equivalent low bit.
        var sourceSelector =
            ((control & 0xFu) << 1) |
            ((control >> 30) & 1u);
        var destinationSelector = ((control >> 8) & 0xFu) << 1;
        var sourceKind = sourceSelector switch
        {
            2 or 3 or 4 or 5 or 6 or 7 => AgcCopyDataSourceKind.Memory,
            10 or 11 => AgcCopyDataSourceKind.Immediate,
            _ => (AgcCopyDataSourceKind?)null,
        };
        if (sourceKind is null ||
            destinationSelector is not (2 or 4 or 6))
        {
            return false;
        }

        var is64Bit = (control & (1u << 16)) != 0;
        var alignmentMask = is64Bit ? 7UL : 3UL;
        var source = sourceLow | ((ulong)sourceHigh << 32);
        var destination = destinationLow | ((ulong)destinationHigh << 32);
        if (destination == 0 ||
            (destination & alignmentMask) != 0 ||
            (sourceKind == AgcCopyDataSourceKind.Memory &&
             (source == 0 || (source & alignmentMask) != 0)))
        {
            return false;
        }

        packet = new(sourceKind.Value, source, destination, is64Bit);
        return true;
    }

    public bool TryExecute(CpuContext context)
    {
        if (SourceKind == AgcCopyDataSourceKind.Immediate)
        {
            return Is64Bit
                ? context.TryWriteUInt64(DestinationAddress, SourceAddressOrImmediate)
                : context.TryWriteUInt32(DestinationAddress, (uint)SourceAddressOrImmediate);
        }

        if (Is64Bit)
        {
            return
                context.TryReadUInt64(SourceAddressOrImmediate, out var value) &&
                context.TryWriteUInt64(DestinationAddress, value);
        }

        return
            context.TryReadUInt32(SourceAddressOrImmediate, out var value32) &&
            context.TryWriteUInt32(DestinationAddress, value32);
    }
}
