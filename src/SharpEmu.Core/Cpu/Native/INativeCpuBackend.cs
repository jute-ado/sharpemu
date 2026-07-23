// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;

namespace SharpEmu.Core.Cpu.Native;

public enum NativeEntryReturnContract
{
    RequireZero,
    IgnoreReturnValue,
    CaptureExitCode,
}

public interface INativeCpuBackend
{
    string BackendName { get; }

    string? LastError { get; }

    CpuTrapInfo? LastTrapInfo => null;

    int LastSessionImportsHit => 0;

    int LastSessionUniqueNidsHit => 0;

    string? LastImportResolutionTrace => null;

    IReadOnlyList<CpuImportTraceEntry>? LastImportTraceEntries => null;

    ulong? LastEntryReturnValue => null;

    bool TryExecute(
        CpuContext context,
        ulong entryPoint,
        Generation generation,
        IReadOnlyDictionary<ulong, string> importStubs,
        IReadOnlyDictionary<string, ulong> runtimeSymbols,
        IReadOnlyDictionary<string, ulong> runtimeDataSymbols,
        CpuExecutionOptions executionOptions,
        NativeEntryReturnContract returnContract,
        out OrbisGen2Result result);
}
