// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
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
    public void UntrackedHostPointerIsRejectedWithoutWindowsVirtualQuery()
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
}
