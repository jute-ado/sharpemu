// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.GUI;
using Xunit;

namespace SharpEmu.GUI.Tests;

public sealed class EmulatorExitCodeTests
{
    [Theory]
    [InlineData(0, "OK")]
    [InlineData(1, "invalid arguments")]
    [InlineData(4, "emulation error")]
    [InlineData(unchecked((int)0x8000_0003u), "breakpoint exception (0x80000003)")]
    [InlineData(unchecked((int)0xC000_0005u), "access violation (0xC0000005)")]
    [InlineData(unchecked((int)0xC000_001Du), "illegal instruction (0xC000001D)")]
    [InlineData(unchecked((int)0xC000_00FDu), "stack overflow (0xC00000FD)")]
    [InlineData(unchecked((int)0xC000_0374u), "heap corruption (0xC0000374)")]
    [InlineData(unchecked((int)0xC000_0409u), "stack buffer overrun or fast fail (0xC0000409)")]
    [InlineData(134, "aborted (signal 6)")]
    [InlineData(139, "segmentation fault (signal 11)")]
    [InlineData(-1, "exit status unavailable")]
    public void DescribesKnownExitCodes(int exitCode, string expected)
    {
        Assert.Equal(expected, EmulatorExitCode.Describe(exitCode));
    }

    [Fact]
    public void PreservesUnknownNativeStatusAsHex()
    {
        Assert.Equal(
            "unrecognized status 0xDEADBEEF",
            EmulatorExitCode.Describe(unchecked((int)0xDEAD_BEEFu)));
    }

    [Fact]
    public void KeepsUnknownPortableExitCodeConcise()
    {
        Assert.Equal("unknown", EmulatorExitCode.Describe(42));
    }
}
