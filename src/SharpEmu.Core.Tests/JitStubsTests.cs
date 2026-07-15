// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Core.Cpu.Native;
using Xunit;

namespace SharpEmu.Core.Tests;

public sealed class JitStubsTests
{
    [Fact]
    public unsafe void TlsPatternScannerFindsPatternThatFillsBuffer()
    {
        var code = JitStubs.TlsAccessPattern.ToArray();

        fixed (byte* start = code)
        {
            var matches = JitStubs.FindTlsAccessPatterns(start, code.Length);

            Assert.Equal([(nint)start], matches);
        }
    }

    [Fact]
    public unsafe void TlsPatternScannerFindsPatternAtFinalValidOffset()
    {
        var pattern = JitStubs.TlsAccessPattern;
        var code = new byte[pattern.Length + 4];
        pattern.CopyTo(code.AsSpan(4));

        fixed (byte* start = code)
        {
            var matches = JitStubs.FindTlsAccessPatterns(start, code.Length);

            Assert.Equal([(nint)(start + 4)], matches);
        }
    }

    [Fact]
    public unsafe void TlsPatternScannerRejectsBuffersShorterThanPattern()
    {
        var code = new byte[JitStubs.TlsAccessPattern.Length - 1];

        fixed (byte* start = code)
        {
            Assert.Empty(JitStubs.FindTlsAccessPatterns(start, code.Length));
        }
    }
}
