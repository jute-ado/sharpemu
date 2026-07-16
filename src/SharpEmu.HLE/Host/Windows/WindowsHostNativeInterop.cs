// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Runtime.InteropServices;

namespace SharpEmu.HLE.Host.Windows;

internal sealed partial class WindowsHostNativeInterop : IHostNativeInterop
{
    private const uint Infinite = uint.MaxValue;
    private const uint WaitObject0 = 0;

    public nint AdaptGuestAbiCallback(nint hostTarget) => hostTarget;

    public nint CreateWorkerEvent() => CreateEvent(0, false, false, null);

    public bool SignalWorkerEvent(nint handle) => SetEvent(handle);

    public bool WaitWorkerEvent(nint handle, int timeoutMilliseconds)
    {
        var timeout = timeoutMilliseconds < 0
            ? Infinite
            : checked((uint)timeoutMilliseconds);
        return WaitForSingleObject(handle, timeout) == WaitObject0;
    }

    public void CloseWorkerEvent(nint handle)
    {
        if (handle != 0)
        {
            _ = CloseHandle(handle);
        }
    }

    [LibraryImport("kernel32.dll", EntryPoint = "CreateEventW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial nint CreateEvent(
        nint eventAttributes,
        [MarshalAs(UnmanagedType.Bool)] bool manualReset,
        [MarshalAs(UnmanagedType.Bool)] bool initialState,
        string? name);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetEvent(nint handle);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial uint WaitForSingleObject(nint handle, uint timeoutMilliseconds);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(nint handle);
}
