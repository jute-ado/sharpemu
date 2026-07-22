// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using SharpEmu.Libs.SystemService;
using SharpEmu.Libs.Voice;
using Xunit;

namespace SharpEmu.Libs.Tests;

public sealed class UpstreamServiceExportTests
{
    [Theory]
    [InlineData("Trpt2QBZHCI", "sceVoiceQoSGetStatus")]
    [InlineData("FuXenJLkk-c", "sceVoiceQoSTerminate")]
    [InlineData("+0lOiPZjnBI", "sceVoiceQoSSetMode")]
    public void VoiceQosExportMetadataIsExact(string nid, string name)
    {
        ExportMetadataAssert.Exact(
            nid,
            name,
            "libSceVoiceQoS",
            Generation.Gen4 | Generation.Gen5);
    }

    [Fact]
    public void VoiceQosOfflineOperationsReturnSuccess()
    {
        var context = new CpuContext(new FakeGuestMemory(), Generation.Gen5);

        Assert.Equal(0, VoiceQoSExports.VoiceQoSGetStatus(context));
        Assert.Equal(0UL, context[CpuRegister.Rax]);
        Assert.Equal(0, VoiceQoSExports.VoiceQoSSetMode(context));
        Assert.Equal(0, VoiceQoSExports.VoiceQoSTerminate(context));
    }

    [Fact]
    public void NoticeScreenSkipFlagSetterMatchesTheParameterlessAbi()
    {
        ExportMetadataAssert.Exact(
            "Q3utJvma4Mo",
            "sceSystemServiceSetNoticeScreenSkipFlag",
            "libSceSystemService",
            Generation.Gen5);

        var context = new CpuContext(new FakeGuestMemory(), Generation.Gen5);
        context[CpuRegister.Rdi] = ulong.MaxValue;
        Assert.Equal(
            0,
            SystemServiceExports.SystemServiceSetNoticeScreenSkipFlag(context));
        Assert.Equal(0UL, context[CpuRegister.Rax]);
    }
}
