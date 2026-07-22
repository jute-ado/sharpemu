// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using SharpEmu.Libs.Agc;
using Xunit;

namespace SharpEmu.Libs.Tests;

public sealed class AgcCommandSizeExportsTests
{
    [Theory]
    [InlineData("cb-nop", 8)]
    [InlineData("acb-acquire", 32)]
    [InlineData("dcb-acquire", 32)]
    [InlineData("cb-eop", 32)]
    [InlineData("dcb-rewind", 8)]
    [InlineData("dcb-jump", 16)]
    public void PacketSizingProbeReturnsExactByteCount(
        string command,
        int expectedBytes)
    {
        var context = new CpuContext(new FakeGuestMemory(), Generation.Gen5);

        var result = command switch
        {
            "cb-nop" => AgcExports.CbNopGetSize(context),
            "acb-acquire" => AgcExports.AcbAcquireMemGetSize(context),
            "dcb-acquire" => AgcExports.DcbAcquireMemGetSize(context),
            "cb-eop" => AgcExports.CbQueueEndOfPipeActionGetSize(context),
            "dcb-rewind" => AgcExports.DcbRewindGetSize(context),
            "dcb-jump" => AgcExports.DcbJumpGetSize(context),
            _ => throw new ArgumentOutOfRangeException(nameof(command)),
        };

        Assert.Equal(expectedBytes, result);
        Assert.Equal((ulong)expectedBytes, context[CpuRegister.Rax]);
    }
}
