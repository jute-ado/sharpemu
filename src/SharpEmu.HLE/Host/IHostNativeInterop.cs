// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.HLE.Host;

/// <summary>
/// Host-native helpers shared by the emitted guest worker loop and managed
/// orchestration. Event handles returned here are also consumed by the native
/// functions exposed through <see cref="IHostSymbolResolver"/>.
/// </summary>
public interface IHostNativeInterop
{
    /// <summary>
    /// Returns a callable target for an emitted Win64-ABI call. Windows returns
    /// the target unchanged; POSIX hosts create a Win64-to-SysV thunk.
    /// </summary>
    nint AdaptGuestAbiCallback(nint hostTarget);

    /// <summary>Creates an auto-reset worker event, or returns 0 on failure.</summary>
    nint CreateWorkerEvent();

    bool SignalWorkerEvent(nint handle);

    /// <summary>A negative timeout waits indefinitely.</summary>
    bool WaitWorkerEvent(nint handle, int timeoutMilliseconds);

    void CloseWorkerEvent(nint handle);
}
