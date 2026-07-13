// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using System.Reflection;
using SharpEmu.HLE;
using SharpEmu.Libs.Mouse;
using SharpEmu.Libs.UserService;
using Xunit;

namespace SharpEmu.Libs.Tests;

public sealed class InputCompatibilityExportsTests
{
    private const ulong OutputAddress = 0x1000;
    private const int PrimaryUserId = 1;
    private const int OrbisUserServiceErrorInvalidArgument = unchecked((int)0x80960005);
    private const int OrbisUserServiceErrorInvalidParameter = unchecked((int)0x80960009);

    [Fact]
    public void MouseInitReturnsSuccess()
    {
        var context = new CpuContext(new FakeGuestMemory(), Generation.Gen5);

        var result = MouseExports.MouseInit(context);

        Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_OK, result);
        Assert.Equal(0UL, context[CpuRegister.Rax]);
    }

    [Fact]
    public void UserServiceGetAgeLevelWritesAdultLevel()
    {
        var output = new byte[sizeof(int)];
        var context = CreateAgeLevelContext(output);

        var result = UserServiceExports.UserServiceGetAgeLevel(context);

        Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_OK, result);
        Assert.Equal(18, BinaryPrimitives.ReadInt32LittleEndian(output));
        Assert.Equal(0UL, context[CpuRegister.Rax]);
    }

    [Fact]
    public void UserServiceGetAgeLevelRejectsUnknownUser()
    {
        var output = new byte[sizeof(int)];
        var context = CreateAgeLevelContext(output);
        context[CpuRegister.Rdi] = 42;

        var result = UserServiceExports.UserServiceGetAgeLevel(context);

        Assert.Equal(OrbisUserServiceErrorInvalidParameter, result);
        Assert.Equal(unchecked((ulong)OrbisUserServiceErrorInvalidParameter), context[CpuRegister.Rax]);
        Assert.Equal(0, BinaryPrimitives.ReadInt32LittleEndian(output));
    }

    [Fact]
    public void UserServiceGetAgeLevelRejectsNullOutput()
    {
        var context = new CpuContext(new FakeGuestMemory(), Generation.Gen5);
        context[CpuRegister.Rdi] = PrimaryUserId;

        var result = UserServiceExports.UserServiceGetAgeLevel(context);

        Assert.Equal(OrbisUserServiceErrorInvalidArgument, result);
        Assert.Equal(unchecked((ulong)OrbisUserServiceErrorInvalidArgument), context[CpuRegister.Rax]);
    }

    [Fact]
    public void UserServiceGetAgeLevelRejectsUnmappedOutput()
    {
        var context = new CpuContext(new FakeGuestMemory(), Generation.Gen5);
        context[CpuRegister.Rdi] = PrimaryUserId;
        context[CpuRegister.Rsi] = OutputAddress;

        var result = UserServiceExports.UserServiceGetAgeLevel(context);

        Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT, result);
        Assert.Equal(
            unchecked((ulong)(int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT),
            context[CpuRegister.Rax]);
    }

    [Theory]
    [InlineData(typeof(MouseExports), nameof(MouseExports.MouseInit), "Qs0wWulgl7U", "sceMouseInit", "libSceMouse")]
    [InlineData(typeof(UserServiceExports), nameof(UserServiceExports.UserServiceGetAgeLevel), "woNpu+45RLk", "sceUserServiceGetAgeLevel", "libSceUserService")]
    public void CompatibilityExportMetadataIsExact(
        Type declaringType,
        string methodName,
        string nid,
        string exportName,
        string libraryName)
    {
        var method = declaringType.GetMethod(methodName, BindingFlags.Public | BindingFlags.Static);
        var attribute = method?.GetCustomAttribute<SysAbiExportAttribute>();

        Assert.NotNull(attribute);
        Assert.Equal(nid, attribute.Nid);
        Assert.Equal(exportName, attribute.ExportName);
        Assert.Equal(libraryName, attribute.LibraryName);
        Assert.Equal(Generation.Gen4 | Generation.Gen5, attribute.Target);
    }

    private static CpuContext CreateAgeLevelContext(byte[] output)
    {
        var memory = new FakeGuestMemory();
        memory.AddRegion(OutputAddress, output);
        var context = new CpuContext(memory, Generation.Gen5);
        context[CpuRegister.Rdi] = PrimaryUserId;
        context[CpuRegister.Rsi] = OutputAddress;
        return context;
    }
}
