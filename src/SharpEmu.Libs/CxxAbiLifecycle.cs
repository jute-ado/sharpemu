// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.Libs.CxxAbi;

/// <summary>
/// Owns process-scoped C++ ABI state that must not cross guest sessions.
/// </summary>
public static class CxxAbiLifecycle
{
    public static void ResetRuntimeState()
    {
        CxaGuardExports.ResetRuntimeState();
    }
}
