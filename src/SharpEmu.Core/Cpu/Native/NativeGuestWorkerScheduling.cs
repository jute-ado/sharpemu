// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.Core.Cpu.Native;

internal static class NativeGuestWorkerScheduling
{
    internal static ThreadPriority MapPriority(int guestPriority)
    {
        if (guestPriority <= 478)
        {
            return ThreadPriority.Highest;
        }

        if (guestPriority >= 733)
        {
            return ThreadPriority.Lowest;
        }

        return ThreadPriority.Normal;
    }

    internal static void ApplyPriority(
        int guestPriority,
        Action<ThreadPriority> apply) =>
        apply(MapPriority(guestPriority));
}
