// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Threading;

namespace SharpEmu.Core.Cpu.Native;

internal readonly record struct EntryThreadDiagnosticSnapshot(
	int HostThreadId,
	ulong GuestThreadHandle,
	int ImportCount,
	string? LastImportNid,
	ulong LastReturnRip)
{
	public static EntryThreadDiagnosticSnapshot Empty { get; } = new(0, 0, 0, null, 0);

	public bool IsRunning => HostThreadId != 0;
}

internal sealed class EntryThreadDiagnosticState
{
	private int _hostThreadId;
	private long _guestThreadHandle;
	private int _importCount;
	private string? _lastImportNid;
	private long _lastReturnRip;

	public void Reset()
	{
		Volatile.Write(ref _hostThreadId, 0);
		Interlocked.Exchange(ref _guestThreadHandle, 0);
		Volatile.Write(ref _importCount, 0);
		Volatile.Write(ref _lastImportNid, null);
		Interlocked.Exchange(ref _lastReturnRip, 0);
	}

	public void Begin(int hostThreadId, ulong guestThreadHandle)
	{
		if (guestThreadHandle != 0)
		{
			Interlocked.Exchange(ref _guestThreadHandle, unchecked((long)guestThreadHandle));
		}
		Volatile.Write(ref _hostThreadId, hostThreadId);
	}

	public void End() => Volatile.Write(ref _hostThreadId, 0);

	public void RecordImport(string nid, ulong returnRip, ulong guestThreadHandle = 0)
	{
		if (guestThreadHandle != 0)
		{
			Interlocked.Exchange(ref _guestThreadHandle, unchecked((long)guestThreadHandle));
		}
		Volatile.Write(ref _lastImportNid, nid);
		Interlocked.Exchange(ref _lastReturnRip, unchecked((long)returnRip));
		IncrementSaturating(ref _importCount);
	}

	public EntryThreadDiagnosticSnapshot Snapshot() => new(
		Volatile.Read(ref _hostThreadId),
		unchecked((ulong)Interlocked.Read(ref _guestThreadHandle)),
		Volatile.Read(ref _importCount),
		Volatile.Read(ref _lastImportNid),
		unchecked((ulong)Interlocked.Read(ref _lastReturnRip)));

	private static void IncrementSaturating(ref int value)
	{
		while (true)
		{
			var current = Volatile.Read(ref value);
			if (current == int.MaxValue ||
				Interlocked.CompareExchange(ref value, current + 1, current) == current)
			{
				return;
			}
		}
	}
}
