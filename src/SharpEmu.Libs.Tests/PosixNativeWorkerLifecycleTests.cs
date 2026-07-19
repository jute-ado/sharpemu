// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Runtime.InteropServices;
using SharpEmu.HLE.Host.Posix;
using Xunit;

namespace SharpEmu.Libs.Tests;

public sealed unsafe class PosixNativeWorkerLifecycleTests
{
    [Fact]
    public void CompletedWorkerCanBeJoinedAndClosed()
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
        {
            return;
        }

        var doneEvent = PosixHostStubs.CreateWorkerEvent();
        Assert.NotEqual(0, doneEvent);
        nint worker = 0;
        try
        {
            worker = PosixHostStubs.CreateWorkerThread(
                (nint)(delegate* unmanaged<nint, nint>)&SignalDone,
                doneEvent,
                4u * 1024u * 1024u,
                out _);
            Assert.NotEqual(0, worker);
            Assert.True(
                PosixHostStubs.WaitWorkerEvent(
                    doneEvent,
                    timeoutMilliseconds: 1000));

            PosixHostStubs.JoinExitedWorkerThread(worker);
            PosixHostStubs.CloseWorkerThreadHandle(worker);
            worker = 0;
        }
        finally
        {
            if (worker != 0)
            {
                PosixHostStubs.CloseWorkerThreadHandle(worker);
            }

            PosixHostStubs.DestroyWorkerEvent(doneEvent);
        }
    }

    [UnmanagedCallersOnly]
    private static nint SignalDone(nint doneEvent)
    {
        _ = PosixHostStubs.SignalWorkerEvent(doneEvent);
        return 0;
    }
}
