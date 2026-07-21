// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using SharpEmu.Libs.CxxAbi;
using Xunit;

namespace SharpEmu.Libs.Tests;

[CollectionDefinition(CxxAbiSessionStateCollection.Name, DisableParallelization = true)]
public sealed class CxxAbiSessionStateCollection
{
    public const string Name = "C++ ABI session state";
}

[Collection(CxxAbiSessionStateCollection.Name)]
public sealed class CxxAbiSessionResetTests
{
    private const ulong GuardAddress = 0x1000;

    [Fact]
    public void ResetRuntimeStateReleasesAbandonedInitializerGuard()
    {
        CxxAbiLifecycle.ResetRuntimeState();
        try
        {
            var firstContext = CreateContext();
            Assert.Equal(0, CxaGuardExports.CxaGuardAcquire(firstContext));
            Assert.Equal(1UL, firstContext[CpuRegister.Rax]);

            CxxAbiLifecycle.ResetRuntimeState();

            var nextContext = CreateContext();
            Assert.Equal(0, CxaGuardExports.CxaGuardAcquire(nextContext));
            Assert.Equal(1UL, nextContext[CpuRegister.Rax]);
        }
        finally
        {
            CxxAbiLifecycle.ResetRuntimeState();
        }
    }

    private static CpuContext CreateContext()
    {
        var memory = new FakeGuestMemory();
        memory.AddRegion(GuardAddress, new byte[sizeof(ulong)]);
        var context = new CpuContext(memory, Generation.Gen5);
        context[CpuRegister.Rdi] = GuardAddress;
        return context;
    }
}
