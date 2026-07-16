// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Runtime.InteropServices;
using SharpEmu.HLE;
using Xunit;

namespace SharpEmu.Libs.Tests;

public sealed unsafe class GuestImageWriteTrackerTests
{
    [Theory]
    [InlineData(0x1000UL, 0x1000UL, 0x1000UL, 0x1000UL)]
    [InlineData(0x1234UL, 1UL, 0x1000UL, 0x1000UL)]
    [InlineData(0x1FFFUL, 2UL, 0x1000UL, 0x2000UL)]
    public void PageAlignmentCoversTheCompleteRequestedRange(
        ulong address,
        ulong byteCount,
        ulong expectedStart,
        ulong expectedLength)
    {
        Assert.True(GuestImageWriteTracker.TryPageAlign(
            address,
            byteCount,
            out var start,
            out var length));
        Assert.Equal(expectedStart, start);
        Assert.Equal(expectedLength, length);
    }

    [Theory]
    [InlineData(0x1000UL, 0UL)]
    [InlineData(ulong.MaxValue - 7, 16UL)]
    [InlineData(ulong.MaxValue - 0x7FF, 1UL)]
    public void PageAlignmentRejectsEmptyOrOverflowingRanges(
        ulong address,
        ulong byteCount)
    {
        Assert.False(GuestImageWriteTracker.TryPageAlign(
            address,
            byteCount,
            out var start,
            out var length));
        Assert.Equal(0UL, start);
        Assert.Equal(0UL, length);
    }

    [Fact]
    public void FaultAndManagedWritePathsPublishConsumableDirtyState()
    {
        if (!GuestImageWriteTracker.Enabled)
        {
            return;
        }

        const nuint pageSize = 0x1000;
        var memory = NativeMemory.AlignedAlloc(pageSize, pageSize);
        Assert.NotEqual((nint)0, (nint)memory);
        var address = (ulong)memory + 32;
        try
        {
            GuestImageWriteTracker.Track(address, 64);
            Assert.False(GuestImageWriteTracker.PeekDirty(address));

            Assert.True(GuestImageWriteTracker.TryHandleWriteFault(address + 8));
            Assert.True(GuestImageWriteTracker.PeekDirty(address));
            Assert.True(GuestImageWriteTracker.ConsumeDirty(address));
            Assert.False(GuestImageWriteTracker.ConsumeDirty(address));

            GuestImageWriteTracker.Rearm(address);
            GuestImageWriteTracker.NotifyManagedWrite(address + 16, 4);
            Assert.True(GuestImageWriteTracker.ConsumeDirty(address));
        }
        finally
        {
            GuestImageWriteTracker.Untrack(address);
            NativeMemory.AlignedFree(memory);
        }
    }

    [Fact]
    public void FaultDirtiesEveryOwnerSharingTheSameHostPage()
    {
        if (!GuestImageWriteTracker.Enabled)
        {
            return;
        }

        const nuint pageSize = 0x1000;
        var memory = NativeMemory.AlignedAlloc(pageSize, pageSize);
        Assert.NotEqual((nint)0, (nint)memory);
        var first = (ulong)memory + 16;
        var second = (ulong)memory + 0x800;
        try
        {
            GuestImageWriteTracker.Track(first, 32);
            GuestImageWriteTracker.Track(second, 32);

            Assert.True(GuestImageWriteTracker.TryHandleWriteFault(first));
            Assert.True(GuestImageWriteTracker.ConsumeDirty(first));
            Assert.True(GuestImageWriteTracker.ConsumeDirty(second));
        }
        finally
        {
            GuestImageWriteTracker.Untrack(first);
            GuestImageWriteTracker.Untrack(second);
            NativeMemory.AlignedFree(memory);
        }
    }
}
