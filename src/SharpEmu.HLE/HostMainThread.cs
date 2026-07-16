// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Collections.Concurrent;

namespace SharpEmu.HLE;

/// <summary>
/// Runs work on the real process main thread. macOS only allows AppKit (and
/// therefore GLFW windowing) on that thread, so the CLI moves emulation onto
/// a worker thread, parks the main thread in <see cref="Pump"/>, and the
/// video presenter posts its window loop here. On other platforms
/// <see cref="IsAvailable"/> stays false and nothing changes.
/// </summary>
public static class HostMainThread
{
    private static readonly HostMainThreadDispatcher Dispatcher = new();

    public static bool IsAvailable => Dispatcher.IsAvailable;

    /// <summary>
    /// Registers a callback invoked by <see cref="Shutdown"/> so a
    /// long-running posted work item (the presenter's window loop) can be
    /// asked to return to the pump.
    /// </summary>
    public static void SetShutdownRequestHandler(Action handler) =>
        Dispatcher.SetShutdownRequestHandler(handler);

    /// <summary>Marks the pump as present. Call before guest code can run.</summary>
    public static void Enable() => Dispatcher.Enable();

    public static void Post(Action work) => Dispatcher.Post(work);

    /// <summary>
    /// Services posted work on the calling (main) thread until
    /// <see cref="Shutdown"/> is called and the queue drains.
    /// </summary>
    public static void Pump() => Dispatcher.Pump();

    public static void Shutdown() => Dispatcher.Shutdown();
}

internal sealed class HostMainThreadDispatcher
{
    private readonly BlockingCollection<Action> _work = new();
    private readonly object _stateGate = new();
    private Action? _shutdownRequestHandler;
    private bool _isAvailable;
    private bool _shutdownRequested;

    public bool IsAvailable => Volatile.Read(ref _isAvailable);

    public void SetShutdownRequestHandler(Action handler)
    {
        ArgumentNullException.ThrowIfNull(handler);

        var invokeImmediately = false;
        lock (_stateGate)
        {
            if (_shutdownRequested)
            {
                invokeImmediately = true;
            }
            else
            {
                _shutdownRequestHandler = handler;
            }
        }

        if (invokeImmediately)
        {
            InvokeShutdownHandler(handler);
        }
    }

    public void Enable()
    {
        lock (_stateGate)
        {
            if (_shutdownRequested)
            {
                throw new InvalidOperationException(
                    "The host main-thread dispatcher cannot be enabled after shutdown.");
            }

            Volatile.Write(ref _isAvailable, true);
        }
    }

    public void Post(Action work)
    {
        ArgumentNullException.ThrowIfNull(work);

        try
        {
            _work.Add(work);
        }
        catch (InvalidOperationException)
        {
            // Shutdown already requested; the process is exiting.
        }
    }

    public void Pump()
    {
        foreach (var work in _work.GetConsumingEnumerable())
        {
            try
            {
                work();
            }
            catch (Exception exception)
            {
                Console.Error.WriteLine($"[LOADER][ERROR] Main-thread work failed: {exception}");
            }
        }
    }

    public void Shutdown()
    {
        Action? shutdownRequestHandler;
        lock (_stateGate)
        {
            if (_shutdownRequested)
            {
                return;
            }

            _shutdownRequested = true;
            Volatile.Write(ref _isAvailable, false);
            shutdownRequestHandler = _shutdownRequestHandler;
            _shutdownRequestHandler = null;
        }

        if (shutdownRequestHandler is not null)
        {
            InvokeShutdownHandler(shutdownRequestHandler);
        }

        _work.CompleteAdding();
    }

    private static void InvokeShutdownHandler(Action handler)
    {
        try
        {
            handler();
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"[LOADER][WARN] Main-thread shutdown handler failed: {exception.Message}");
        }
    }
}
