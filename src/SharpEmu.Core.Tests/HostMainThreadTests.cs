// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using Xunit;

namespace SharpEmu.Core.Tests;

public sealed class HostMainThreadTests
{
    [Fact]
    public void PumpDrainsQueuedWorkAfterShutdown()
    {
        var dispatcher = new HostMainThreadDispatcher();
        var calls = new List<int>();
        dispatcher.Enable();
        dispatcher.Post(() => calls.Add(1));
        dispatcher.Post(() => calls.Add(2));

        dispatcher.Shutdown();
        dispatcher.Pump();

        Assert.Equal([1, 2], calls);
        Assert.False(dispatcher.IsAvailable);
    }

    [Fact]
    public async Task PumpProcessesPostedWorkUntilShutdown()
    {
        var dispatcher = new HostMainThreadDispatcher();
        using var executed = new ManualResetEventSlim();
        dispatcher.Enable();
        var pump = Task.Run(dispatcher.Pump);

        dispatcher.Post(executed.Set);

        Assert.True(executed.Wait(TimeSpan.FromSeconds(5)));
        dispatcher.Shutdown();
        await pump.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(dispatcher.IsAvailable);
    }

    [Fact]
    public void WorkFailureDoesNotPreventQueueDrain()
    {
        var dispatcher = new HostMainThreadDispatcher();
        var completed = false;
        dispatcher.Post(() => throw new InvalidOperationException("expected"));
        dispatcher.Post(() => completed = true);

        dispatcher.Shutdown();
        dispatcher.Pump();

        Assert.True(completed);
    }

    [Fact]
    public void ShutdownIsIdempotentAndRequestsLongRunningWorkOnce()
    {
        var dispatcher = new HostMainThreadDispatcher();
        var shutdownRequests = 0;
        dispatcher.Enable();
        dispatcher.SetShutdownRequestHandler(() => shutdownRequests++);

        dispatcher.Shutdown();
        dispatcher.Shutdown();

        Assert.Equal(1, shutdownRequests);
        Assert.False(dispatcher.IsAvailable);
    }

    [Fact]
    public void ShutdownHandlerCanQueueFinalMainThreadWork()
    {
        var dispatcher = new HostMainThreadDispatcher();
        var cleanupCompleted = false;
        dispatcher.SetShutdownRequestHandler(
            () => dispatcher.Post(() => cleanupCompleted = true));

        dispatcher.Shutdown();
        dispatcher.Pump();

        Assert.True(cleanupCompleted);
    }

    [Fact]
    public void LateShutdownHandlerIsInvokedImmediately()
    {
        var dispatcher = new HostMainThreadDispatcher();
        var shutdownRequested = false;
        dispatcher.Shutdown();

        dispatcher.SetShutdownRequestHandler(() => shutdownRequested = true);

        Assert.True(shutdownRequested);
    }

    [Fact]
    public void ShutdownHandlerFailureDoesNotEscape()
    {
        var dispatcher = new HostMainThreadDispatcher();
        dispatcher.SetShutdownRequestHandler(() => throw new InvalidOperationException("expected"));

        dispatcher.Shutdown();

        Assert.False(dispatcher.IsAvailable);
    }

    [Fact]
    public void PostAfterShutdownIsIgnoredAndEnableIsRejected()
    {
        var dispatcher = new HostMainThreadDispatcher();
        var executed = false;
        dispatcher.Shutdown();

        dispatcher.Post(() => executed = true);
        dispatcher.Pump();

        Assert.False(executed);
        Assert.Throws<InvalidOperationException>(dispatcher.Enable);
    }

    [Fact]
    public void NullWorkAndHandlerAreRejected()
    {
        var dispatcher = new HostMainThreadDispatcher();

        Assert.Throws<ArgumentNullException>(() => dispatcher.Post(null!));
        Assert.Throws<ArgumentNullException>(() => dispatcher.SetShutdownRequestHandler(null!));
    }
}
