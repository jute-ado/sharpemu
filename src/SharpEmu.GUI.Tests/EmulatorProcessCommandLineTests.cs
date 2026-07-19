// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.GUI;
using Xunit;

namespace SharpEmu.GUI.Tests;

public sealed class EmulatorProcessCommandLineTests
{
    [Fact]
    public void QuotesExecutableAndGamePathsContainingSpaces()
    {
        var commandLine = EmulatorProcess.BuildCommandLine(
            @"C:\Emulator Install\SharpEmu.exe",
            [@"D:\Game Library\sample.bin"]);

        Assert.Equal(
            "\"C:\\Emulator Install\\SharpEmu.exe\" \"D:\\Game Library\\sample.bin\"",
            commandLine);
    }
}
