// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using Xunit;

namespace SharpEmu.Libs.Tests.Agc;

public sealed class AgcDriverStatusTests
{
    private const string CaptureInProgressNid = "Ddwk4gLT5j0";

    [Fact]
    public void CaptureStatusIsARegisteredGen5AgcDriverExport()
    {
        var gen4Manager = CreateManager(Generation.Gen4);
        Assert.False(gen4Manager.TryGetExport(CaptureInProgressNid, out _));

        var gen5Manager = CreateManager(Generation.Gen5);
        Assert.True(gen5Manager.TryGetExport(CaptureInProgressNid, out var export));
        Assert.Equal("sceAgcDriverIsCaptureInProgress", export.Name);
        Assert.Equal("libSceAgcDriver", export.LibraryName);
        Assert.Equal(Generation.Gen5, export.Target);
    }

    [Fact]
    public void CaptureStatusReportsInactiveWithoutReadingArguments()
    {
        var manager = CreateManager(Generation.Gen5);
        var context = new CpuContext(new FakeCpuMemory(0x1_0000_0000, 0x1000), Generation.Gen5);
        context[CpuRegister.Rdi] = ulong.MaxValue;
        context[CpuRegister.Rsi] = 0xDEAD_BEEF;
        context[CpuRegister.Rdx] = 0xBAD0_C0DE;

        Assert.True(manager.TryDispatch(CaptureInProgressNid, context, out var result));
        Assert.Equal(OrbisGen2Result.ORBIS_GEN2_OK, result);
        Assert.Equal(0UL, context[CpuRegister.Rax]);
    }

    private static ModuleManager CreateManager(Generation generation)
    {
        var manager = new ModuleManager();
        manager.RegisterExports(
            SharpEmu.Generated.SysAbiExportRegistry.CreateExports(generation));
        return manager;
    }
}
