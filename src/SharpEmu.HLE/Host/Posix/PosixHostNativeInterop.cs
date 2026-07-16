// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.HLE.Host.Posix;

internal sealed class PosixHostNativeInterop : IHostNativeInterop
{
    private readonly Dictionary<nint, nint> _callbackThunks = [];
    private readonly object _callbackThunkGate = new();

    public nint AdaptGuestAbiCallback(nint hostTarget) =>
        GetOrCreateCallbackThunk(hostTarget);

    public nint CreateWorkerEvent() => PosixHostStubs.CreateWorkerEvent();

    public bool SignalWorkerEvent(nint handle) => PosixHostStubs.SignalWorkerEvent(handle);

    public bool WaitWorkerEvent(nint handle, int timeoutMilliseconds) =>
        PosixHostStubs.WaitWorkerEvent(handle, timeoutMilliseconds);

    public void CloseWorkerEvent(nint handle) => PosixHostStubs.DestroyWorkerEvent(handle);

    private nint GetOrCreateCallbackThunk(nint hostTarget)
    {
        lock (_callbackThunkGate)
        {
            if (!_callbackThunks.TryGetValue(hostTarget, out var thunk))
            {
                thunk = PosixHostStubs.CreateWin64ToSysVThunk(hostTarget);
                _callbackThunks.Add(hostTarget, thunk);
            }

            return thunk;
        }
    }
}
