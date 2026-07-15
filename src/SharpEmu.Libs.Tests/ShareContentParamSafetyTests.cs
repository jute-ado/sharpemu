// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using SharpEmu.Libs.Share;
using Xunit;

namespace SharpEmu.Libs.Tests;

public sealed class ShareContentParamSafetyTests
{
    private const ulong ContentParamAddress = 0x1000;

    [Fact]
    public void SetContentParamAcceptsNullTerminatedUtf8()
    {
        var memory = new FakeGuestMemory();
        memory.AddRegion(ContentParamAddress, "Dreaming Sarah\0"u8.ToArray());
        var context = CreateContext(memory, ContentParamAddress);

        Assert.Equal(
            (int)OrbisGen2Result.ORBIS_GEN2_OK,
            ShareExports.ShareSetContentParam(context));
        Assert.Equal((ulong)OrbisGen2Result.ORBIS_GEN2_OK, context[CpuRegister.Rax]);
    }

    [Fact]
    public void SetContentParamRejectsAddressWrap()
    {
        var memory = new FakeGuestMemory();
        memory.AddRegion(ulong.MaxValue, [(byte)'D']);
        memory.AddRegion(0, "reaming Sarah\0"u8.ToArray());
        var context = CreateContext(memory, ulong.MaxValue);

        Assert.Equal(
            (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT,
            ShareExports.ShareSetContentParam(context));
        Assert.Equal(
            unchecked((ulong)(int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT),
            context[CpuRegister.Rax]);
    }

    private static CpuContext CreateContext(FakeGuestMemory memory, ulong address)
    {
        var context = new CpuContext(memory, Generation.Gen5);
        context[CpuRegister.Rdi] = address;
        return context;
    }
}
