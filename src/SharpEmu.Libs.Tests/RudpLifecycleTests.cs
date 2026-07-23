// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using SharpEmu.Libs.Network;
using SharpEmu.Libs.Rudp;
using Xunit;

namespace SharpEmu.Libs.Tests;

public sealed class RudpLifecycleTests : IDisposable
{
    private const ulong BaseAddress = 0x4_0000_0000;
    private const int NotInitialized = unchecked((int)0x80770001);
    private const int AlreadyInitialized = unchecked((int)0x80770002);
    private const int InvalidArgument = unchecked((int)0x80770004);
    private const int OutOfMemory = unchecked((int)0x80770007);
    private const int InternalIoThreadAlreadyEnabled = unchecked((int)0x80770010);
    private const int InvalidEventHandler = unchecked((int)0x80770022);

    public RudpLifecycleTests() => RudpLifecycle.ResetRuntimeState();

    public void Dispose() => RudpLifecycle.ResetRuntimeState();

    [Fact]
    public void ExportsRegisterWithFirmwareGenerationsAndIdentity()
    {
        var gen4 = CreateManager(Generation.Gen4);
        var gen5 = CreateManager(Generation.Gen5);

        AssertExport(gen4, "amuBfI-AQc4", "sceRudpInit");
        AssertExport(gen5, "amuBfI-AQc4", "sceRudpInit");
        Assert.False(gen4.TryGetExport("SUEVes8gvmw", out _));
        Assert.False(gen4.TryGetExport("6PBNpsgyaxw", out _));
        Assert.False(gen4.TryGetExport("3hBvwqEwqj8", out _));
        AssertExport(gen5, "SUEVes8gvmw", "sceRudpSetEventHandler");
        AssertExport(gen5, "6PBNpsgyaxw", "sceRudpEnableInternalIOThread");
        AssertExport(gen5, "3hBvwqEwqj8", "sceRudpEnd");
    }

    [Fact]
    public void InitValidatesAndRetainsCallerOwnedStorageTransactionally()
    {
        var context = CreateContext();

        Assert.Equal(InvalidArgument, RudpExports.Init(context));
        context[CpuRegister.Rdi] = BaseAddress;
        context[CpuRegister.Rsi] = 0x3CF;
        Assert.Equal(OutOfMemory, RudpExports.Init(context));
        Assert.Equal((false, 0UL, 0), RudpExports.GetStateForTests());

        context[CpuRegister.Rsi] = 0x1000;
        Assert.Equal(0, RudpExports.Init(context));
        Assert.Equal(
            (true, BaseAddress, 0x1000),
            RudpExports.GetStateForTests());

        context[CpuRegister.Rdi] = 0;
        context[CpuRegister.Rsi] = 0;
        Assert.Equal(AlreadyInitialized, RudpExports.Init(context));
        Assert.Equal(
            (true, BaseAddress, 0x1000),
            RudpExports.GetStateForTests());
    }

    [Fact]
    public void InitRejectsUnreadableRangeWithoutPublishingState()
    {
        var context = CreateContext();
        context[CpuRegister.Rdi] = BaseAddress + 0x3E00;
        context[CpuRegister.Rsi] = 0x400;

        Assert.Equal(OutOfMemory, RudpExports.Init(context));
        Assert.Equal((false, 0UL, 0), RudpExports.GetStateForTests());
    }

    [Fact]
    public void HandlerAndInternalWorkerFollowInitializedLifecycle()
    {
        var context = CreateContext();
        context[CpuRegister.Rdi] = 0;
        Assert.Equal(NotInitialized, RudpExports.SetEventHandler(context));
        Assert.Equal(NotInitialized, RudpExports.EnableInternalIoThread(context));

        Initialize(context);
        context[CpuRegister.Rdi] = 0;
        Assert.Equal(InvalidEventHandler, RudpExports.SetEventHandler(context));
        context[CpuRegister.Rdi] = 0x1234_5678;
        context[CpuRegister.Rsi] = 0x8765_4321;
        Assert.Equal(0, RudpExports.SetEventHandler(context));
        Assert.Equal(
            (0x1234_5678UL, 0x8765_4321UL),
            RudpExports.GetEventHandlerStateForTests());

        context[CpuRegister.Rdi] = 1;
        context[CpuRegister.Rsi] = unchecked((ulong)-7L);
        Assert.Equal(0, RudpExports.EnableInternalIoThread(context));
        Assert.Equal(
            (true, 0x4000U, -7),
            RudpExports.GetInternalIoThreadStateForTests());
        Assert.Equal(
            InternalIoThreadAlreadyEnabled,
            RudpExports.EnableInternalIoThread(context));
    }

    [Fact]
    public void EndAndProcessResetClearAllRetainedOwnership()
    {
        var context = CreateContext();
        Assert.Equal(NotInitialized, RudpExports.End(context));
        InitializeWithHandlerAndWorker(context);

        Assert.Equal(0, RudpExports.End(context));
        AssertCleared();
        InitializeWithHandlerAndWorker(context);

        NetworkLifecycle.ResetRuntimeState();

        AssertCleared();
        Initialize(context);
    }

    [Fact]
    public void DispatchZeroExtendsFirmwareStatusIntoRax()
    {
        var manager = CreateManager(Generation.Gen5);
        var context = CreateContext();

        Assert.True(manager.TryDispatch("3hBvwqEwqj8", context, out _));
        Assert.Equal(0x0000_0000_8077_0001UL, context[CpuRegister.Rax]);

        context[CpuRegister.Rdi] = BaseAddress;
        context[CpuRegister.Rsi] = 0x1000;
        Assert.True(manager.TryDispatch("amuBfI-AQc4", context, out _));
        Assert.Equal(0UL, context[CpuRegister.Rax]);
    }

    private static void Initialize(CpuContext context)
    {
        context[CpuRegister.Rdi] = BaseAddress;
        context[CpuRegister.Rsi] = 0x1000;
        Assert.Equal(0, RudpExports.Init(context));
    }

    private static void InitializeWithHandlerAndWorker(CpuContext context)
    {
        Initialize(context);
        context[CpuRegister.Rdi] = 0x1234_5678;
        context[CpuRegister.Rsi] = 0x8765_4321;
        Assert.Equal(0, RudpExports.SetEventHandler(context));
        context[CpuRegister.Rdi] = 0x8000;
        context[CpuRegister.Rsi] = 4;
        Assert.Equal(0, RudpExports.EnableInternalIoThread(context));
    }

    private static void AssertCleared()
    {
        Assert.Equal((false, 0UL, 0), RudpExports.GetStateForTests());
        Assert.Equal((0UL, 0UL), RudpExports.GetEventHandlerStateForTests());
        Assert.Equal(
            (false, 0U, 0),
            RudpExports.GetInternalIoThreadStateForTests());
    }

    private static ModuleManager CreateManager(Generation generation)
    {
        var manager = new ModuleManager();
        manager.RegisterExports(
            SharpEmu.Generated.SysAbiExportRegistry.CreateExports(generation));
        return manager;
    }

    private static void AssertExport(
        ModuleManager manager,
        string nid,
        string name)
    {
        Assert.True(manager.TryGetExport(nid, out var export));
        Assert.Equal(name, export.Name);
        Assert.Equal("libSceRudp", export.LibraryName);
    }

    private static CpuContext CreateContext() =>
        new(new FakeCpuMemory(BaseAddress, 0x4000), Generation.Gen5);
}
