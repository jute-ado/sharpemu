// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.Rudp;

namespace SharpEmu.Libs.Network;

/// <summary>
/// Owns process-scoped network state that must not cross guest sessions.
/// </summary>
public static class NetworkLifecycle
{
    public static void ResetRuntimeState()
    {
        HttpExports.ResetRuntimeState();
        Http2Exports.ResetRuntimeState();
        SslExports.ResetRuntimeState();
        NetCtlExports.ResetRuntimeState();
        NetExports.ResetRuntimeState();
        RudpLifecycle.ResetRuntimeState();
    }
}
