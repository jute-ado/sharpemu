// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using SharpEmu.Libs.Kernel;
using Xunit;

namespace SharpEmu.Libs.Tests;

public sealed class KernelModulePathSafetyTests : IDisposable
{
    private const ulong PathAddress = 0x1000;

    public KernelModulePathSafetyTests()
    {
        KernelModuleRegistry.Reset();
    }

    [Fact]
    public void LoadStartModuleUsesNullTerminatedGuestPath()
    {
        var memory = new FakeGuestMemory();
        memory.AddRegion(PathAddress, CreateReadChunk("/app0/DreamingSarah.prx"u8));
        var context = CreateContext(memory, PathAddress);

        Assert.Equal(
            (int)OrbisGen2Result.ORBIS_GEN2_OK,
            KernelRuntimeCompatExports.KernelLoadStartModule(context));
        AssertRegisteredName(context, "DreamingSarah.prx");
    }

    [Fact]
    public void LoadStartModuleDoesNotSpliceWrappedPathFromAddressZero()
    {
        var memory = new FakeGuestMemory();
        memory.AddRegion(ulong.MaxValue, [(byte)'X']);
        memory.AddRegion(0, CreateReadChunk("Wrapped.prx"u8));
        var context = CreateContext(memory, ulong.MaxValue);

        Assert.Equal(
            (int)OrbisGen2Result.ORBIS_GEN2_OK,
            KernelRuntimeCompatExports.KernelLoadStartModule(context));
        AssertRegisteredName(context, "module.sprx");
    }

    [Fact]
    public void LoadStartModuleRunsDeferredInitializersOnlyOnce()
    {
        const string registeredPath = @"F:\game\Media\Plugins\plugin.prx";
        var handle = KernelModuleRegistry.RegisterModule(
            registeredPath,
            baseAddress: 0x20000,
            size: 0x20000,
            entryPoint: 0,
            isMain: false);
        KernelModuleRegistry.RegisterModuleInitializers(
            handle,
            [0x21000, 0x22000]);
        var memory = new FakeGuestMemory();
        memory.AddRegion(
            PathAddress,
            CreateReadChunk("/app0/Media/Plugins/plugin.prx"u8));
        var context = CreateContext(memory, PathAddress);
        var scheduler = new RecordingScheduler();
        var previousScheduler = GuestThreadExecution.Scheduler;
        GuestThreadExecution.Scheduler = scheduler;

        try
        {
            Assert.Equal(
                (int)OrbisGen2Result.ORBIS_GEN2_OK,
                KernelRuntimeCompatExports.KernelLoadStartModule(context));
            Assert.Equal(handle, unchecked((int)context[CpuRegister.Rax]));

            context[CpuRegister.Rdi] = PathAddress;
            Assert.Equal(
                (int)OrbisGen2Result.ORBIS_GEN2_OK,
                KernelRuntimeCompatExports.KernelLoadStartModule(context));

            Assert.Equal([0x21000UL, 0x22000UL], scheduler.EntryPoints);
            Assert.True(KernelModuleRegistry.TryGetModuleByHandle(handle, out var module));
            Assert.Equal(
                KernelModuleRegistry.ModuleStartState.Started,
                module.StartState);
        }
        finally
        {
            GuestThreadExecution.Scheduler = previousScheduler;
        }
    }

    [Fact]
    public void LoadStartModuleRetriesInitializationAfterGuestFailure()
    {
        const string registeredPath = @"F:\game\Media\Plugins\plugin.prx";
        var handle = KernelModuleRegistry.RegisterModule(
            registeredPath,
            baseAddress: 0x20000,
            size: 0x20000,
            entryPoint: 0,
            isMain: false);
        KernelModuleRegistry.RegisterModuleInitializers(handle, [0x21000]);
        var memory = new FakeGuestMemory();
        memory.AddRegion(
            PathAddress,
            CreateReadChunk("/app0/Media/Plugins/plugin.prx"u8));
        var context = CreateContext(memory, PathAddress);
        var scheduler = new RecordingScheduler
        {
            FailuresRemaining = 1,
        };
        var previousScheduler = GuestThreadExecution.Scheduler;
        GuestThreadExecution.Scheduler = scheduler;

        try
        {
            Assert.Equal(
                (int)OrbisGen2Result.ORBIS_GEN2_ERROR_CPU_TRAP,
                KernelRuntimeCompatExports.KernelLoadStartModule(context));
            Assert.True(KernelModuleRegistry.TryGetModuleByHandle(handle, out var failed));
            Assert.Equal(
                KernelModuleRegistry.ModuleStartState.NotStarted,
                failed.StartState);

            context[CpuRegister.Rdi] = PathAddress;
            Assert.Equal(
                (int)OrbisGen2Result.ORBIS_GEN2_OK,
                KernelRuntimeCompatExports.KernelLoadStartModule(context));

            Assert.Equal([0x21000UL, 0x21000UL], scheduler.EntryPoints);
            Assert.True(KernelModuleRegistry.TryGetModuleByHandle(handle, out var started));
            Assert.Equal(
                KernelModuleRegistry.ModuleStartState.Started,
                started.StartState);
        }
        finally
        {
            GuestThreadExecution.Scheduler = previousScheduler;
        }
    }

    public void Dispose()
    {
        KernelModuleRegistry.Reset();
        GC.SuppressFinalize(this);
    }

    private static CpuContext CreateContext(FakeGuestMemory memory, ulong pathAddress)
    {
        var context = new CpuContext(memory, Generation.Gen5);
        context[CpuRegister.Rdi] = pathAddress;
        return context;
    }

    private static byte[] CreateReadChunk(ReadOnlySpan<byte> value)
    {
        var chunk = new byte[64];
        value.CopyTo(chunk);
        return chunk;
    }

    private static void AssertRegisteredName(CpuContext context, string expectedName)
    {
        var handle = unchecked((int)context[CpuRegister.Rax]);
        Assert.True(KernelModuleRegistry.TryGetModuleByHandle(handle, out var module));
        Assert.Equal(expectedName, module.Name);
    }

    private sealed class RecordingScheduler : IGuestThreadScheduler
    {
        public bool SupportsGuestContextTransfer => true;

        public int FailuresRemaining { get; set; }

        public List<ulong> EntryPoints { get; } = [];

        public void RegisterGuestThreadContext(ulong threadHandle, CpuContext context)
        {
        }

        public bool TryStartThread(
            CpuContext creatorContext,
            GuestThreadStartRequest request,
            out string? error)
        {
            error = "not used";
            return false;
        }

        public bool TryJoinThread(
            CpuContext callerContext,
            ulong threadHandle,
            out ulong returnValue,
            out string? error)
        {
            returnValue = 0;
            error = "not used";
            return false;
        }

        public void Pump(CpuContext callerContext, string reason)
        {
        }

        public IReadOnlyList<GuestThreadSnapshot> SnapshotThreads() => [];

        public bool TryCallGuestFunction(
            CpuContext callerContext,
            ulong entryPoint,
            ulong arg0,
            ulong arg1,
            ulong arg2,
            ulong stackAddress,
            ulong stackSize,
            string reason,
            out ulong returnValue,
            out string? error)
        {
            EntryPoints.Add(entryPoint);
            returnValue = 0;
            if (FailuresRemaining > 0)
            {
                FailuresRemaining--;
                error = "synthetic guest failure";
                return false;
            }

            error = null;
            return true;
        }

        public bool TryCallGuestContinuation(
            CpuContext callerContext,
            GuestCpuContinuation continuation,
            string reason,
            out string? error)
        {
            error = "not used";
            return false;
        }
    }
}
