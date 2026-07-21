// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.Kernel;
using Xunit;

namespace SharpEmu.Libs.Tests;

public sealed class KernelSemaphoreTraceTests
{
    [Fact]
    public void WaitBlockTraceIncludesGuestIdentityAndCallSite()
    {
        var trace = KernelSemaphoreCompatExports.FormatWaitBlockTrace(
            handle: 0x96,
            name: "Baselib_SystemSemaphore",
            needCount: 1,
            count: 0,
            waitingThreads: 2,
            guestThreadHandle: 0x1234,
            returnRip: 0x801484053);

        Assert.Equal(
            "wait-block handle=0x00000096 name='Baselib_SystemSemaphore' " +
            "need=1 count=0 waiters=2 guest_thread=0x0000000000001234 " +
            "ret=0x0000000801484053",
            trace);
    }

    [Fact]
    public void BlockDescriptionIdentifiesSemaphoreResource()
    {
        Assert.Equal(
            "sceKernelWaitSema(handle=0x00000065 name='Baselib_SystemSemaphore')",
            KernelSemaphoreCompatExports.FormatWaitBlockDescription(
                handle: 0x65,
                name: "Baselib_SystemSemaphore"));
    }
}
