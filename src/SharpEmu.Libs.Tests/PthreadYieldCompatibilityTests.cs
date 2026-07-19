// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using SharpEmu.Libs.Kernel;
using Xunit;

namespace SharpEmu.Libs.Tests;

public sealed class PthreadYieldCompatibilityTests
{
    [Fact]
    public void PosixAliasSharesTheCanonicalYieldImplementation()
    {
        var memory = new FakeCpuMemory(0x1_0000_0000, 0x1000);
        var context = new CpuContext(memory, Generation.Gen5);

        Assert.Equal(
            (int)OrbisGen2Result.ORBIS_GEN2_OK,
            KernelPthreadCompatExports.PosixPthreadYield(context));
    }

    [Fact]
    public void PosixAliasIsPublishedWithTheObservedKernelNid()
    {
        ExportMetadataAssert.Exact(
            "B5GmVDKwpn0",
            "pthread_yield",
            "libKernel",
            Generation.Gen4 | Generation.Gen5);
    }
}
