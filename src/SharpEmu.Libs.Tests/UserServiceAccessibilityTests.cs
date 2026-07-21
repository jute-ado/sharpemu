// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using SharpEmu.HLE;
using SharpEmu.Libs.UserService;
using Xunit;

namespace SharpEmu.Libs.Tests;

public sealed class UserServiceAccessibilityTests
{
    private const ulong OutputAddress = 0x1000;
    private const int OrbisUserServiceErrorInvalidArgument = unchecked((int)0x80960005);
    private const int OrbisUserServiceErrorNotLoggedIn = unchecked((int)0x80960009);

    public static TheoryData<string, string, int> AccessibilityExports => new()
    {
        { "rnEhHqG-4xo", "sceUserServiceGetAccessibilityChatTranscription", 0 },
        { "ZKJtxdgvzwg", "sceUserServiceGetAccessibilityPressAndHoldDelay", 0 },
        { "qWYHOFwqCxY", "sceUserServiceGetAccessibilityVibration", 1 },
        { "-3Y5GO+-i78", "sceUserServiceGetAccessibilityTriggerEffect", 1 },
        { "hD-H81EN9Vg", "sceUserServiceGetAccessibilityZoomEnabled", 0 },
        { "O6IW1-Dwm-w", "sceUserServiceGetAccessibilityZoomFollowFocus", 0 },
    };

    [Theory]
    [MemberData(nameof(AccessibilityExports))]
    public void AccessibilityExportWritesDeterministicOfflineDefault(
        string nid,
        string exportName,
        int expectedValue)
    {
        var output = Enumerable.Repeat((byte)0xCC, sizeof(int)).ToArray();
        var context = CreateContext(output);
        var export = FindExport(nid);

        var result = export.Function(context);

        Assert.Equal(exportName, export.Name);
        Assert.Equal("libSceUserService", export.LibraryName);
        Assert.Equal(Generation.Gen5, export.Target);
        Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_OK, result);
        Assert.Equal(0UL, context[CpuRegister.Rax]);
        Assert.Equal(expectedValue, BinaryPrimitives.ReadInt32LittleEndian(output));
    }

    [Fact]
    public void AccessibilityExportRejectsNullOutput()
    {
        var context = new CpuContext(new FakeGuestMemory(), Generation.Gen5);
        context[CpuRegister.Rdi] = EmulatedUser.PrimaryId;

        var result = FindExport("rnEhHqG-4xo").Function(context);

        Assert.Equal(OrbisUserServiceErrorInvalidArgument, result);
        Assert.Equal(unchecked((ulong)OrbisUserServiceErrorInvalidArgument), context[CpuRegister.Rax]);
    }

    [Fact]
    public void AccessibilityExportRejectsUnknownUser()
    {
        var output = new byte[sizeof(int)];
        var context = CreateContext(output);
        context[CpuRegister.Rdi] = 42;

        var result = FindExport("rnEhHqG-4xo").Function(context);

        Assert.Equal(OrbisUserServiceErrorNotLoggedIn, result);
        Assert.Equal(unchecked((ulong)OrbisUserServiceErrorNotLoggedIn), context[CpuRegister.Rax]);
        Assert.Equal(0, BinaryPrimitives.ReadInt32LittleEndian(output));
    }

    [Fact]
    public void AccessibilityExportReturnsMemoryFaultForUnmappedOutput()
    {
        var context = new CpuContext(new FakeGuestMemory(), Generation.Gen5);
        context[CpuRegister.Rdi] = EmulatedUser.PrimaryId;
        context[CpuRegister.Rsi] = OutputAddress;

        var result = FindExport("rnEhHqG-4xo").Function(context);

        Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT, result);
        Assert.Equal(
            unchecked((ulong)(int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT),
            context[CpuRegister.Rax]);
    }

    private static ExportedFunction FindExport(string nid) =>
        Assert.Single(
            SharpEmu.Generated.SysAbiExportRegistry.CreateExports(Generation.Gen5),
            candidate => candidate.Nid == nid);

    private static CpuContext CreateContext(byte[] output)
    {
        var memory = new FakeGuestMemory();
        memory.AddRegion(OutputAddress, output);
        var context = new CpuContext(memory, Generation.Gen5);
        context[CpuRegister.Rdi] = EmulatedUser.PrimaryId;
        context[CpuRegister.Rsi] = OutputAddress;
        return context;
    }
}
