// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using SharpEmu.Libs.AppContent;
using Xunit;

namespace SharpEmu.Libs.Tests;

public sealed class AppContentInitializationSafetyTests
{
    private const ulong InitParamAddress = 0x1000;
    private const ulong BootParamAddress = 0x2000;
    private const byte Canary = 0xA5;

    [Fact]
    public void InitializeClearsBootParameterAttributes()
    {
        var bootParameter = Enumerable.Repeat(Canary, 12).ToArray();
        var memory = new FakeGuestMemory();
        memory.AddRegion(BootParamAddress, bootParameter);
        var context = CreateContext(memory, BootParamAddress);

        Assert.Equal(
            (int)OrbisGen2Result.ORBIS_GEN2_OK,
            AppContentExports.AppContentInitialize(context));
        Assert.Equal(
            [Canary, Canary, Canary, Canary, 0, 0, 0, 0, Canary, Canary, Canary, Canary],
            bootParameter);
    }

    [Fact]
    public void InitializeRejectsWrappedBootParameterFieldWithoutWriting()
    {
        var wrappedDestination = Enumerable.Repeat(Canary, sizeof(uint)).ToArray();
        var memory = new FakeGuestMemory();
        memory.AddRegion(0, wrappedDestination);
        var context = CreateContext(memory, ulong.MaxValue - 3);

        Assert.Equal(
            (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT,
            AppContentExports.AppContentInitialize(context));
        Assert.All(wrappedDestination, value => Assert.Equal(Canary, value));
    }

    private static CpuContext CreateContext(FakeGuestMemory memory, ulong bootParamAddress)
    {
        var context = new CpuContext(memory, Generation.Gen5);
        context[CpuRegister.Rdi] = InitParamAddress;
        context[CpuRegister.Rsi] = bootParamAddress;
        return context;
    }
}
