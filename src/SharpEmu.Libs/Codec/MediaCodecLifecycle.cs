// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.Ajm;

namespace SharpEmu.Libs.Codec;

/// <summary>
/// Owns process-scoped media codec state that must not cross guest sessions.
/// </summary>
public static class MediaCodecLifecycle
{
    public static void ResetRuntimeState()
    {
        AjmExports.ResetRuntimeState();
        CodecExports.ResetRuntimeState();
    }
}
