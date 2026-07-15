// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Core.Cpu;
using SharpEmu.Core.Cpu.Native;
using SharpEmu.Core.Loader;
using SharpEmu.Core.Memory;
using SharpEmu.HLE;
using Xunit;

namespace SharpEmu.Core.Tests;

public sealed class CpuDispatcherTests
{
    [Theory]
    [InlineData(
        OrbisGen2Result.ORBIS_GEN2_ERROR_CPU_TRAP,
        CpuExitReason.CpuTrap)]
    [InlineData(
        OrbisGen2Result.ORBIS_GEN2_ERROR_NOT_IMPLEMENTED,
        CpuExitReason.NativeBackendUnavailable)]
    [InlineData(
        OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT,
        CpuExitReason.UnhandledException)]
    public void NativeBackendFailurePreservesSpecificResult(
        OrbisGen2Result backendResult,
        CpuExitReason expectedReason)
    {
        var backend = new FailingNativeBackend(backendResult);
        using var dispatcher = new CpuDispatcher(
            new VirtualMemory(),
            new ModuleManager(),
            backend);

        var result = dispatcher.DispatchModuleInitializer(
            0x0000_0008_0000_0000,
            Generation.Gen5,
            moduleName: "failing-backend-test");

        Assert.Equal(backendResult, result);
        Assert.Equal(backendResult, dispatcher.LastSessionSummary.Result);
        Assert.Equal(expectedReason, dispatcher.LastSessionSummary.Reason);
        if (backendResult == OrbisGen2Result.ORBIS_GEN2_ERROR_NOT_IMPLEMENTED)
        {
            Assert.Equal("synthetic backend failure", dispatcher.LastNotImplementedInfo?.Detail);
        }
        else
        {
            Assert.Null(dispatcher.LastNotImplementedInfo);
        }
    }

    [Fact]
    public void NativeBackendTrapPreservesStructuredExceptionInfo()
    {
        const ulong entryPoint = 0x0000_0008_0000_0000;
        const ulong trapRip = entryPoint + 0x24;
        var backend = new FailingNativeBackend(
            OrbisGen2Result.ORBIS_GEN2_ERROR_CPU_TRAP,
            new CpuTrapInfo(
                trapRip,
                0xF7,
                exceptionCode: 0xC0000005,
                accessAddress: 0,
                accessKind: CpuMemoryAccessKind.Read));
        using var dispatcher = new CpuDispatcher(
            new VirtualMemory(),
            new ModuleManager(),
            backend);

        var result = dispatcher.DispatchModuleInitializer(
            entryPoint,
            Generation.Gen5,
            moduleName: "structured-trap-test");

        Assert.Equal(OrbisGen2Result.ORBIS_GEN2_ERROR_CPU_TRAP, result);
        Assert.Equal(trapRip, dispatcher.LastTrapInfo?.InstructionPointer);
        Assert.Equal((byte)0xF7, dispatcher.LastTrapInfo?.Opcode);
        Assert.Equal(0xC0000005u, dispatcher.LastTrapInfo?.ExceptionCode);
        Assert.Equal(0uL, dispatcher.LastTrapInfo?.AccessAddress);
        Assert.Equal(CpuMemoryAccessKind.Read, dispatcher.LastTrapInfo?.AccessKind);
        Assert.Equal(trapRip, dispatcher.LastSessionSummary.LastGuestRip);
        Assert.Null(dispatcher.LastNotImplementedInfo);
    }

    [Fact]
    public void RepeatedModuleInitializersReuseDispatcherMemoryRegions()
    {
        var memory = new VirtualMemory();
        var backend = new SuccessfulNativeBackend();
        using var dispatcher = new CpuDispatcher(memory, new ModuleManager(), backend);

        for (var i = 0; i < 40; i++)
        {
            var result = dispatcher.DispatchModuleInitializer(
                0x0000_0008_0000_0000,
                Generation.Gen5,
                moduleName: $"module-{i}");

            Assert.Equal(OrbisGen2Result.ORBIS_GEN2_OK, result);
            Assert.Equal(CpuExitReason.ReturnedToHost, dispatcher.LastSessionSummary.Reason);
        }

        Assert.Equal(40, backend.ExecutionCount);
        Assert.All(
            backend.ReturnContracts,
            contract => Assert.Equal(NativeEntryReturnContract.IgnoreReturnValue, contract));
        Assert.Equal(3, memory.SnapshotRegions().Count);
    }

    [Fact]
    public void RepeatedProcessEntriesReuseDispatcherMemoryRegions()
    {
        var memory = new VirtualMemory();
        var backend = new SuccessfulNativeBackend();
        using var dispatcher = new CpuDispatcher(memory, new ModuleManager(), backend);

        for (var i = 0; i < 20; i++)
        {
            var result = dispatcher.DispatchEntry(
                0x0000_0008_0000_0000,
                Generation.Gen5,
                processImageName: $"eboot-{i}.bin");

            Assert.Equal(OrbisGen2Result.ORBIS_GEN2_OK, result);
            Assert.Equal(CpuExitReason.ReturnedToHost, dispatcher.LastSessionSummary.Reason);
        }

        Assert.Equal(20, backend.ExecutionCount);
        Assert.All(
            backend.ReturnContracts,
            contract => Assert.Equal(NativeEntryReturnContract.RequireZero, contract));
        Assert.Equal(4, memory.SnapshotRegions().Count);
    }

    [Fact]
    public void RepeatedBootstrapEntriesReuseDispatcherMemoryRegions()
    {
        const ulong entryPoint = 0x0000_0008_0000_0000;
        var memory = new VirtualMemory();
        memory.Map(
            entryPoint,
            0x1000,
            fileOffset: 0,
            [
                0x55, 0x48, 0x89, 0xE5, 0x41, 0x57, 0x41, 0x56,
                0x41, 0x55, 0x41, 0x54, 0x53, 0x50, 0x48, 0x89,
            ],
            ProgramHeaderFlags.Read | ProgramHeaderFlags.Execute);
        var backend = new SuccessfulNativeBackend();
        using var dispatcher = new CpuDispatcher(memory, new ModuleManager(), backend);

        for (var i = 0; i < 20; i++)
        {
            Assert.Equal(
                OrbisGen2Result.ORBIS_GEN2_OK,
                dispatcher.DispatchEntry(
                    entryPoint,
                    Generation.Gen5,
                    processImageName: $"bootstrap-{i}.bin"));
        }

        Assert.Equal(20, backend.ExecutionCount);
        Assert.Equal(7, memory.SnapshotRegions().Count);
    }

    [Fact]
    public void MemoryResetInvalidatesReusableDispatcherRegions()
    {
        var memory = new VirtualMemory();
        var backend = new SuccessfulNativeBackend();
        using var dispatcher = new CpuDispatcher(memory, new ModuleManager(), backend);

        Assert.Equal(
            OrbisGen2Result.ORBIS_GEN2_OK,
            dispatcher.DispatchModuleInitializer(
                0x0000_0008_0000_0000,
                Generation.Gen5,
                moduleName: "before-reset"));
        Assert.Equal(3, memory.SnapshotRegions().Count);

        memory.Clear();

        Assert.Equal(
            OrbisGen2Result.ORBIS_GEN2_OK,
            dispatcher.DispatchModuleInitializer(
                0x0000_0008_0000_0000,
                Generation.Gen5,
                moduleName: "after-reset"));
        Assert.Equal(3, memory.SnapshotRegions().Count);
        Assert.Equal(2, backend.ExecutionCount);
    }

    [Fact]
    public void ReusedDispatcherRegionsAreZeroedBetweenEntries()
    {
        var memory = new VirtualMemory();
        var backend = new CleanInfrastructureBackend();
        using var dispatcher = new CpuDispatcher(memory, new ModuleManager(), backend);

        for (var i = 0; i < 2; i++)
        {
            Assert.Equal(
                OrbisGen2Result.ORBIS_GEN2_OK,
                dispatcher.DispatchModuleInitializer(
                    0x0000_0008_0000_0000,
                    Generation.Gen5,
                    moduleName: $"clean-entry-{i}"));
        }

        Assert.Equal(2, backend.ExecutionCount);
    }

    [WindowsX64Fact]
    public void PhysicalMemorySupportsReusableDispatcherRegions()
    {
        using var memory = new PhysicalVirtualMemory();
        var backend = new SuccessfulNativeBackend();
        using var dispatcher = new CpuDispatcher(memory, new ModuleManager(), backend);
        var regionCountAfterFirstEntry = 0;

        for (var i = 0; i < 2; i++)
        {
            var result = dispatcher.DispatchModuleInitializer(
                0x0000_0008_0000_0000,
                Generation.Gen5,
                moduleName: $"physical-entry-{i}");
            Assert.True(
                result == OrbisGen2Result.ORBIS_GEN2_OK,
                $"Dispatch {i} failed with {result}; regions={string.Join(", ", memory.SnapshotRegions().Select(region => $"0x{region.VirtualAddress:X}+0x{region.MemorySize:X}:{region.Protection}"))}.");
            if (i == 0)
            {
                regionCountAfterFirstEntry = memory.SnapshotRegions().Count;
            }
        }

        Assert.Equal(2, backend.ExecutionCount);
        Assert.Equal(regionCountAfterFirstEntry, memory.SnapshotRegions().Count);
    }

    private sealed class FailingNativeBackend(
        OrbisGen2Result result,
        CpuTrapInfo? lastTrapInfo = null) : INativeCpuBackend
    {
        public string BackendName => "synthetic-backend";

        public string? LastError => "synthetic backend failure";

        public CpuTrapInfo? LastTrapInfo => lastTrapInfo;

        public bool TryExecute(
            CpuContext context,
            ulong entryPoint,
            Generation generation,
            IReadOnlyDictionary<ulong, string> importStubs,
            IReadOnlyDictionary<string, ulong> runtimeSymbols,
            CpuExecutionOptions executionOptions,
            NativeEntryReturnContract returnContract,
            out OrbisGen2Result executionResult)
        {
            if (lastTrapInfo is { } trapInfo)
            {
                context.Rip = trapInfo.InstructionPointer;
            }
            executionResult = result;
            return false;
        }
    }

    private sealed class SuccessfulNativeBackend : INativeCpuBackend
    {
        public string BackendName => "synthetic-backend";

        public string? LastError => null;

        public int ExecutionCount { get; private set; }

        public List<NativeEntryReturnContract> ReturnContracts { get; } = [];

        public bool TryExecute(
            CpuContext context,
            ulong entryPoint,
            Generation generation,
            IReadOnlyDictionary<ulong, string> importStubs,
            IReadOnlyDictionary<string, ulong> runtimeSymbols,
            CpuExecutionOptions executionOptions,
            NativeEntryReturnContract returnContract,
            out OrbisGen2Result executionResult)
        {
            ExecutionCount++;
            ReturnContracts.Add(returnContract);
            executionResult = OrbisGen2Result.ORBIS_GEN2_OK;
            return true;
        }
    }

    private sealed class CleanInfrastructureBackend : INativeCpuBackend
    {
        public string BackendName => "clean-infrastructure-backend";

        public string? LastError => null;

        public int ExecutionCount { get; private set; }

        public bool TryExecute(
            CpuContext context,
            ulong entryPoint,
            Generation generation,
            IReadOnlyDictionary<ulong, string> importStubs,
            IReadOnlyDictionary<string, ulong> runtimeSymbols,
            CpuExecutionOptions executionOptions,
            NativeEntryReturnContract returnContract,
            out OrbisGen2Result executionResult)
        {
            var stackProbe = context[CpuRegister.Rsp] - 0x100;
            var tlsProbe = context.FsBase + 0x200;
            Span<byte> values = stackalloc byte[2];
            if (!context.Memory.TryRead(stackProbe, values[..1]) ||
                !context.Memory.TryRead(tlsProbe, values[1..]))
            {
                executionResult = OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT;
                return true;
            }

            if (values[0] != 0 || values[1] != 0)
            {
                executionResult = OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT;
                return true;
            }

            if (!context.Memory.TryWrite(stackProbe, [0xA5]) ||
                !context.Memory.TryWrite(tlsProbe, [0x5A]))
            {
                executionResult = OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT;
                return true;
            }

            ExecutionCount++;
            executionResult = OrbisGen2Result.ORBIS_GEN2_OK;
            return true;
        }
    }
}
