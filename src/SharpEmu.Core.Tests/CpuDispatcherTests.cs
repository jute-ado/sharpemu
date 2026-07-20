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
        CpuExitReason.MemoryFault)]
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
    public void SuccessfulNativeDispatchReportsBackendImportProgress()
    {
        var backend = new SuccessfulNativeBackend(lastSessionImportsHit: 37);
        using var dispatcher = new CpuDispatcher(
            new VirtualMemory(),
            new ModuleManager(),
            backend);

        Assert.Equal(
            OrbisGen2Result.ORBIS_GEN2_OK,
            dispatcher.DispatchModuleInitializer(
                0x0000_0008_0000_0000,
                Generation.Gen5,
                moduleName: "native-import-progress-success"));
        Assert.Equal(37, dispatcher.LastSessionSummary.ImportsHit);
    }

    [Fact]
    public void FailedNativeDispatchReportsProgressBeforeFailure()
    {
        var backend = new FailingNativeBackend(
            OrbisGen2Result.ORBIS_GEN2_ERROR_CPU_TRAP,
            lastSessionImportsHit: 41);
        using var dispatcher = new CpuDispatcher(
            new VirtualMemory(),
            new ModuleManager(),
            backend);

        Assert.Equal(
            OrbisGen2Result.ORBIS_GEN2_ERROR_CPU_TRAP,
            dispatcher.DispatchModuleInitializer(
                0x0000_0008_0000_0000,
                Generation.Gen5,
                moduleName: "native-import-progress-failure"));
        Assert.Equal(41, dispatcher.LastSessionSummary.ImportsHit);
    }

    [Fact]
    public void ProcessEntryReturnCapturesSignedGuestExitCode()
    {
        var backend = new SuccessfulNativeBackend(
            lastEntryReturnValue: unchecked((ulong)-1L));
        using var dispatcher = new CpuDispatcher(
            new VirtualMemory(),
            new ModuleManager(),
            backend);

        Assert.Equal(
            OrbisGen2Result.ORBIS_GEN2_OK,
            dispatcher.DispatchEntry(
                0x0000_0008_0000_0000,
                Generation.Gen5,
                processImageName: "native-exit-code"));
        Assert.Equal(CpuExitReason.Exited, dispatcher.LastSessionSummary.Reason);
        Assert.Equal(-1, dispatcher.LastSessionSummary.ExitCode);
        Assert.Equal(NativeEntryReturnContract.CaptureExitCode, Assert.Single(backend.ReturnContracts));
    }

    [Fact]
    public void InfrastructureMappingFailureReportsExactStage()
    {
        var stackBaseAddress = OperatingSystem.IsWindows()
            ? 0x7FFF_F000_0000UL
            : 0x6FFF_F000_0000UL;
        const ulong stackStride = 0x0100_0000UL;
        var memory = new VirtualMemory();
        for (var index = 0; index < 32; index++)
        {
            memory.Map(
                stackBaseAddress - ((ulong)index * stackStride),
                0x1000,
                fileOffset: 0,
                ReadOnlySpan<byte>.Empty,
                ProgramHeaderFlags.Read | ProgramHeaderFlags.Write);
        }

        using var dispatcher = new CpuDispatcher(
            memory,
            new ModuleManager(),
            new SuccessfulNativeBackend());

        var result = dispatcher.DispatchModuleInitializer(
            0x0000_0008_0000_0000,
            Generation.Gen5,
            moduleName: "mapping-failure-test");

        Assert.Equal(OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT, result);
        Assert.Equal(CpuExitReason.MemoryFault, dispatcher.LastSessionSummary.Reason);
        Assert.Contains("stage=map-stack", dispatcher.LastMilestoneLog);
    }

    [Fact]
    public void TlsFallbackSearchAvoidsReservedStubBands()
    {
        var tlsBaseAddress = OperatingSystem.IsWindows()
            ? 0x7FFE_0000_0000UL
            : 0x6FFE_0000_0000UL;
        var tlsPrefixSize = OperatingSystem.IsWindows()
            ? 0x0000_1000UL
            : 0x0001_0000UL;
        const ulong tlsStride = 0x0100_0000UL;
        var memory = new VirtualMemory();

        // Occupy both directions used by the old 32-candidate search as well
        // as the first 32 candidates in the new upward band. The canonical
        // search must continue upward instead of falling into stub addresses.
        for (var index = 0; index < 32; index++)
        {
            var upwardBase = tlsBaseAddress + ((ulong)index * tlsStride);
            memory.Map(
                upwardBase - tlsPrefixSize,
                0x1000,
                fileOffset: 0,
                ReadOnlySpan<byte>.Empty,
                ProgramHeaderFlags.Read | ProgramHeaderFlags.Write);

            if (index != 0)
            {
                var downwardBase = tlsBaseAddress - ((ulong)index * tlsStride);
                memory.Map(
                    downwardBase - tlsPrefixSize,
                    0x1000,
                    fileOffset: 0,
                    ReadOnlySpan<byte>.Empty,
                    ProgramHeaderFlags.Read | ProgramHeaderFlags.Write);
            }
        }

        using var dispatcher = new CpuDispatcher(
            memory,
            new ModuleManager(),
            new SuccessfulNativeBackend());

        var result = dispatcher.DispatchModuleInitializer(
            0x0000_0008_0000_0000,
            Generation.Gen5,
            moduleName: "tls-fallback-layout-test");

        Assert.Equal(OrbisGen2Result.ORBIS_GEN2_OK, result);
        Assert.Equal(CpuExitReason.ReturnedToHost, dispatcher.LastSessionSummary.Reason);
    }

    [Fact]
    public void SeedsRegisteredTlsTemplateBeforeBackendExecution()
    {
        GuestTlsTemplate.Reset();
        try
        {
            var staticOffset = GuestTlsTemplate.RegisterModule(1, [0xA5, 0x5A], 8, 8);
            var backend = new TlsInspectingNativeBackend(staticOffset);
            using var dispatcher = new CpuDispatcher(
                new VirtualMemory(),
                new ModuleManager(),
                backend);

            var result = dispatcher.DispatchModuleInitializer(
                0x0000_0008_0000_0000,
                Generation.Gen5,
                moduleName: "tls-template-test");

            Assert.Equal(OrbisGen2Result.ORBIS_GEN2_OK, result);
            Assert.Equal(new byte[] { 0xA5, 0x5A }, backend.InitializedBytes);
            Assert.NotEqual(0UL, backend.DtvAddress);
        }
        finally
        {
            GuestTlsTemplate.Reset();
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
            contract => Assert.Equal(NativeEntryReturnContract.CaptureExitCode, contract));
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
    public void MainThreadTlsIsFreshAfterGuestMemoryReset()
    {
        var memory = new VirtualMemory();
        var backend = new TlsPersistenceBackend();
        using var dispatcher = new CpuDispatcher(memory, new ModuleManager(), backend);

        Assert.Equal(
            OrbisGen2Result.ORBIS_GEN2_OK,
            dispatcher.DispatchModuleInitializer(
                0x0000_0008_0000_0000,
                Generation.Gen5,
                moduleName: "before-reset"));

        memory.Clear();

        Assert.Equal(
            OrbisGen2Result.ORBIS_GEN2_OK,
            dispatcher.DispatchModuleInitializer(
                0x0000_0008_0000_0000,
                Generation.Gen5,
                moduleName: "after-reset"));
        Assert.Equal(new byte[] { 0, 0 }, backend.ObservedValues);
    }

    [Fact]
    public void ReusedDispatcherStacksAreZeroedBetweenEntries()
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

    [Fact]
    public void MainThreadTlsPersistsFromInitializerIntoProcessEntry()
    {
        var backend = new TlsPersistenceBackend();
        using var dispatcher = new CpuDispatcher(
            new VirtualMemory(),
            new ModuleManager(),
            backend);

        Assert.Equal(
            OrbisGen2Result.ORBIS_GEN2_OK,
            dispatcher.DispatchModuleInitializer(
                0x0000_0008_0000_0000,
                Generation.Gen5,
                moduleName: "tls-writer"));
        Assert.Equal(
            OrbisGen2Result.ORBIS_GEN2_OK,
            dispatcher.DispatchEntry(
                0x0000_0008_0000_1000,
                Generation.Gen5,
                processImageName: "eboot.bin"));

        Assert.Equal(2, backend.ExecutionCount);
        Assert.Equal(new byte[] { 0, 0xA5 }, backend.ObservedValues);
    }

    [Fact]
    public void PhysicalMemorySupportsReusableDispatcherRegions()
    {
        using var memory = TestHostMemory.CreatePhysicalMemory();
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
        CpuTrapInfo? lastTrapInfo = null,
        int lastSessionImportsHit = 0) : INativeCpuBackend
    {
        public string BackendName => "synthetic-backend";

        public string? LastError => "synthetic backend failure";

        public CpuTrapInfo? LastTrapInfo => lastTrapInfo;

        public int LastSessionImportsHit => lastSessionImportsHit;

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

    private sealed class SuccessfulNativeBackend(
        int lastSessionImportsHit = 0,
        ulong? lastEntryReturnValue = null) : INativeCpuBackend
    {
        public string BackendName => "synthetic-backend";

        public string? LastError => null;

        public int LastSessionImportsHit => lastSessionImportsHit;

        public ulong? LastEntryReturnValue => lastEntryReturnValue;

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
            Span<byte> value = stackalloc byte[1];
            if (!context.Memory.TryRead(stackProbe, value))
            {
                executionResult = OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT;
                return true;
            }

            if (value[0] != 0)
            {
                executionResult = OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT;
                return true;
            }

            if (!context.Memory.TryWrite(stackProbe, [0xA5]))
            {
                executionResult = OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT;
                return true;
            }

            ExecutionCount++;
            executionResult = OrbisGen2Result.ORBIS_GEN2_OK;
            return true;
        }
    }

    private sealed class TlsInspectingNativeBackend(ulong staticOffset) : INativeCpuBackend
    {
        public string BackendName => "tls-inspecting-backend";

        public string? LastError => null;

        public byte[] InitializedBytes { get; private set; } = [];

        public ulong DtvAddress { get; private set; }

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
            var initializedBytes = new byte[2];
            var readTemplate = context.Memory.TryRead(
                context.FsBase - staticOffset,
                initializedBytes);
            var readDtv = context.TryReadUInt64(context.FsBase + sizeof(ulong), out var dtvAddress);
            InitializedBytes = readTemplate ? initializedBytes : [];
            DtvAddress = readDtv ? dtvAddress : 0;
            executionResult = readTemplate && readDtv
                ? OrbisGen2Result.ORBIS_GEN2_OK
                : OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT;
            return true;
        }
    }

    private sealed class TlsPersistenceBackend : INativeCpuBackend
    {
        public string BackendName => "tls-persistence-backend";

        public string? LastError => null;

        public int ExecutionCount { get; private set; }

        public List<byte> ObservedValues { get; } = [];

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
            var tlsProbe = context.FsBase + 0x200;
            Span<byte> value = stackalloc byte[1];
            if (!context.Memory.TryRead(tlsProbe, value))
            {
                executionResult = OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT;
                return true;
            }

            ObservedValues.Add(value[0]);
            executionResult = context.Memory.TryWrite(tlsProbe, [0xA5])
                ? OrbisGen2Result.ORBIS_GEN2_OK
                : OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT;
            ExecutionCount++;
            return true;
        }
    }
}
