// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Core.Cpu;
using SharpEmu.Core.Memory;
using SharpEmu.HLE;

namespace SharpEmu.Core.Tests;

internal readonly record struct SyntheticGuestExecutionResult(
    OrbisGen2Result Result,
    CpuExitReason ExitReason,
    string? FailureDetail);

internal static class SyntheticNativeGuest
{
    public const ulong DefaultCodeAddress = 0x0000_0008_1000_0000;
    private const ulong CodeRegionSize = 0x1000;

    public static SyntheticGuestExecutionResult ExecuteModuleInitializer(
        byte[] code,
        Generation generation,
        string moduleName,
        IReadOnlyDictionary<ulong, string>? importStubs = null,
        Action<ModuleManager>? configureModules = null,
        ulong codeAddress = DefaultCodeAddress)
    {
        ArgumentNullException.ThrowIfNull(code);
        ArgumentException.ThrowIfNullOrWhiteSpace(moduleName);
        if ((ulong)code.Length > CodeRegionSize)
        {
            throw new ArgumentException("Synthetic guest code exceeds its mapped region.", nameof(code));
        }

        using var memory = new PhysicalVirtualMemory();
        var entryPoint = memory.AllocateAt(codeAddress, CodeRegionSize, executable: true);
        if (entryPoint != codeAddress || !memory.TryWrite(entryPoint, code))
        {
            throw new InvalidOperationException(
                $"Could not map synthetic guest code at 0x{codeAddress:X16}.");
        }

        if (importStubs is not null)
        {
            foreach (var address in importStubs.Keys)
            {
                if (address < codeAddress || address > codeAddress + CodeRegionSize - 2 ||
                    !memory.TryWrite(address, [0xCC, 0xC3]))
                {
                    throw new InvalidOperationException(
                        $"Could not map synthetic import stub at 0x{address:X16}.");
                }
            }
        }

        var moduleManager = new ModuleManager();
        configureModules?.Invoke(moduleManager);
        moduleManager.Freeze();
        using var dispatcher = new CpuDispatcher(memory, moduleManager);
        var result = dispatcher.DispatchModuleInitializer(
            entryPoint,
            generation,
            importStubs,
            moduleName: moduleName);

        return new SyntheticGuestExecutionResult(
            result,
            dispatcher.LastSessionSummary.Reason,
            dispatcher.LastNotImplementedInfo?.Detail);
    }
}
