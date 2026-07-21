// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Collections.Concurrent;

namespace SharpEmu.Core.Cpu.Native;

internal readonly record struct ImportHotspot(string Name, long Count);

internal readonly record struct ImportProfileSnapshot(
    ImportHotspot[] Imports,
    ImportHotspot[] Threads,
    ImportHotspot[] CallSites,
    ImportHotspot[] ThreadImports,
    ImportHotspot[] ThreadCallSites);

internal sealed class ImportHotspotCounter
{
    private ConcurrentDictionary<string, long> _counts =
        new(StringComparer.Ordinal);

    public void Record(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        _counts.AddOrUpdate(name, 1, static (_, count) => count + 1);
    }

    public ImportHotspot[] TakeTop(int limit)
    {
        if (limit <= 0)
        {
            return [];
        }

        var snapshot = Interlocked.Exchange(
            ref _counts,
            new ConcurrentDictionary<string, long>(StringComparer.Ordinal));
        return snapshot
            .Select(static entry => new ImportHotspot(entry.Key, entry.Value))
            .OrderByDescending(static entry => entry.Count)
            .ThenBy(static entry => entry.Name, StringComparer.Ordinal)
            .Take(limit)
            .ToArray();
    }
}

internal sealed class ImportProfileWindow
{
    private readonly ImportHotspotCounter _imports = new();
    private readonly ImportHotspotCounter _threads = new();
    private readonly ImportHotspotCounter _callSites = new();
    private readonly ImportHotspotCounter _threadImports = new();
    private readonly ImportHotspotCounter _threadCallSites = new();

    public void Record(
        string importName,
        ulong guestThreadHandle,
        ulong returnRip,
        string? callSite = null)
    {
        var site = string.IsNullOrWhiteSpace(callSite)
            ? $"0x{returnRip:X16}"
            : callSite;
        _imports.Record(importName);
        _threads.Record($"0x{guestThreadHandle:X16}");
        _callSites.Record($"{importName}@{site}");
        _threadImports.Record($"0x{guestThreadHandle:X16}:{importName}");
        _threadCallSites.Record(
            $"0x{guestThreadHandle:X16}:{importName}@{site}");
    }

    public ImportProfileSnapshot TakeTop(int limit) => new(
        _imports.TakeTop(limit),
        _threads.TakeTop(limit),
        _callSites.TakeTop(limit),
        _threadImports.TakeTop(limit),
        _threadCallSites.TakeTop(limit));
}

internal sealed class ImportProfileBoundary
{
    private readonly long _interval;
    private long _nextBoundary;

    public ImportProfileBoundary(long interval)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(interval);
        _interval = interval;
        _nextBoundary = interval;
    }

    public bool ShouldTakeSnapshot(long dispatchIndex)
    {
        while (true)
        {
            var boundary = Volatile.Read(ref _nextBoundary);
            if (dispatchIndex < boundary)
            {
                return false;
            }

            if (Interlocked.CompareExchange(
                    ref _nextBoundary,
                    boundary + _interval,
                    boundary) == boundary)
            {
                return true;
            }
        }
    }
}
