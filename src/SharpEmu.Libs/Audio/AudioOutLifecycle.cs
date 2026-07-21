// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.Libs.Audio;

/// <summary>
/// Owns process-scoped AudioOut state that must not cross guest sessions.
/// </summary>
public static class AudioOutLifecycle
{
    public static void ResetRuntimeState()
    {
        AudioOutExports.ResetRuntimeState();
    }
}
