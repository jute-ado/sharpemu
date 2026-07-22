// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Collections.Concurrent;
using System.Text;

namespace SharpEmu.Core.Cpu.Native;

internal readonly record struct RecentImportTraceData(
    long DispatchIndex,
    string Nid,
    string? LibraryName,
    string? ExportName,
    ulong ThreadHandle,
    ulong ReturnRip,
    ulong Arg0,
    ulong Arg1,
    ulong Arg2,
    ulong Arg3,
    ulong Arg4,
    ulong Arg5);

internal sealed class RecentImportTraceBuffer
{
    private const int MaximumTrackedThreads = 256;
    private const int MaximumPerThreadCapacity = 64;

    private readonly Ring _global;
    private readonly int _perThreadCapacity;
    private readonly ConcurrentDictionary<ulong, Ring> _byThread = [];
    private readonly object _threadRegistrationGate = new();

    public RecentImportTraceBuffer(int capacity)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacity);
        _global = new Ring(capacity);
        _perThreadCapacity = Math.Min(capacity, MaximumPerThreadCapacity);
    }

    public int Capacity => _global.Capacity;

    public Completion Record(RecentImportTraceData entry)
    {
        var global = _global.Record(entry);
        var threadTrace = GetOrCreateThreadTrace(entry.ThreadHandle);
        var thread = threadTrace?.Record(entry) ?? default;
        return new Completion(global, thread);
    }

    public string? Build(int requestedLimit, ulong? prioritizedThreadHandle)
        => BuildSnapshot(requestedLimit, prioritizedThreadHandle).Formatted;

    public (string? Formatted, IReadOnlyList<CpuImportTraceEntry>? Entries) BuildSnapshot(
        int requestedLimit,
        ulong? prioritizedThreadHandle)
    {
        var selected = Select(requestedLimit, prioritizedThreadHandle);
        if (selected.Count == 0)
        {
            return (null, null);
        }

        var entries = selected.Select(ToSnapshot).ToArray();
        return (Format(entries), entries);
    }

    private Ring? GetOrCreateThreadTrace(ulong threadHandle)
    {
        if (_byThread.TryGetValue(threadHandle, out var existing))
        {
            return existing;
        }

        lock (_threadRegistrationGate)
        {
            if (_byThread.TryGetValue(threadHandle, out existing))
            {
                return existing;
            }
            if (_byThread.Count >= MaximumTrackedThreads)
            {
                return null;
            }

            var created = new Ring(_perThreadCapacity);
            _byThread[threadHandle] = created;
            return created;
        }
    }

    private List<Snapshot> Select(int requestedLimit, ulong? prioritizedThreadHandle)
    {
        var limit = Math.Min(Math.Max(requestedLimit, 0), Capacity);
        var global = _global.SnapshotLast(limit);
        if (limit == 0 || global.Count == 0 ||
            prioritizedThreadHandle is not { } threadHandle ||
            !_byThread.TryGetValue(threadHandle, out var prioritizedTrace))
        {
            return global;
        }

        var prioritized = prioritizedTrace.SnapshotLast(_perThreadCapacity);
        var reservedCount = Math.Min(prioritized.Count, Math.Max(1, limit / 2));
        var selected = prioritized.Count <= reservedCount
            ? prioritized
            : prioritized[^reservedCount..];
        var selectedKeys = selected
            .Select(entry => (entry.Data.DispatchIndex, entry.Data.ThreadHandle))
            .ToHashSet();
        for (var index = global.Count - 1;
             index >= 0 && selected.Count < limit;
             index--)
        {
            var entry = global[index];
            if (selectedKeys.Add((entry.Data.DispatchIndex, entry.Data.ThreadHandle)))
            {
                selected.Add(entry);
            }
        }

        selected.Sort(static (left, right) =>
        {
            var dispatchOrder = left.Data.DispatchIndex.CompareTo(right.Data.DispatchIndex);
            return dispatchOrder != 0
                ? dispatchOrder
                : left.Data.ThreadHandle.CompareTo(right.Data.ThreadHandle);
        });
        return selected;
    }

    private static CpuImportTraceEntry ToSnapshot(Snapshot entry) =>
        new(
            entry.Data.DispatchIndex,
            entry.Data.Nid,
            entry.Data.LibraryName,
            entry.Data.ExportName,
            entry.Data.ThreadHandle,
            entry.Data.ReturnRip,
            entry.Data.Arg0,
            entry.Data.Arg1,
            entry.Data.Arg2,
            entry.Data.Arg3,
            entry.Data.Arg4,
            entry.Data.Arg5,
            entry.ReturnValue);

    private static string Format(IReadOnlyList<CpuImportTraceEntry> entries)
    {
        var builder = new StringBuilder(entries.Count * 192);
        for (var index = 0; index < entries.Count; index++)
        {
            if (index > 0)
            {
                builder.AppendLine();
            }

            var entry = entries[index];
            var returnValue = entry.ReturnValue is { } completedReturnValue
                ? $"0x{completedReturnValue:X16}"
                : "<pending>";
            var symbol = entry.LibraryName is { Length: > 0 } libraryName &&
                entry.ExportName is { Length: > 0 } exportName
                ? $"{libraryName}:{exportName}"
                : "<unresolved>";
            builder.Append(
                $"#{entry.DispatchIndex} nid={entry.Nid} symbol={symbol} thread=0x{entry.GuestThreadHandle:X16} " +
                $"ret=0x{entry.ReturnAddress:X16} " +
                $"rdi=0x{entry.Arg0:X16} rsi=0x{entry.Arg1:X16} " +
                $"rdx=0x{entry.Arg2:X16} rcx=0x{entry.Arg3:X16} " +
                $"r8=0x{entry.Arg4:X16} r9=0x{entry.Arg5:X16} rax={returnValue}");
        }

        return builder.ToString();
    }

    internal readonly struct Completion
    {
        private readonly WriteToken _global;
        private readonly WriteToken _thread;

        internal Completion(WriteToken global, WriteToken thread)
        {
            _global = global;
            _thread = thread;
        }

        public void Complete(ulong returnValue)
        {
            _global.Complete(returnValue);
            _thread.Complete(returnValue);
        }
    }

    internal readonly record struct Snapshot(
        RecentImportTraceData Data,
        ulong? ReturnValue);

    internal readonly struct WriteToken(Slot? slot, long sequence)
    {
        public void Complete(ulong returnValue) =>
            slot?.Complete(sequence, returnValue);
    }

    internal sealed class Slot
    {
        private long _publishedSequence;
        private long _completionSequence;
        private long _returnValue;
        private RecentImportTraceData _data;
        private int _writerGate;

        public WriteToken Write(long sequence, RecentImportTraceData data)
        {
            var spinner = new SpinWait();
            while (Interlocked.CompareExchange(ref _writerGate, 1, 0) != 0)
            {
                spinner.SpinOnce();
            }

            try
            {
                var published = Volatile.Read(ref _publishedSequence);
                var publishedMagnitude = published < 0 ? -published : published;
                if (publishedMagnitude > sequence)
                {
                    return default;
                }

                Volatile.Write(ref _publishedSequence, -sequence);
                _data = data;
                Volatile.Write(ref _returnValue, 0);
                Volatile.Write(ref _completionSequence, -sequence);
                Volatile.Write(ref _publishedSequence, sequence);
                return new WriteToken(this, sequence);
            }
            finally
            {
                Volatile.Write(ref _writerGate, 0);
            }
        }

        public void Complete(long sequence, ulong returnValue)
        {
            Volatile.Write(ref _returnValue, unchecked((long)returnValue));
            _ = Interlocked.CompareExchange(
                ref _completionSequence,
                sequence,
                -sequence);
        }

        public bool TryRead(long expectedSequence, out Snapshot snapshot)
        {
            var before = Volatile.Read(ref _publishedSequence);
            if (before != expectedSequence)
            {
                snapshot = default;
                return false;
            }

            var data = _data;
            var completionSequence = Volatile.Read(ref _completionSequence);
            var returnValue = unchecked((ulong)Volatile.Read(ref _returnValue));
            var after = Volatile.Read(ref _publishedSequence);
            if (after != before)
            {
                snapshot = default;
                return false;
            }

            snapshot = new Snapshot(
                data,
                completionSequence == expectedSequence ? returnValue : null);
            return true;
        }
    }

    private sealed class Ring
    {
        private readonly Slot[] _entries;
        private long _nextTicket = -1;

        public Ring(int capacity)
        {
            _entries = new Slot[capacity];
            for (var index = 0; index < _entries.Length; index++)
            {
                _entries[index] = new Slot();
            }
        }

        public int Capacity => _entries.Length;

        public WriteToken Record(RecentImportTraceData entry)
        {
            var ticket = Interlocked.Increment(ref _nextTicket);
            var slot = _entries[(int)((ulong)ticket % (ulong)_entries.Length)];
            return slot.Write(ticket + 1, entry);
        }

        public List<Snapshot> SnapshotLast(int requestedCount)
        {
            var latestTicket = Volatile.Read(ref _nextTicket);
            if (latestTicket < 0 || requestedCount <= 0)
            {
                return [];
            }

            var count = (int)Math.Min(
                Math.Min((long)requestedCount, _entries.Length),
                latestTicket + 1);
            var snapshot = new List<Snapshot>(count);
            var firstTicket = latestTicket - count + 1;
            for (var ticket = firstTicket; ticket <= latestTicket; ticket++)
            {
                var slot = _entries[(int)((ulong)ticket % (ulong)_entries.Length)];
                if (slot.TryRead(ticket + 1, out var entry))
                {
                    snapshot.Add(entry);
                }
            }

            return snapshot;
        }
    }
}
