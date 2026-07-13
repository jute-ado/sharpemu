// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Reflection;
using SharpEmu.HLE;
using SharpEmu.Libs.Kernel;
using Xunit;

namespace SharpEmu.Libs.Tests;

public sealed class PthreadDetachCompatibilityTests
{
    [Fact]
    public void PosixDetachMarksThreadAsDetachedAndPreventsJoin()
    {
        const ulong thread = 0xD37A_C001;
        var context = CreateContext(thread);

        var detachResult = KernelPthreadExtendedCompatExports.PosixPthreadDetach(context);

        Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_OK, detachResult);
        Assert.Equal(0UL, context[CpuRegister.Rax]);
        Assert.True(KernelPthreadExtendedCompatExports.IsThreadDetached(thread));

        context[CpuRegister.Rdi] = thread;
        context[CpuRegister.Rsi] = 0;
        var joinResult = KernelExports.PthreadJoin(context);

        Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT, joinResult);
        Assert.Equal(
            unchecked((ulong)(int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT),
            context[CpuRegister.Rax]);
    }

    [Fact]
    public void DetachingThreadTwiceReturnsInvalidArgument()
    {
        var context = CreateContext(0xD37A_C002);
        Assert.Equal(
            (int)OrbisGen2Result.ORBIS_GEN2_OK,
            KernelPthreadExtendedCompatExports.PthreadDetach(context));

        var result = KernelPthreadExtendedCompatExports.PosixPthreadDetach(context);

        Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT, result);
        Assert.Equal(
            unchecked((ulong)(int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT),
            context[CpuRegister.Rax]);
    }

    [Fact]
    public void DetachingNullThreadReturnsInvalidArgument()
    {
        var context = CreateContext(0);

        var result = KernelPthreadExtendedCompatExports.PosixPthreadDetach(context);

        Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT, result);
        Assert.Equal(
            unchecked((ulong)(int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT),
            context[CpuRegister.Rax]);
    }

    [Theory]
    [InlineData(nameof(KernelPthreadExtendedCompatExports.PthreadDetach), "4qGrR6eoP9Y", "scePthreadDetach", "libKernel")]
    [InlineData(nameof(KernelPthreadExtendedCompatExports.PosixPthreadDetach), "+U1R4WtXvoc", "pthread_detach", "libScePosix")]
    public void DetachExportMetadataIsExact(
        string methodName,
        string nid,
        string exportName,
        string libraryName)
    {
        var method = typeof(KernelPthreadExtendedCompatExports).GetMethod(
            methodName,
            BindingFlags.Public | BindingFlags.Static);
        var attribute = method?.GetCustomAttribute<SysAbiExportAttribute>();

        Assert.NotNull(attribute);
        Assert.Equal(nid, attribute.Nid);
        Assert.Equal(exportName, attribute.ExportName);
        Assert.Equal(libraryName, attribute.LibraryName);
        Assert.Equal(Generation.Gen4 | Generation.Gen5, attribute.Target);
    }

    private static CpuContext CreateContext(ulong thread)
    {
        var context = new CpuContext(new FakeGuestMemory(), Generation.Gen5);
        context[CpuRegister.Rdi] = thread;
        return context;
    }
}
