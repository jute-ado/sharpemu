// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Core.Cpu.Native;
using Xunit;

namespace SharpEmu.Core.Tests;

public sealed class ImportLoopGuardTests
{
    [Theory]
    [InlineData("1jfXLRVzisc", true)]
    [InlineData("QcteRwbsnV0", true)]
    [InlineData("n88vx3C5nW8", true)]
    [InlineData("Zxa0VhQVTsk", true)]
    [InlineData("T72hz6ffq08", true)]
    [InlineData("Zxa0VhQVIsk", false)]
    [InlineData("unrelated-import", false)]
    public void ClassifiesSchedulingBoundaries(string nid, bool expected)
    {
        Assert.Equal(expected, DirectExecutionBackend.IsImportLoopGuardBoundary(nid));
    }
}
