// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Runtime.InteropServices;
using SharpEmu.Core.Cpu.Native;
using SharpEmu.HLE;
using Xunit;

namespace SharpEmu.Core.Tests;

public sealed unsafe class GuestImageWriteFaultRoutingTests
{
    private const uint AccessViolation = 0xC0000005;
    private const nuint PageSize = 16 * 1024;

    [WindowsFact]
    public void OnlyTrackedWriteAccessViolationsAreConsumed()
    {
        var allocation = NativeMemory.AlignedAlloc(PageSize, PageSize);
        Assert.NotEqual((nint)0, (nint)allocation);
        var address = (ulong)allocation;

        try
        {
            GuestImageWriteTracker.Track(address, PageSize);

            Assert.False(DirectExecutionBackend.TryHandleGuestImageWriteFault(
                exceptionCode: AccessViolation,
                accessType: 0,
                faultAddress: address));
            Assert.False(DirectExecutionBackend.TryHandleGuestImageWriteFault(
                exceptionCode: AccessViolation,
                accessType: 8,
                faultAddress: address));
            Assert.False(DirectExecutionBackend.TryHandleGuestImageWriteFault(
                exceptionCode: 0xC000001D,
                accessType: 1,
                faultAddress: address));
            Assert.False(DirectExecutionBackend.TryHandleGuestImageWriteFault(
                exceptionCode: AccessViolation,
                accessType: 1,
                faultAddress: address + PageSize));

            Assert.True(DirectExecutionBackend.TryHandleGuestImageWriteFault(
                exceptionCode: AccessViolation,
                accessType: 1,
                faultAddress: address));
            Assert.True(GuestImageWriteTracker.ConsumeDirty(address));
        }
        finally
        {
            GuestImageWriteTracker.Untrack(address);
            NativeMemory.AlignedFree(allocation);
        }
    }
}
