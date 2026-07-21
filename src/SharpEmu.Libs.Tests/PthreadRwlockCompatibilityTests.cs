// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using SharpEmu.Libs.Kernel;
using Xunit;

namespace SharpEmu.Libs.Tests;

public sealed class PthreadRwlockCompatibilityTests
{
    private const ulong RwlockAddress = 0x7A_0000;

    [Fact]
    public void PosixEntryPointsReturnErrnoWhileSceAliasesKeepKernelErrors()
    {
        var memory = new FakeGuestMemory();
        memory.AddRegion(RwlockAddress, new byte[sizeof(ulong)]);
        var context = new CpuContext(memory, Generation.Gen5);

        context[CpuRegister.Rdi] = 0;
        Assert.Equal(
            22, // EINVAL
            KernelPthreadExtendedCompatExports.PosixPthreadRwlockInit(context));
        Assert.Equal(
            (int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT,
            KernelPthreadExtendedCompatExports.PthreadRwlockInit(context));

        context[CpuRegister.Rdi] = RwlockAddress;
        Assert.Equal(
            (int)OrbisGen2Result.ORBIS_GEN2_OK,
            KernelPthreadExtendedCompatExports.PosixPthreadRwlockInit(context));

        context[CpuRegister.Rdi] = RwlockAddress;
        Assert.Equal(
            1, // EPERM
            KernelPthreadExtendedCompatExports.PosixPthreadRwlockUnlock(context));
        Assert.Equal(
            (int)OrbisGen2Result.ORBIS_GEN2_ERROR_PERMISSION_DENIED,
            KernelPthreadExtendedCompatExports.PthreadRwlockUnlock(context));

        context[CpuRegister.Rdi] = RwlockAddress;
        Assert.Equal(
            (int)OrbisGen2Result.ORBIS_GEN2_OK,
            KernelPthreadExtendedCompatExports.PosixPthreadRwlockRdlock(context));
        Assert.Equal(
            16, // EBUSY
            KernelPthreadExtendedCompatExports.PosixPthreadRwlockDestroy(context));
        Assert.Equal(
            (int)OrbisGen2Result.ORBIS_GEN2_ERROR_BUSY,
            KernelPthreadExtendedCompatExports.PthreadRwlockDestroy(context));

        context[CpuRegister.Rdi] = RwlockAddress;
        Assert.Equal(
            (int)OrbisGen2Result.ORBIS_GEN2_OK,
            KernelPthreadExtendedCompatExports.PosixPthreadRwlockUnlock(context));
        Assert.Equal(
            (int)OrbisGen2Result.ORBIS_GEN2_OK,
            KernelPthreadExtendedCompatExports.PosixPthreadRwlockDestroy(context));
    }

    [Fact]
    public void FailedInitializationDoesNotLeaveResolvableRwlockState()
    {
        var memory = new FakeGuestMemory();
        var context = new CpuContext(memory, Generation.Gen5);
        context[CpuRegister.Rdi] = RwlockAddress;

        Assert.Equal(
            14, // EFAULT
            KernelPthreadExtendedCompatExports.PosixPthreadRwlockInit(context));

        memory.AddRegion(RwlockAddress, new byte[sizeof(ulong)]);
        context[CpuRegister.Rdi] = RwlockAddress;
        Assert.Equal(
            2, // ENOENT
            KernelPthreadExtendedCompatExports.PosixPthreadRwlockDestroy(context));
    }
}
