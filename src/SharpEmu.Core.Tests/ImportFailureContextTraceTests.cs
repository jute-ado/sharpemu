// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Core.Cpu.Native;
using Xunit;

namespace SharpEmu.Core.Tests;

public sealed class ImportFailureContextTraceTests
{
    [Theory]
    [InlineData("memcpy", "Q3VBxCXhUHs", "libSceLibcInternal", "memcpy")]
    [InlineData("q3vBxcxHuHs", "Q3VBxCXhUHs", "libSceLibcInternal", "memcpy")]
    [InlineData("libscelibc", "Q3VBxCXhUHs", "libSceLibcInternal", "memcpy")]
    public void SelectorMatchesNidLibraryOrExportIgnoringCase(
        string selector,
        string nid,
        string libraryName,
        string exportName)
    {
        var trace = new ImportFailureContextTrace(selector);

        Assert.True(trace.ShouldDump(nid, libraryName, exportName, result: -1));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void EmptySelectorDisablesFailureContext(string? selector)
    {
        var trace = new ImportFailureContextTrace(selector);

        Assert.False(trace.Enabled);
        Assert.False(trace.ShouldDump("nid", "library", "export", result: -1));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void NonFailureResultDoesNotMatch(int result)
    {
        var trace = new ImportFailureContextTrace("target");

        Assert.False(trace.ShouldDump("target", "library", "export", result));
    }

    [Fact]
    public void NonMatchingFailureDoesNotConsumeDumpBudget()
    {
        var trace = new ImportFailureContextTrace("target", maximumDumps: 1);

        Assert.False(trace.ShouldDump("other", "library", "export", result: -1));
        Assert.True(trace.ShouldDump("target", "library", "export", result: -1));
    }

    [Fact]
    public void MatchingFailuresAreBoundedBySessionBudget()
    {
        var trace = new ImportFailureContextTrace("target", maximumDumps: 2);

        Assert.True(trace.ShouldDump("target", "library", "export", result: -1));
        Assert.True(trace.ShouldDump("target", "library", "export", result: -2));
        Assert.False(trace.ShouldDump("target", "library", "export", result: -3));
    }
}
