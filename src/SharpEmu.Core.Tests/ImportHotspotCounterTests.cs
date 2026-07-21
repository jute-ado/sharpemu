// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Core.Cpu.Native;
using Xunit;

namespace SharpEmu.Core.Tests;

public sealed class ImportHotspotCounterTests
{
    [Fact]
    public void SnapshotRanksCallsAndStartsANewWindow()
    {
        var counter = new ImportHotspotCounter();

        counter.Record("libKernel:scePthreadMutexLock");
        counter.Record("libc:memcpy");
        counter.Record("libKernel:scePthreadMutexLock");

        Assert.Equal(
            [
                new ImportHotspot("libKernel:scePthreadMutexLock", 2),
                new ImportHotspot("libc:memcpy", 1),
            ],
            counter.TakeTop(8));
        Assert.Empty(counter.TakeTop(8));
    }

    [Fact]
    public void ProfileWindowSeparatesImportsThreadsAndCallSites()
    {
        var profile = new ImportProfileWindow();

        profile.Record("libc:memcpy", 0x12, 0x8001234);
        profile.Record("libc:memcpy", 0x12, 0x8001234);
        profile.Record("libKernel:sceKernelWaitSema", 0x34, 0x8005678);

        var snapshot = profile.TakeTop(4);

        Assert.Equal(new ImportHotspot("libc:memcpy", 2), snapshot.Imports[0]);
        Assert.Equal(new ImportHotspot("0x0000000000000012", 2), snapshot.Threads[0]);
        Assert.Equal(
            new ImportHotspot("libc:memcpy@0x0000000008001234", 2),
            snapshot.CallSites[0]);
        Assert.Empty(profile.TakeTop(4).Imports);
    }

    [Fact]
    public void ProfileBoundaryTriggersWhenBlockAllocatedIndexesSkipExactMultiple()
    {
        var boundary = new ImportProfileBoundary(1_000_000);

        Assert.False(boundary.ShouldTakeSnapshot(999_900));
        Assert.True(boundary.ShouldTakeSnapshot(1_000_032));
        Assert.False(boundary.ShouldTakeSnapshot(1_000_040));
        Assert.True(boundary.ShouldTakeSnapshot(2_100_000));
    }
}
