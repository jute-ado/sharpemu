// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.Libs.Gpu;

internal static class GuestWorkPresentationPolicy
{
    internal static bool ShouldProcessGuestWork(
        bool hasReadyPresentation,
        int completedWork,
        int maxGuestWork)
    {
        if (completedWork < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(completedWork));
        }

        if (maxGuestWork <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxGuestWork));
        }

        return !hasReadyPresentation && completedWork < maxGuestWork;
    }
}
