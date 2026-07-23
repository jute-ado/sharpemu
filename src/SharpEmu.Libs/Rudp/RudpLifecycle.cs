// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.Libs.Rudp;

public static class RudpLifecycle
{
    public static void ResetRuntimeState() => RudpExports.ResetRuntimeState();
}
