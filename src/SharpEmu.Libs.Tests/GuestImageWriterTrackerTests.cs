// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.Gpu;
using Xunit;

namespace SharpEmu.Libs.Tests;

public sealed class GuestImageWriterTrackerTests
{
    [Fact]
    public void RenderTargetTrackingDoesNotAllocate()
    {
        const ulong address = 0x1000;
        GuestRenderTarget[] targets =
        [
            new(address, 1, 1, 0, 0),
        ];
        var writers = new Dictionary<ulong, long>
        {
            [address] = 0,
        };

        GuestImageWriterTracker.RecordRenderTargets(targets, writers, 1);
        var allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        GuestImageWriterTracker.RecordRenderTargets(targets, writers, 2);

        Assert.Equal(
            0,
            GC.GetAllocatedBytesForCurrentThread() - allocatedBefore);
        Assert.Equal(2, writers[address]);
    }

    [Fact]
    public void StorageTrackingIgnoresSampledAndNullAddressTextures()
    {
        GuestDrawTexture[] textures =
        [
            new(0x1000, 1, 1, 0, 0, [], false, IsStorage: true),
            new(0x2000, 1, 1, 0, 0, [], false, IsStorage: false),
            new(0, 1, 1, 0, 0, [], false, IsStorage: true),
        ];
        var writers = new Dictionary<ulong, long>();

        GuestImageWriterTracker.RecordStorageTextures(
            textures,
            writers,
            sequence: 7);

        Assert.Equal(7, writers[0x1000]);
        Assert.False(writers.ContainsKey(0x2000));
        Assert.False(writers.ContainsKey(0));
    }
}
