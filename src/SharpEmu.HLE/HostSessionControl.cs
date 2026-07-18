// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.HLE;

/// <summary>
/// Lets host-facing libraries (VideoOut, AudioOut) request cooperative guest
/// shutdown without taking a dependency on SharpEmu.Core.
/// </summary>
public static class HostSessionControl
{
    private static ShutdownRegistration? _shutdownRegistration;
    private static long _embeddedHostWindow;
    private static long _embeddedHostDisplay;

    /// <summary>
    /// Native GUI surface used by an isolated emulator child. Input backends
    /// use it to treat the launcher window as the active game window.
    /// </summary>
    public static nint EmbeddedHostWindow => unchecked((nint)Interlocked.Read(ref _embeddedHostWindow));

    /// <summary>X11 Display* paired with <see cref="EmbeddedHostWindow"/> when available.</summary>
    public static nint EmbeddedHostDisplay => unchecked((nint)Interlocked.Read(ref _embeddedHostDisplay));

    public static void SetEmbeddedHostSurface(nint window, nint display = 0)
    {
        Interlocked.Exchange(ref _embeddedHostDisplay, unchecked((long)display));
        Interlocked.Exchange(ref _embeddedHostWindow, unchecked((long)window));
    }

    public static IDisposable RegisterShutdownHandler(Action<string> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);

        var registration = new ShutdownRegistration(handler);
        Interlocked.Exchange(ref _shutdownRegistration, registration);
        return registration;
    }

    public static void RequestShutdown(string reason)
    {
        try
        {
            Volatile.Read(ref _shutdownRegistration)?.Invoke(reason);
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(
                $"[LOADER][WARN] Host shutdown handler failed: {exception.Message}");
        }
    }

    private sealed class ShutdownRegistration(Action<string> handler) : IDisposable
    {
        public void Invoke(string reason)
        {
            handler(reason);
        }

        public void Dispose()
        {
            Interlocked.CompareExchange(ref _shutdownRegistration, null, this);
        }
    }
}
