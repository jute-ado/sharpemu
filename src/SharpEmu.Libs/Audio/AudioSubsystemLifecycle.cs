// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.Ngs2;

namespace SharpEmu.Libs.Audio;

/// <summary>
/// Owns process-scoped audio state that must not cross guest sessions.
/// </summary>
public static class AudioSubsystemLifecycle
{
    public static void ResetRuntimeState()
    {
        AudioOutLifecycle.ResetRuntimeState();
        AudioOut2Exports.ResetRuntimeState();
        AcmExports.ResetRuntimeState();
        FmodCompatExports.ResetRuntimeState();
        Ngs2Exports.ResetRuntimeState();
    }
}
