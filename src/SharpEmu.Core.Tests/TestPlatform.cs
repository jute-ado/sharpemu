// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Runtime.InteropServices;
using Xunit;

namespace SharpEmu.Core.Tests;

public sealed class WindowsFactAttribute : FactAttribute
{
    public WindowsFactAttribute()
    {
        if (!OperatingSystem.IsWindows())
        {
            Skip = "This test requires Windows.";
        }
    }
}

public sealed class WindowsX64FactAttribute : FactAttribute
{
    public WindowsX64FactAttribute()
    {
        if (!OperatingSystem.IsWindows() ||
            RuntimeInformation.ProcessArchitecture != Architecture.X64)
        {
            Skip = "This test requires Windows x64.";
        }
    }
}

public sealed class WindowsX64TheoryAttribute : TheoryAttribute
{
    public WindowsX64TheoryAttribute()
    {
        if (!OperatingSystem.IsWindows() ||
            RuntimeInformation.ProcessArchitecture != Architecture.X64)
        {
            Skip = "This test requires Windows x64.";
        }
    }
}
