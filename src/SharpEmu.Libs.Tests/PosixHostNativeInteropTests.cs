// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Runtime.InteropServices;
using SharpEmu.HLE.Host;
using SharpEmu.HLE.Host.Posix;
using Xunit;

namespace SharpEmu.Libs.Tests;

public sealed class PosixHostNativeInteropTests
{
    private delegate nint IdentityCallback(nint value);

    private static readonly IdentityCallback Callback = Identity;

    [Fact]
    public void WorkerEventSignalsOnceAndTimesOutAfterConsumption()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var interop = new PosixHostNativeInterop();
        var workerEvent = interop.CreateWorkerEvent();
        Assert.NotEqual(0, workerEvent);

        try
        {
            Assert.True(interop.SignalWorkerEvent(workerEvent));
            Assert.True(interop.WaitWorkerEvent(workerEvent, 1000));
            Assert.False(interop.WaitWorkerEvent(workerEvent, 0));
        }
        finally
        {
            interop.CloseWorkerEvent(workerEvent);
        }
    }

    [Fact]
    public void GuestAbiCallbackThunkIsCachedAndReadExecute()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var target = Marshal.GetFunctionPointerForDelegate(Callback);
        var interop = new PosixHostNativeInterop();

        var firstThunk = interop.AdaptGuestAbiCallback(target);
        var secondThunk = interop.AdaptGuestAbiCallback(target);

        Assert.NotEqual(0, firstThunk);
        Assert.Equal(firstThunk, secondThunk);
        Assert.True(
            HostPlatform.Current.Memory.Query(unchecked((ulong)firstThunk), out var region));
        Assert.Equal(HostPageProtection.ReadExecute, region.Protection);
    }

    private static nint Identity(nint value) => value;
}
