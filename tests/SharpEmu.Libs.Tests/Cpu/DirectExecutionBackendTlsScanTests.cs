// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Core.Cpu.Native;
using Xunit;

namespace SharpEmu.Libs.Tests.Cpu;

public sealed class DirectExecutionBackendTlsScanTests
{
    [Fact]
    public void GetTlsPatchCandidates_IncludesLastValidOffset()
    {
        ReadOnlySpan<byte> pattern =
        [
            0x64, 0x48, 0x8B, 0x04, 0x25, 0x00, 0x00, 0x00, 0x00,
        ];
        var code = new byte[pattern.Length + 3];
        code.AsSpan(0, 3).Fill(0x90);
        pattern.CopyTo(code.AsSpan(3));

        var candidates = DirectExecutionBackend.GetTlsPatchCandidates(
            code,
            code.Length,
            out var consumedLength);

        var candidate = Assert.Single(candidates);
        Assert.Equal(3, candidate.Offset);
        Assert.Equal(code.Length, consumedLength);
    }
}
