// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.HLE;

/// <summary>
/// Provides checked arithmetic for the 64-bit guest virtual address space.
/// </summary>
public static class GuestAddress
{
    /// <summary>
    /// Adds an offset without allowing the address to wrap through zero.
    /// </summary>
    public static bool TryAdd(ulong address, ulong offset, out ulong result)
    {
        if (offset > ulong.MaxValue - address)
        {
            result = 0;
            return false;
        }

        result = address + offset;
        return true;
    }

    /// <summary>
    /// Returns whether every byte in a guest range has a representable address.
    /// </summary>
    public static bool IsRangeValid(ulong address, ulong length)
        => length == 0 || length - 1 <= ulong.MaxValue - address;
}
