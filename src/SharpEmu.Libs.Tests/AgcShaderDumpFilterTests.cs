// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.Agc;
using Xunit;

namespace SharpEmu.Libs.Tests;

public sealed class AgcShaderDumpFilterTests
{
    [Fact]
    public void SignatureFilterMatchesAcrossAslrAddresses()
    {
        byte[] payload = [1, 2, 3, 4];

        Assert.True(AgcExports.ShaderDumpFilterMatches(
            null, "9f64a747e1b97f13", 0x1000, payload));
        Assert.True(AgcExports.ShaderDumpFilterMatches(
            null, "9F64A747E1B97F13", 0x9000, payload));
        Assert.False(AgcExports.ShaderDumpFilterMatches(
            null, "DEADBEEF", 0x9000, payload));
    }

    [Fact]
    public void AddressAndSignatureFiltersMustBothMatch()
    {
        byte[] payload = [1, 2, 3, 4];

        Assert.True(AgcExports.ShaderDumpFilterMatches(
            "0x1000", "9F64A747E1B97F13", 0x1000, payload));
        Assert.False(AgcExports.ShaderDumpFilterMatches(
            "0x2000", "9F64A747E1B97F13", 0x1000, payload));
    }
}
