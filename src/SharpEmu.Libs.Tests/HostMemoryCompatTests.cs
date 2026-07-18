// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using SharpEmu.HLE.Host;
using SharpEmu.Libs.Kernel;
using Xunit;

namespace SharpEmu.Libs.Tests;

public sealed class HostMemoryCompatTests
{
    [Fact]
    public void TrackedLibcHeapRemainsAccessibleThroughCompatFallback()
    {
        var context = new CpuContext(new FakeGuestMemory(), Generation.Gen5);
        context[CpuRegister.Rdi] = 16;

        Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_OK, KernelMemoryCompatExports.Malloc(context));
        var address = context[CpuRegister.Rax];
        Assert.NotEqual(0UL, address);

        try
        {
            context[CpuRegister.Rdi] = address;
            context[CpuRegister.Rsi] = 0xA5;
            context[CpuRegister.Rdx] = 16;

            Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_OK, KernelMemoryCompatExports.Memset(context));

            var bytes = new byte[16];
            Assert.True(KernelMemoryCompatExports.TryReadTrackedLibcHeap(address, bytes));
            Assert.All(bytes, value => Assert.Equal(0xA5, value));
        }
        finally
        {
            context[CpuRegister.Rdi] = address;
            _ = KernelMemoryCompatExports.Free(context);
        }
    }

    [Fact]
    public void UntrackedHostPointerIsRejected()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var context = new CpuContext(new FakeGuestMemory(), Generation.Gen5);
        context[CpuRegister.Rdi] = 0x0000_0000_0042_0000;
        context[CpuRegister.Rsi] = 0xA5;
        context[CpuRegister.Rdx] = 0x41;

        Assert.Equal(
            (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT,
            KernelMemoryCompatExports.Memset(context));
    }

    [Fact]
    public void HostRangeWalkRejectsInaccessibleMiddleRegion()
    {
        var memory = new RegionHostMemory(
            new HostRegionInfo(
                0x1000,
                0x1000,
                0x1000,
                HostRegionState.Committed,
                0,
                HostPageProtection.ReadWrite,
                0,
                0),
            new HostRegionInfo(
                0x2000,
                0x1000,
                0x1000,
                HostRegionState.Committed,
                0,
                HostPageProtection.NoAccess,
                0,
                0));

        Assert.False(KernelMemoryCompatExports.IsHostRangeAccessible(
            memory,
            0x1800,
            0x1000,
            writeAccess: false));
    }

    [Fact]
    public void HostRangeWalkAcceptsEveryReadableRegion()
    {
        var memory = new RegionHostMemory(
            new HostRegionInfo(
                0x1000,
                0x1000,
                0x1000,
                HostRegionState.Committed,
                0,
                HostPageProtection.ReadOnly,
                0,
                0),
            new HostRegionInfo(
                0x2000,
                0x1000,
                0x1000,
                HostRegionState.Committed,
                0,
                HostPageProtection.ReadWrite,
                0,
                0));

        Assert.True(KernelMemoryCompatExports.IsHostRangeAccessible(
            memory,
            0x1800,
            0x1000,
            writeAccess: false));
        Assert.False(KernelMemoryCompatExports.IsHostRangeAccessible(
            memory,
            0x1800,
            0x1000,
            writeAccess: true));
    }

    private sealed class RegionHostMemory(params HostRegionInfo[] regions) :
        IHostMemory
    {
        public ulong Allocate(
            ulong desiredAddress,
            ulong size,
            HostPageProtection protection) =>
            throw new NotSupportedException();

        public ulong Reserve(
            ulong desiredAddress,
            ulong size,
            HostPageProtection protection) =>
            throw new NotSupportedException();

        public bool Commit(
            ulong address,
            ulong size,
            HostPageProtection protection) =>
            throw new NotSupportedException();

        public bool Free(ulong address) =>
            throw new NotSupportedException();

        public bool Protect(
            ulong address,
            ulong size,
            HostPageProtection protection,
            out uint rawOldProtection) =>
            throw new NotSupportedException();

        public bool ProtectRaw(
            ulong address,
            ulong size,
            uint rawProtection,
            out uint rawOldProtection) =>
            throw new NotSupportedException();

        public bool Query(ulong address, out HostRegionInfo info)
        {
            foreach (var region in regions)
            {
                if (address >= region.BaseAddress &&
                    address - region.BaseAddress < region.RegionSize)
                {
                    info = region;
                    return true;
                }
            }

            info = default;
            return false;
        }

        public void FlushInstructionCache(ulong address, ulong size) =>
            throw new NotSupportedException();
    }
}
