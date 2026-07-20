// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Text;

namespace SharpEmu.Core.Cpu.Native;

internal sealed class RecentImportTraceEntry(
    long DispatchIndex,
    string Nid,
    ulong ThreadHandle,
    ulong ReturnRip,
    ulong Arg0,
    ulong Arg1,
    ulong Arg2,
    ulong Arg3,
    ulong Arg4,
    ulong Arg5)
{
    private ulong _returnValue;
    private int _isComplete;

    public long DispatchIndex { get; } = DispatchIndex;
    public string Nid { get; } = Nid;
    public ulong ThreadHandle { get; } = ThreadHandle;
    public ulong ReturnRip { get; } = ReturnRip;
    public ulong Arg0 { get; } = Arg0;
    public ulong Arg1 { get; } = Arg1;
    public ulong Arg2 { get; } = Arg2;
    public ulong Arg3 { get; } = Arg3;
    public ulong Arg4 { get; } = Arg4;
    public ulong Arg5 { get; } = Arg5;

    public void Complete(ulong returnValue)
    {
        _returnValue = returnValue;
        Volatile.Write(ref _isComplete, 1);
    }

    public bool TryGetReturnValue(out ulong returnValue)
    {
        if (Volatile.Read(ref _isComplete) == 0)
        {
            returnValue = 0;
            return false;
        }

        returnValue = _returnValue;
        return true;
    }
}

internal sealed class RecentImportTraceBuffer
{
    private const int MaximumTrackedThreads = 256;
    private const int MaximumPerThreadCapacity = 64;

    private readonly object _gate = new();
    private readonly Ring _global;
    private readonly int _perThreadCapacity;
    private readonly Dictionary<ulong, Ring> _byThread = [];

    public RecentImportTraceBuffer(int capacity)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacity);
        _global = new Ring(capacity);
        _perThreadCapacity = Math.Min(capacity, MaximumPerThreadCapacity);
    }

    public int Capacity => _global.Capacity;

    public void Record(RecentImportTraceEntry entry)
    {
        lock (_gate)
        {
            _global.Record(entry);
            if (!_byThread.TryGetValue(entry.ThreadHandle, out var threadTrace))
            {
                if (_byThread.Count >= MaximumTrackedThreads)
                {
                    return;
                }

                threadTrace = new Ring(_perThreadCapacity);
                _byThread.Add(entry.ThreadHandle, threadTrace);
            }

            threadTrace.Record(entry);
        }
    }

    public string? Build(int requestedLimit, ulong? prioritizedThreadHandle)
    {
        lock (_gate)
        {
            var limit = Math.Min(Math.Max(requestedLimit, 0), Capacity);
            if (limit == 0)
            {
                return null;
            }

            var global = _global.SnapshotLast(limit);
            if (global.Count == 0)
            {
                return null;
            }

            if (prioritizedThreadHandle is not { } threadHandle ||
                !_byThread.TryGetValue(threadHandle, out var prioritizedTrace))
            {
                return Format(global);
            }

            var reservedCount = Math.Min(
                prioritizedTrace.Count,
                Math.Max(1, limit / 2));
            var prioritized = prioritizedTrace.SnapshotLast(reservedCount);
            var selectedKeys = new HashSet<(long DispatchIndex, ulong ThreadHandle)>();
            foreach (var entry in prioritized)
            {
                selectedKeys.Add((entry.DispatchIndex, entry.ThreadHandle));
            }

            var globalTail = new List<RecentImportTraceEntry>(limit - prioritized.Count);
            for (var index = global.Count - 1;
                 index >= 0 && globalTail.Count < limit - prioritized.Count;
                 index--)
            {
                var entry = global[index];
                if (selectedKeys.Add((entry.DispatchIndex, entry.ThreadHandle)))
                {
                    globalTail.Add(entry);
                }
            }

            globalTail.Reverse();
            prioritized.AddRange(globalTail);
            return Format(prioritized);
        }
    }

    private static string Format(IReadOnlyList<RecentImportTraceEntry> entries)
    {
        var builder = new StringBuilder(entries.Count * 192);
        for (var index = 0; index < entries.Count; index++)
        {
            if (index > 0)
            {
                builder.AppendLine();
            }

            var entry = entries[index];
            var returnValue = entry.TryGetReturnValue(out var completedReturnValue)
                ? $"0x{completedReturnValue:X16}"
                : "<pending>";
            builder.Append(
                $"#{entry.DispatchIndex} nid={entry.Nid} thread=0x{entry.ThreadHandle:X16} " +
                $"ret=0x{entry.ReturnRip:X16} " +
                $"rdi=0x{entry.Arg0:X16} rsi=0x{entry.Arg1:X16} " +
                $"rdx=0x{entry.Arg2:X16} rcx=0x{entry.Arg3:X16} " +
                $"r8=0x{entry.Arg4:X16} r9=0x{entry.Arg5:X16} rax={returnValue}");
        }

        return builder.ToString();
    }

    private sealed class Ring(int capacity)
    {
        private readonly RecentImportTraceEntry[] _entries = new RecentImportTraceEntry[capacity];
        private int _writeIndex;

        public int Capacity => _entries.Length;

        public int Count { get; private set; }

        public void Record(RecentImportTraceEntry entry)
        {
            _entries[_writeIndex] = entry;
            _writeIndex = (_writeIndex + 1) % Capacity;
            Count = Math.Min(Count + 1, Capacity);
        }

        public List<RecentImportTraceEntry> SnapshotLast(int requestedCount)
        {
            var count = Math.Min(Count, Math.Max(requestedCount, 0));
            var snapshot = new List<RecentImportTraceEntry>(count);
            var readIndex = (_writeIndex - count + Capacity) % Capacity;
            for (var index = 0; index < count; index++)
            {
                snapshot.Add(_entries[(readIndex + index) % Capacity]);
            }

            return snapshot;
        }
    }
}
