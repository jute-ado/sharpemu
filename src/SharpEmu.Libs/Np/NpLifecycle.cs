// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.Libs.Np;

public static class NpLifecycle
{
    public static void ResetRuntimeState()
    {
        NpAuthExports.ResetRuntimeState();
        NpManagerExports.ResetRuntimeState();
    }
}
