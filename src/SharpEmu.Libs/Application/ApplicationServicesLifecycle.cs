// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.Json;
using SharpEmu.Libs.PlayGo;
using SharpEmu.Libs.GameUpdate;

namespace SharpEmu.Libs.Application;

/// <summary>
/// Owns process-scoped application service state that must not cross guest sessions.
/// </summary>
public static class ApplicationServicesLifecycle
{
    public static void ResetRuntimeState()
    {
        PlayGoExports.ResetRuntimeState();
        JsonExports.ResetRuntimeState();
        GameUpdateExports.ResetRuntimeState();
    }
}
