// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using Xunit;

namespace SharpEmu.Libs.Tests;

[Collection("JsonState")]
public sealed class JsonInitializerTerminateTests
{
    private const string InitializerTerminateNid = "PR5k1penBLM";
    private const string InitializerTerminateName =
        "_ZN3sce4Json11Initializer9terminateEv";

    [Fact]
    public void ExportMetadataMatchesTheGen4AndGen5VoidInstanceAbi()
    {
        foreach (var generation in new[] { Generation.Gen4, Generation.Gen5 })
        {
            var manager = CreateManager(generation);

            Assert.True(
                manager.TryGetExport(InitializerTerminateNid, out var export));
            Assert.Equal(InitializerTerminateName, export.Name);
            Assert.Equal("libSceJson", export.LibraryName);
            Assert.Equal(Generation.Gen4 | Generation.Gen5, export.Target);
        }
    }

    [Theory]
    [InlineData(0UL)]
    [InlineData(0x1_0000_1234UL)]
    public void DispatchIsSideEffectFreeForTheOpaqueInstancePointer(
        ulong initializerAddress)
    {
        var manager = CreateManager(Generation.Gen5);
        var context = new CpuContext(
            new FakeCpuMemory(0x2000, 0x1000),
            Generation.Gen5);
        context[CpuRegister.Rdi] = initializerAddress;
        context[CpuRegister.Rax] = ulong.MaxValue;

        Assert.True(
            manager.TryDispatch(
                InitializerTerminateNid,
                context,
                out var result));
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
