// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Core.Cpu;
using SharpEmu.Core.Cpu.Native;
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
        Assert.Equal("synthetic backend failure", dispatcher.LastNotImplementedInfo?.Detail);
    }

    private sealed class FailingNativeBackend(OrbisGen2Result result) : INativeCpuBackend
    {
        public string BackendName => "synthetic-backend";

        public string? LastError => "synthetic backend failure";

        public bool TryExecute(
            CpuContext context,
            ulong entryPoint,
            Generation generation,
            IReadOnlyDictionary<ulong, string> importStubs,
            IReadOnlyDictionary<string, ulong> runtimeSymbols,
            CpuExecutionOptions executionOptions,
            out OrbisGen2Result executionResult)
        {
            executionResult = result;
            return false;
        }
    }
}
