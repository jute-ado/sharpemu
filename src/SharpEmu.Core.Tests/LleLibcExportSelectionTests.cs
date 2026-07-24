// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Core.Cpu.Native;
using Xunit;

namespace SharpEmu.Core.Tests;

public sealed class LleLibcExportSelectionTests
{
    [Theory]
    [InlineData("_Getpctype")]
    [InlineData("_Getptolower")]
    [InlineData("_Getptoupper")]
    public void CtypeTableAccessors_PreferBundledLibcWhenAvailable(string exportName)
    {
        Assert.True(DirectExecutionBackend.IsSafeLleLibcExport(exportName));
    }
}
