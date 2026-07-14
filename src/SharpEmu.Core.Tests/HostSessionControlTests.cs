// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using Xunit;

namespace SharpEmu.Core.Tests;

public sealed class HostSessionControlTests
{
    [Fact]
    public void RegisteredHandlerReceivesShutdownReasonUntilDisposed()
    {
        string? receivedReason = null;
        var registration = HostSessionControl.RegisterShutdownHandler(reason => receivedReason = reason);

        HostSessionControl.RequestShutdown("window closed");

        Assert.Equal("window closed", receivedReason);

        registration.Dispose();
        receivedReason = null;
        HostSessionControl.RequestShutdown("after disposal");
        Assert.Null(receivedReason);
    }

    [Fact]
    public void DisposingStaleRegistrationDoesNotClearReplacement()
    {
        var firstCalls = 0;
        var secondCalls = 0;
        using var first = HostSessionControl.RegisterShutdownHandler(_ => firstCalls++);
        using var second = HostSessionControl.RegisterShutdownHandler(_ => secondCalls++);

        first.Dispose();
        HostSessionControl.RequestShutdown("new session");

        Assert.Equal(0, firstCalls);
        Assert.Equal(1, secondCalls);
    }

    [Fact]
    public void ThrowingHandlerDoesNotEscapeRequestBoundary()
    {
        using var registration = HostSessionControl.RegisterShutdownHandler(
            _ => throw new InvalidOperationException("test failure"));

        var exception = Record.Exception(() => HostSessionControl.RequestShutdown("window closed"));

        Assert.Null(exception);
    }

    [Fact]
    public void NullHandlerIsRejectedWithoutReplacingCurrentRegistration()
    {
        var calls = 0;
        using var registration = HostSessionControl.RegisterShutdownHandler(_ => calls++);

        Assert.Throws<ArgumentNullException>(() => HostSessionControl.RegisterShutdownHandler(null!));
        HostSessionControl.RequestShutdown("still registered");

        Assert.Equal(1, calls);
    }
}
