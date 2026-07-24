// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Runtime.InteropServices;
using SharpEmu.HLE;
using Xunit;

namespace SharpEmu.Libs.Tests.Memory;

/// <summary>
/// The write generation lets GPU caches detect guest CPU rewrites even after
/// another cache owner consumed the (single) dirty flag: the generation is
/// monotonic and survives consume/re-arm cycles and range replacement. These
/// invariants back the presenter's stale-upload detection for CPU-rewritten
/// images (video planes, streamed font atlases).
/// </summary>
public sealed unsafe class GuestImageWriteTrackerTests
{
    // The tracker aligns to the guest's 4 KiB pages; the mprotect underneath
    // operates on host pages, which are 16 KiB on Apple Silicon (the emulator
    // itself always runs with 4 KiB host pages under Rosetta, but this test
    // host may not). Align the allocation to the largest host page size so
    // the kernel's rounding stays inside memory this test owns instead of
    // spilling onto neighbouring heap pages.
    private const nuint TrackedByteCount = 4096;
    private const nuint HostPageAlignment = 16384;

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct MemoryBasicInformation
    {
        public readonly nint BaseAddress;
        public readonly nint AllocationBase;
        public readonly uint AllocationProtect;
        public readonly ushort PartitionId;
        public readonly nuint RegionSize;
        public readonly uint State;
        public readonly uint Protect;
        public readonly uint Type;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nuint VirtualQuery(
        nint address,
        out MemoryBasicInformation information,
        nuint length);

    private static ulong AllocateTrackedPages(out void* allocation)
    {
        allocation = (void*)GuestImageWriteTracker.AllocateProtectionPages(
            2 * HostPageAlignment);
        return (ulong)allocation;
    }

    private static void FreeTrackedPages(void* allocation) =>
        GuestImageWriteTracker.FreeProtectionPages((nint)allocation);

    [Fact]
    public void ProtectionPagesUseDedicatedWindowsReservation()
    {
        if (!GuestImageWriteTracker.Enabled)
        {
            return;
        }

        var address = AllocateTrackedPages(out var allocation);
        Assert.NotEqual(0UL, address);
        try
        {
            if (OperatingSystem.IsWindows())
            {
                Assert.NotEqual(
                    0u,
                    VirtualQuery(
                        (nint)allocation,
                        out var information,
                        (nuint)Marshal.SizeOf<MemoryBasicInformation>()));
                Assert.Equal((nint)allocation, information.AllocationBase);
            }

            GuestImageWriteTracker.Track(address, TrackedByteCount);
            Assert.True(GuestImageWriteTracker.TryHandleWriteFault(address));
            Assert.True(GuestImageWriteTracker.ConsumeDirty(address));
        }
        finally
        {
            GuestImageWriteTracker.Untrack(address);
            FreeTrackedPages(allocation);
        }
    }

    [Fact]
    public void WindowsBackendIsEnabledByDefault()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        Assert.True(GuestImageWriteTracker.Enabled);
    }

    [Fact]
    public void GenerationSurvivesDirtyConsume()
    {
        if (!GuestImageWriteTracker.Enabled)
        {
            return;
        }

        var address = AllocateTrackedPages(out var allocation);
        try
        {
            GuestImageWriteTracker.Track(address, TrackedByteCount);
            Assert.True(GuestImageWriteTracker.TryGetWriteGeneration(address, out var generation));
            Assert.Equal(0, generation);

            Assert.True(GuestImageWriteTracker.TryHandleWriteFault(address));
            Assert.True(GuestImageWriteTracker.ConsumeDirty(address));

            // Consuming the dirty flag must not roll back the generation:
            // that is exactly what lets a second cache owner still observe
            // the rewrite after the first owner consumed the flag.
            Assert.True(GuestImageWriteTracker.TryGetWriteGeneration(address, out generation));
            Assert.Equal(1, generation);
        }
        finally
        {
            GuestImageWriteTracker.Untrack(address);
            FreeTrackedPages(allocation);
        }
    }

    [Fact]
    public void GenerationIncrementsOncePerArmedLifetime()
    {
        if (!GuestImageWriteTracker.Enabled)
        {
            return;
        }

        var address = AllocateTrackedPages(out var allocation);
        try
        {
            GuestImageWriteTracker.Track(address, TrackedByteCount);
            Assert.True(GuestImageWriteTracker.TryHandleWriteFault(address));
            // The first fault disarmed the range; later writes are free-running
            // and must not inflate the generation until the owner re-arms.
            Assert.True(GuestImageWriteTracker.TryHandleWriteFault(address));
            Assert.True(GuestImageWriteTracker.TryGetWriteGeneration(address, out var generation));
            Assert.Equal(1, generation);

            GuestImageWriteTracker.Rearm(address);
            Assert.True(GuestImageWriteTracker.TryHandleWriteFault(address));
            Assert.True(GuestImageWriteTracker.TryGetWriteGeneration(address, out generation));
            Assert.Equal(2, generation);
        }
        finally
        {
            GuestImageWriteTracker.Untrack(address);
            FreeTrackedPages(allocation);
        }
    }

    [Fact]
    public void GenerationCarriesAcrossRangeReplacement()
    {
        if (!GuestImageWriteTracker.Enabled)
        {
            return;
        }

        var address = AllocateTrackedPages(out var allocation);
        try
        {
            GuestImageWriteTracker.Track(address, TrackedByteCount);
            Assert.True(GuestImageWriteTracker.TryHandleWriteFault(address));

            // Re-registering the same allocation with a different size retires
            // the range object (the signal handler may still see the old
            // snapshot) but must carry the generation, otherwise a resize
            // would hide the rewrite from cache owners.
            GuestImageWriteTracker.Track(address, 2 * TrackedByteCount);
            Assert.True(GuestImageWriteTracker.TryGetWriteGeneration(address, out var generation));
            Assert.Equal(1, generation);
        }
        finally
        {
            GuestImageWriteTracker.Untrack(address);
            FreeTrackedPages(allocation);
        }
    }

    [Fact]
    public void UntrackedAddressHasNoGeneration()
    {
        if (!GuestImageWriteTracker.Enabled)
        {
            return;
        }

        Assert.False(GuestImageWriteTracker.TryGetWriteGeneration(0xDEAD_0000_0000UL, out _));
    }
}
