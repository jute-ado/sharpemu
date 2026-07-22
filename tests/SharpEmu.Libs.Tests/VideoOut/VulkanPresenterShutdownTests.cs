// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Diagnostics;
using SharpEmu.Libs.VideoOut;
using Xunit;

namespace SharpEmu.Libs.Tests.VideoOut;

public sealed class VulkanPresenterShutdownTests
{
    [Fact]
    public void WaitForPresenterCloseReturnsAfterPresenterSignalsCompletion()
    {
        var gate = new object();
        Thread? presenter = null;
        using var started = new ManualResetEventSlim();
        var presenterThread = new Thread(() =>
        {
            started.Set();
            Thread.Sleep(25);
            lock (gate)
            {
                presenter = null;
                Monitor.PulseAll(gate);
            }
        });
        presenter = presenterThread;

        presenterThread.Start();
        Assert.True(started.Wait(TimeSpan.FromSeconds(1)));

        Assert.True(VulkanVideoPresenter.WaitForPresenterClose(
            gate,
            () => presenter,
            TimeSpan.FromSeconds(1)));
        Assert.True(presenterThread.Join(TimeSpan.FromSeconds(1)));
    }

    [Fact]
    public void WaitForPresenterCloseTimesOutWhilePresenterIsRunning()
    {
        var gate = new object();
        using var release = new ManualResetEventSlim();
        var presenter = new Thread(release.Wait);
        presenter.Start();

        try
        {
            Assert.False(VulkanVideoPresenter.WaitForPresenterClose(
                gate,
                () => presenter,
                TimeSpan.FromMilliseconds(20)));
        }
        finally
        {
            release.Set();
            Assert.True(presenter.Join(TimeSpan.FromSeconds(1)));
        }
    }

    [Fact]
    public void WaitForPresenterCloseDoesNotBlockPresenterThread()
    {
        var gate = new object();
        var stopwatch = Stopwatch.StartNew();

        Assert.False(VulkanVideoPresenter.WaitForPresenterClose(
            gate,
            () => Thread.CurrentThread,
            TimeSpan.FromSeconds(1)));

        Assert.True(stopwatch.Elapsed < TimeSpan.FromMilliseconds(500));
    }
}
