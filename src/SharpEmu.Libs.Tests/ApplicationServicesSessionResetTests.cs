// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using System.Text.Json;
using SharpEmu.HLE;
using SharpEmu.Libs.Application;
using SharpEmu.Libs.GameUpdate;
using SharpEmu.Libs.Json;
using SharpEmu.Libs.PlayGo;
using Xunit;

namespace SharpEmu.Libs.Tests;

[CollectionDefinition(ApplicationServicesSessionStateCollection.Name, DisableParallelization = true)]
public sealed class ApplicationServicesSessionStateCollection
{
    public const string Name = "Application services session state";
}

[Collection(ApplicationServicesSessionStateCollection.Name)]
public sealed class ApplicationServicesSessionResetTests
{
    private const ulong InitParametersAddress = 0x1000;
    private const ulong JsonValueAddress = 0x3000;
    private const ulong JsonInitializerAddress = 0x4000;
    private const ulong JsonCallback = 0x5000;
    private const ulong JsonCallbackContext = 0x6000;

    [Fact]
    public void ResetRuntimeStateClearsPlayGoJsonAndGameUpdateGuestState()
    {
        ApplicationServicesLifecycle.ResetRuntimeState();
        try
        {
            var firstContext = CreateContext();
            Assert.Equal(0, PlayGoExports.PlayGoInitialize(firstContext));
            Assert.Equal(0, GameUpdateExports.GameUpdateInitialize(firstContext));
            firstContext[CpuRegister.Rdi] = InitParametersAddress;
            Assert.Equal(1, GameUpdateExports.GameUpdateCreateRequest(firstContext));

            firstContext[CpuRegister.Rdi] = JsonValueAddress;
            firstContext[CpuRegister.Rsi] = 1;
            Assert.Equal(0, JsonValueExports.ValueSetBoolean(firstContext));
            firstContext[CpuRegister.Rdi] = JsonInitializerAddress;
            firstContext[CpuRegister.Rsi] = JsonCallback;
            firstContext[CpuRegister.Rdx] = JsonCallbackContext;
            Assert.Equal(0, JsonExports.InitializerSetGlobalNullAccessCallback(firstContext));
            Assert.Equal(JsonValueKind.True, JsonExports.GetValueForTests(JsonValueAddress).ValueKind);
            Assert.Equal(JsonCallback, JsonExports.GlobalNullAccessCallbackForTests);

            ApplicationServicesLifecycle.ResetRuntimeState();

            var nextContext = CreateContext();
            Assert.Equal(0, PlayGoExports.PlayGoInitialize(nextContext));
            nextContext[CpuRegister.Rdi] = InitParametersAddress;
            Assert.Equal(
                (int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT,
                GameUpdateExports.GameUpdateCreateRequest(nextContext));
            Assert.Equal(0, GameUpdateExports.GameUpdateInitialize(nextContext));
            Assert.Equal(1, GameUpdateExports.GameUpdateCreateRequest(nextContext));
            Assert.Equal(JsonValueKind.Null, JsonExports.GetValueForTests(JsonValueAddress).ValueKind);
            Assert.Equal(0UL, JsonExports.GlobalNullAccessCallbackForTests);
            Assert.Equal(0UL, JsonExports.GlobalNullAccessCallbackContextForTests);
        }
        finally
        {
            ApplicationServicesLifecycle.ResetRuntimeState();
        }
    }

    private static CpuContext CreateContext()
    {
        var memory = new FakeGuestMemory();
        var parameters = new byte[16];
        BinaryPrimitives.WriteUInt64LittleEndian(parameters, 0x1000_0000);
        BinaryPrimitives.WriteUInt32LittleEndian(parameters.AsSpan(8), 0x20_0000);
        memory.AddRegion(InitParametersAddress, parameters);
        memory.AddRegion(JsonValueAddress, new byte[0x20]);
        var context = new CpuContext(memory, Generation.Gen5);
        context[CpuRegister.Rdi] = InitParametersAddress;
        return context;
    }
}
