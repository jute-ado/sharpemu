// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using Xunit;

namespace SharpEmu.Libs.Tests;

public sealed class KernelSignalReturnTests
{
    [Fact]
    public void SignalReturnPredicateIsFalseWithoutInstalledGuestTrampoline()
    {
        var export = Assert.Single(
            SharpEmu.Generated.SysAbiExportRegistry.CreateExports(Generation.Gen5),
            candidate => candidate.Nid == "crb5j7mkk1c");
        var memory = new FakeGuestMemory();
        memory.AddRegion(0x1000, [0x90, 0xC3]);
        var context = new CpuContext(memory, Generation.Gen5);

        context[CpuRegister.Rdi] = 0x1000;
        Assert.Equal(0, export.Function(context));
        context[CpuRegister.Rdi] = 0xDEAD_BEEF;
        Assert.Equal(0, export.Function(context));
    }
}
