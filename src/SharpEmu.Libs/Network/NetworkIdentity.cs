// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.Libs.Network;

internal static class NetworkIdentity
{
    // A stable, locally administered unicast address gives offline guests a
    // coherent adapter identity without exposing a host hardware address.
    public static ReadOnlySpan<byte> EtherAddress => [0x02, 0, 0, 0, 0, 1];
}
