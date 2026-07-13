// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using System.Reflection;
using System.Runtime.InteropServices;
using SharpEmu.HLE;
using SharpEmu.Libs.Kernel;
using Xunit;

namespace SharpEmu.Libs.Tests;

public sealed class PthreadSchedulingCompatibilityTests
{
    private const ulong PolicyAddress = 0x1000;
    private const ulong ParameterAddress = 0x2000;
    private const ulong PriorityAddress = 0x3000;

    [Fact]
    public void GetschedparamReturnsDefaultPolicyAndPriority()
    {
        var policy = new byte[sizeof(int)];
        var parameter = new byte[sizeof(int)];
        var context = CreateContext(policy, parameter);
        SetGetArguments(context, thread: 0x7100, PolicyAddress, ParameterAddress);

        var result = KernelPthreadExtendedCompatExports.PthreadGetschedparam(context);

        Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_OK, result);
        Assert.Equal(0UL, context[CpuRegister.Rax]);
        Assert.Equal(1, BinaryPrimitives.ReadInt32LittleEndian(policy));
        Assert.Equal(700, BinaryPrimitives.ReadInt32LittleEndian(parameter));
    }

    [Fact]
    public void SceSchedulingParametersRoundTripAndStayInSyncWithPriorityApi()
    {
        var policy = new byte[sizeof(int)];
        var parameter = new byte[sizeof(int)];
        var priority = new byte[sizeof(int)];
        BinaryPrimitives.WriteInt32LittleEndian(parameter, 512);
        var context = CreateContext(policy, parameter, priority);

        context[CpuRegister.Rdi] = 0x7200;
        context[CpuRegister.Rsi] = 2;
        context[CpuRegister.Rdx] = ParameterAddress;
        Assert.Equal(
            (int)OrbisGen2Result.ORBIS_GEN2_OK,
            KernelPthreadExtendedCompatExports.PthreadSetschedparam(context));

        policy.AsSpan().Fill(0xCC);
        parameter.AsSpan().Fill(0xCC);
        SetGetArguments(context, thread: 0x7200, PolicyAddress, ParameterAddress);
        Assert.Equal(
            (int)OrbisGen2Result.ORBIS_GEN2_OK,
            KernelPthreadExtendedCompatExports.PthreadGetschedparam(context));
        Assert.Equal(2, BinaryPrimitives.ReadInt32LittleEndian(policy));
        Assert.Equal(512, BinaryPrimitives.ReadInt32LittleEndian(parameter));

        context[CpuRegister.Rdi] = 0x7200;
        context[CpuRegister.Rsi] = PriorityAddress;
        Assert.Equal(
            (int)OrbisGen2Result.ORBIS_GEN2_OK,
            KernelPthreadExtendedCompatExports.PthreadGetprio(context));
        Assert.Equal(0UL, context[CpuRegister.Rax]);
        Assert.Equal(512, BinaryPrimitives.ReadInt32LittleEndian(priority));

        context[CpuRegister.Rdi] = 0x7200;
        context[CpuRegister.Rsi] = 384;
        Assert.Equal(
            (int)OrbisGen2Result.ORBIS_GEN2_OK,
            KernelPthreadExtendedCompatExports.PthreadSetprio(context));

        SetGetArguments(context, thread: 0x7200, PolicyAddress, ParameterAddress);
        Assert.Equal(
            (int)OrbisGen2Result.ORBIS_GEN2_OK,
            KernelPthreadExtendedCompatExports.PthreadGetschedparam(context));
        Assert.Equal(2, BinaryPrimitives.ReadInt32LittleEndian(policy));
        Assert.Equal(384, BinaryPrimitives.ReadInt32LittleEndian(parameter));
    }

    [Fact]
    public void PosixSchedulingExportsShareTheSameState()
    {
        var policy = new byte[sizeof(int)];
        var parameter = new byte[sizeof(int)];
        BinaryPrimitives.WriteInt32LittleEndian(parameter, 640);
        var context = CreateContext(policy, parameter);
        context[CpuRegister.Rdi] = 0x7300;
        context[CpuRegister.Rsi] = 3;
        context[CpuRegister.Rdx] = ParameterAddress;

        Assert.Equal(
            (int)OrbisGen2Result.ORBIS_GEN2_OK,
            KernelPthreadExtendedCompatExports.PosixPthreadSetschedparam(context));

        SetGetArguments(context, thread: 0x7300, PolicyAddress, ParameterAddress);
        Assert.Equal(
            (int)OrbisGen2Result.ORBIS_GEN2_OK,
            KernelPthreadExtendedCompatExports.PosixPthreadGetschedparam(context));
        Assert.Equal(3, BinaryPrimitives.ReadInt32LittleEndian(policy));
        Assert.Equal(640, BinaryPrimitives.ReadInt32LittleEndian(parameter));
    }

    [Fact]
    public void SchedulingParametersSupportTrackedLibcBuffers()
    {
        var context = new CpuContext(new FakeGuestMemory(), Generation.Gen5);
        var policyAddress = AllocateTrackedBuffer(context);
        var parameterAddress = AllocateTrackedBuffer(context);

        try
        {
            Marshal.WriteInt32((nint)parameterAddress, 456);
            context[CpuRegister.Rdi] = 0x7400;
            context[CpuRegister.Rsi] = 2;
            context[CpuRegister.Rdx] = parameterAddress;
            Assert.Equal(
                (int)OrbisGen2Result.ORBIS_GEN2_OK,
                KernelPthreadExtendedCompatExports.PthreadSetschedparam(context));

            SetGetArguments(context, thread: 0x7400, policyAddress, parameterAddress);
            Assert.Equal(
                (int)OrbisGen2Result.ORBIS_GEN2_OK,
                KernelPthreadExtendedCompatExports.PthreadGetschedparam(context));
            Assert.Equal(2, Marshal.ReadInt32((nint)policyAddress));
            Assert.Equal(456, Marshal.ReadInt32((nint)parameterAddress));
        }
        finally
        {
            FreeTrackedBuffer(context, parameterAddress);
            FreeTrackedBuffer(context, policyAddress);
        }
    }

    [Fact]
    public void GetschedparamValidatesThreadAndOutputs()
    {
        var policy = new byte[sizeof(int)];
        var parameter = new byte[sizeof(int)];
        var context = CreateContext(policy, parameter);

        SetGetArguments(context, thread: 0, PolicyAddress, ParameterAddress);
        Assert.Equal(
            (int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT,
            KernelPthreadExtendedCompatExports.PthreadGetschedparam(context));

        SetGetArguments(context, thread: 0x7500, policyAddress: 0, ParameterAddress);
        Assert.Equal(
            (int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT,
            KernelPthreadExtendedCompatExports.PthreadGetschedparam(context));

        SetGetArguments(context, thread: 0x7500, PolicyAddress, parameterAddress: 0xDEAD_0000);
        Assert.Equal(
            (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT,
            KernelPthreadExtendedCompatExports.PthreadGetschedparam(context));
    }

    [Theory]
    [InlineData(nameof(KernelPthreadExtendedCompatExports.PthreadGetschedparam), "P41kTWUS3EI", "scePthreadGetschedparam")]
    [InlineData(nameof(KernelPthreadExtendedCompatExports.PosixPthreadGetschedparam), "FIs3-UQT9sg", "pthread_getschedparam")]
    [InlineData(nameof(KernelPthreadExtendedCompatExports.PthreadSetschedparam), "oIRFTjoILbg", "scePthreadSetschedparam")]
    [InlineData(nameof(KernelPthreadExtendedCompatExports.PosixPthreadSetschedparam), "Xs9hdiD7sAA", "pthread_setschedparam")]
    public void SchedulingExportMetadataIsExact(string methodName, string nid, string exportName)
    {
        var method = typeof(KernelPthreadExtendedCompatExports).GetMethod(
            methodName,
            BindingFlags.Public | BindingFlags.Static);
        var attribute = method?.GetCustomAttribute<SysAbiExportAttribute>();

        Assert.NotNull(attribute);
        Assert.Equal(nid, attribute.Nid);
        Assert.Equal(exportName, attribute.ExportName);
        Assert.Equal("libKernel", attribute.LibraryName);
        Assert.Equal(Generation.Gen4 | Generation.Gen5, attribute.Target);
    }

    private static CpuContext CreateContext(
        byte[] policy,
        byte[] parameter,
        byte[]? priority = null)
    {
        var memory = new FakeGuestMemory();
        memory.AddRegion(PolicyAddress, policy);
        memory.AddRegion(ParameterAddress, parameter);
        if (priority is not null)
        {
            memory.AddRegion(PriorityAddress, priority);
        }

        return new CpuContext(memory, Generation.Gen5);
    }

    private static void SetGetArguments(
        CpuContext context,
        ulong thread,
        ulong policyAddress,
        ulong parameterAddress)
    {
        context[CpuRegister.Rdi] = thread;
        context[CpuRegister.Rsi] = policyAddress;
        context[CpuRegister.Rdx] = parameterAddress;
    }

    private static ulong AllocateTrackedBuffer(CpuContext context)
    {
        context[CpuRegister.Rdi] = sizeof(int);
        Assert.Equal(
            (int)OrbisGen2Result.ORBIS_GEN2_OK,
            KernelMemoryCompatExports.Malloc(context));
        Assert.NotEqual(0UL, context[CpuRegister.Rax]);
        return context[CpuRegister.Rax];
    }

    private static void FreeTrackedBuffer(CpuContext context, ulong address)
    {
        context[CpuRegister.Rdi] = address;
        _ = KernelMemoryCompatExports.Free(context);
    }
}
