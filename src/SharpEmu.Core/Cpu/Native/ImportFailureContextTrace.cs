// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.Core.Cpu.Native;

internal sealed class ImportFailureContextTrace
{
    public const int DefaultTraceCapacity = 64;
    public const int DefaultMaximumDumps = 8;

    private readonly string? _selector;
    private readonly int _maximumDumps;
    private int _dumpCount;

    public ImportFailureContextTrace(
        string? selector,
        int maximumDumps = DefaultMaximumDumps)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumDumps);
        _selector = string.IsNullOrWhiteSpace(selector) ? null : selector.Trim();
        _maximumDumps = maximumDumps;
    }

    public bool Enabled => _selector is not null;

    public bool ShouldDump(
        string nid,
        string? libraryName,
        string? exportName,
        int result)
    {
        if (result >= 0 || _selector is not { } selector ||
            !Matches(selector, nid, libraryName, exportName))
        {
            return false;
        }

        while (true)
        {
            var dumpCount = Volatile.Read(ref _dumpCount);
            if (dumpCount >= _maximumDumps)
            {
                return false;
            }

            if (Interlocked.CompareExchange(ref _dumpCount, dumpCount + 1, dumpCount) == dumpCount)
            {
                return true;
            }
        }
    }

    private static bool Matches(
        string selector,
        string nid,
        string? libraryName,
        string? exportName) =>
        nid.Contains(selector, StringComparison.OrdinalIgnoreCase) ||
        (libraryName?.Contains(selector, StringComparison.OrdinalIgnoreCase) ?? false) ||
        (exportName?.Contains(selector, StringComparison.OrdinalIgnoreCase) ?? false);
}
