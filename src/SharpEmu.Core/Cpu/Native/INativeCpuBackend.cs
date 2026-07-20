// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;

namespace SharpEmu.Core.Cpu.Native;

public enum NativeEntryReturnContract
{
    RequireZero,
    IgnoreReturnValue,
}

public interface INativeCpuBackend
{
    string BackendName { get; }

    string? LastError { get; }

    CpuTrapInfo? LastTrapInfo => null;

    int LastSessionImportsHit => 0;

    bool TryExecute(
        CpuContext context,
        ulong entryPoint,
        Generation generation,
        IReadOnlyDictionary<ulong, string> importStubs,
        IReadOnlyDictionary<string, ulong> runtimeSymbols,
        CpuExecutionOptions executionOptions,
        NativeEntryReturnContract returnContract,
        out OrbisGen2Result result);
}
