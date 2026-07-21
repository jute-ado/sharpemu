// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using SharpEmu.Libs.Kernel;
using Xunit;

namespace SharpEmu.Libs.Tests;

[CollectionDefinition(
    KernelSysmoduleSessionStateCollection.Name,
    DisableParallelization = true)]
public sealed class KernelSysmoduleSessionStateCollection
{
    public const string Name = "Kernel sysmodule session state";
}

[Collection(KernelSysmoduleSessionStateCollection.Name)]
public sealed class KernelSysmoduleSessionStateTests
{
    [Fact]
    public void RegistryResetDiscardsLoadedSysmodulesFromPreviousSession()
    {
        const int moduleId = 0x1234;
        var context = new CpuContext(new FakeGuestMemory(), Generation.Gen5);
        KernelModuleRegistry.Reset();
        try
        {
            context[CpuRegister.Rdi] = moduleId;
            Assert.Equal(
                (int)OrbisGen2Result.ORBIS_GEN2_OK,
                KernelRuntimeCompatExports.SysmoduleLoadModule(context));

            Assert.Equal(
                (int)OrbisGen2Result.ORBIS_GEN2_OK,
                KernelRuntimeCompatExports.SysmoduleIsLoaded(context));
            Assert.Equal(0UL, context[CpuRegister.Rax]);

            KernelModuleRegistry.Reset();

            Assert.Equal(
                (int)OrbisGen2Result.ORBIS_GEN2_OK,
                KernelRuntimeCompatExports.SysmoduleIsLoaded(context));
            Assert.Equal(
                unchecked((ulong)(int)OrbisGen2Result.ORBIS_GEN2_ERROR_NOT_FOUND),
                context[CpuRegister.Rax]);
        }
        finally
        {
            context[CpuRegister.Rdi] = moduleId;
            _ = KernelRuntimeCompatExports.SysmoduleUnloadModule(context);
            KernelModuleRegistry.Reset();
        }
    }
}
