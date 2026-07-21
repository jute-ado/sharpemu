// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Core.Cpu.Native;
using Xunit;

namespace SharpEmu.Core.Tests;

public sealed class EntryThreadDiagnosticStateTests
{
    [Fact]
    public void SnapshotTracksLiveEntryThreadAndLatestImport()
    {
        var state = new EntryThreadDiagnosticState();

        state.Begin(hostThreadId: 73, guestThreadHandle: 0);
        state.RecordImport("Zxa0VhQVTsk", returnRip: 0x800D58836);
        state.RecordImport("4J2sUJmuHZQ", returnRip: 0x800D59900, guestThreadHandle: 0x2A);

        var snapshot = state.Snapshot();

        Assert.True(snapshot.IsRunning);
        Assert.Equal(73, snapshot.HostThreadId);
        Assert.Equal(0x2AUL, snapshot.GuestThreadHandle);
        Assert.Equal(2, snapshot.ImportCount);
        Assert.Equal("4J2sUJmuHZQ", snapshot.LastImportNid);
        Assert.Equal(0x800D59900UL, snapshot.LastReturnRip);
    }

    [Fact]
    public void EndPreservesLastImportButMarksHostThreadInactive()
    {
        var state = new EntryThreadDiagnosticState();
        state.Begin(hostThreadId: 73, guestThreadHandle: 0x2A);
        state.RecordImport("Zxa0VhQVTsk", returnRip: 0x800D58836);

        state.End();

        var snapshot = state.Snapshot();
        Assert.False(snapshot.IsRunning);
        Assert.Equal(0, snapshot.HostThreadId);
        Assert.Equal(1, snapshot.ImportCount);
        Assert.Equal("Zxa0VhQVTsk", snapshot.LastImportNid);
    }

    [Fact]
    public void ResetClearsPreviousExecutionState()
    {
        var state = new EntryThreadDiagnosticState();
        state.Begin(hostThreadId: 73, guestThreadHandle: 0x2A);
        state.RecordImport("old", returnRip: 0x1234);

        state.Reset();

        Assert.Equal(EntryThreadDiagnosticSnapshot.Empty, state.Snapshot());
    }
}
