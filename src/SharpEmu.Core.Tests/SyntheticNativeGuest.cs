// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Core.Cpu;
using SharpEmu.Core.Memory;
using SharpEmu.HLE;

namespace SharpEmu.Core.Tests;

internal readonly record struct SyntheticGuestExecutionResult(
    OrbisGen2Result Result,
    CpuExitReason ExitReason,
    string? FailureDetail)
{
    public int ImportsHit { get; init; }

    public int UniqueNidsHit { get; init; }

    public string? ImportTrace { get; init; }

    public IReadOnlyList<CpuImportTraceEntry>? ImportTraceEntries { get; init; }
}

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
        ulong codeAddress = DefaultCodeAddress,
        CpuExecutionOptions executionOptions = default)
    {
        return ExecuteModuleInitializers(
            code,
            generation,
            moduleName,
            executionCount: 1,
            importStubs,
            configureModules,
            codeAddress,
            executionOptions: executionOptions)[0];
    }

    public static IReadOnlyList<SyntheticGuestExecutionResult> ExecuteModuleInitializers(
        byte[] code,
        Generation generation,
        string moduleName,
        int executionCount,
        IReadOnlyDictionary<ulong, string>? importStubs = null,
        Action<ModuleManager>? configureModules = null,
        ulong codeAddress = DefaultCodeAddress,
        ulong guestThreadHandle = 0,
        bool useDedicatedHostThreads = false,
        CpuExecutionOptions executionOptions = default)
    {
        ArgumentNullException.ThrowIfNull(code);
        ArgumentException.ThrowIfNullOrWhiteSpace(moduleName);
        if (executionCount <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(executionCount),
                "Execution count must be greater than zero.");
        }

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
            MapImportStubs(memory, importStubs.Keys, codeAddress);
        }

        var moduleManager = new ModuleManager();
        configureModules?.Invoke(moduleManager);
        moduleManager.Freeze();
        using var dispatcher = new CpuDispatcher(memory, moduleManager);
        var executions = new SyntheticGuestExecutionResult[executionCount];

        void Execute(int index)
        {
            var currentGuestThreadHandle = guestThreadHandle == 0
                ? 0
                : checked(guestThreadHandle + (ulong)index);
            var previousGuestThreadHandle = currentGuestThreadHandle == 0
                ? 0
                : GuestThreadExecution.EnterGuestThread(currentGuestThreadHandle);
            try
            {
                var currentModuleName = executions.Length == 1
                    ? moduleName
                    : $"{moduleName}#{index}";
                var result = dispatcher.DispatchModuleInitializer(
                    entryPoint,
                    generation,
                    importStubs,
                    moduleName: currentModuleName,
                    executionOptions: executionOptions);
                executions[index] = new SyntheticGuestExecutionResult(
                    result,
                    dispatcher.LastSessionSummary.Reason,
                    dispatcher.LastNotImplementedInfo?.Detail)
                {
                    ImportsHit = dispatcher.LastSessionSummary.ImportsHit,
                    UniqueNidsHit = dispatcher.LastSessionSummary.UniqueNidsHit,
                    ImportTrace = dispatcher.LastImportResolutionTrace,
                    ImportTraceEntries = dispatcher.LastImportTraceEntries,
                };
            }
            finally
            {
                if (currentGuestThreadHandle != 0)
                {
                    GuestThreadExecution.RestoreGuestThread(previousGuestThreadHandle);
                }
            }
        }

        for (var i = 0; i < executions.Length; i++)
        {
            if (!useDedicatedHostThreads)
            {
                Execute(i);
                continue;
            }

            Exception? executionException = null;
            var executionIndex = i;
            var hostThread = new Thread(() =>
            {
                try
                {
                    Execute(executionIndex);
                }
                catch (Exception ex)
                {
                    executionException = ex;
                }
            })
            {
                IsBackground = true,
                Name = $"{moduleName}-host-{i}",
            };
            hostThread.Start();
            hostThread.Join();
            if (executionException is not null)
            {
                System.Runtime.ExceptionServices.ExceptionDispatchInfo
                    .Capture(executionException)
                    .Throw();
            }
        }

        return executions;
    }

    private static void MapImportStubs(
        PhysicalVirtualMemory memory,
        IEnumerable<ulong> addresses,
        ulong codeAddress)
    {
        const ulong pageSize = 0x1000;
        var codeEnd = checked(codeAddress + CodeRegionSize);
        var mappedPages = new HashSet<ulong>();
        foreach (var address in addresses.Order())
        {
            if (address > ulong.MaxValue - 2)
            {
                throw new InvalidOperationException(
                    $"Synthetic import stub at 0x{address:X16} overflows guest memory.");
            }

            var stubEnd = address + 2;
            var isInsideCodeRegion = address >= codeAddress && stubEnd <= codeEnd;
            if (!isInsideCodeRegion)
            {
                var pageAddress = address & ~(pageSize - 1);
                if (stubEnd > pageAddress + pageSize)
                {
                    throw new InvalidOperationException(
                        $"Synthetic import stub at 0x{address:X16} crosses a page boundary.");
                }

                if (mappedPages.Add(pageAddress))
                {
                    var mappedAddress = memory.AllocateAt(
                        pageAddress,
                        pageSize,
                        executable: true,
                        allowAlternative: false);
                    if (mappedAddress != pageAddress)
                    {
                        throw new InvalidOperationException(
                            $"Could not map synthetic import page at 0x{pageAddress:X16}.");
                    }
                }
            }

            if (!memory.TryWrite(address, [0xCC, 0xC3]))
            {
                throw new InvalidOperationException(
                    $"Could not write synthetic import stub at 0x{address:X16}.");
            }
        }
    }
}
