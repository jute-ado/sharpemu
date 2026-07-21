// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.Libs.AvPlayer;

/// <summary>
/// Owns process-scoped media playback state that must not cross guest sessions.
/// </summary>
public static class AvPlayerLifecycle
{
    public static void ResetRuntimeState()
    {
        AvPlayerExports.ResetRuntimeState();
    }
}
