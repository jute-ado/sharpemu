// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using System.Text;
using SharpEmu.HLE;
using SharpEmu.Libs.Kernel;
using Xunit;

namespace SharpEmu.Libs.Tests;

public sealed class KernelEventFlagLifecycleTests
{
    private const ulong NameAddress = 0x1000;
    private const ulong HandleAddress = 0x2000;
    private const ulong ResultAddress = 0x3000;
    private const ulong WaiterCountAddress = 0x4000;

    [Fact]
    public void DeleteReleasesCooperativeWaitWithDeletedResult()
    {
        var memory = new FakeGuestMemory();
        memory.AddRegion(NameAddress, Encoding.UTF8.GetBytes("delete-wake\0"));
        var handleBytes = new byte[sizeof(ulong)];
        var resultBytes = new byte[sizeof(ulong)];
        memory.AddRegion(HandleAddress, handleBytes);
        memory.AddRegion(ResultAddress, resultBytes);
        var context = new CpuContext(memory, Generation.Gen5);
        context[CpuRegister.Rdi] = HandleAddress;
        context[CpuRegister.Rsi] = NameAddress;
        context[CpuRegister.Rdx] = 0;
        context[CpuRegister.Rcx] = 0;
        context[CpuRegister.R8] = 0;
        Assert.Equal(
            (int)OrbisGen2Result.ORBIS_GEN2_OK,
            KernelEventFlagCompatExports.KernelCreateEventFlag(context));
        var handle = BinaryPrimitives.ReadUInt64LittleEndian(handleBytes);
        var previousGuestThread = GuestThreadExecution.EnterGuestThread(0x1234);

        try
        {
            context[CpuRegister.Rdi] = handle;
            context[CpuRegister.Rsi] = 1;
            context[CpuRegister.Rdx] = 2;
            context[CpuRegister.Rcx] = ResultAddress;
            context[CpuRegister.R8] = 0;
            Assert.Equal(
                (int)OrbisGen2Result.ORBIS_GEN2_OK,
                KernelEventFlagCompatExports.KernelWaitEventFlag(context));
            Assert.True(GuestThreadExecution.TryConsumeCurrentThreadBlock(
                out var reason,
                out _,
                out _,
                out var wakeKey,
                out var resumeHandler,
                out var wakeHandler,
                out _));
            Assert.Equal("sceKernelWaitEventFlag", reason);
            Assert.Equal($"event_flag:0x{handle:X16}", wakeKey);
            Assert.NotNull(resumeHandler);
            Assert.NotNull(wakeHandler);

            context[CpuRegister.Rdi] = handle;
            Assert.Equal(
                (int)OrbisGen2Result.ORBIS_GEN2_OK,
                KernelEventFlagCompatExports.KernelDeleteEventFlag(context));
            Assert.True(wakeHandler!());
            Assert.Equal(
                (int)OrbisGen2Result.ORBIS_GEN2_ERROR_DELETED,
                resumeHandler!());
            Assert.Equal(0UL, BinaryPrimitives.ReadUInt64LittleEndian(resultBytes));
        }
        finally
        {
            GuestThreadExecution.RestoreGuestThread(previousGuestThread);
            context[CpuRegister.Rdi] = handle;
            _ = KernelEventFlagCompatExports.KernelDeleteEventFlag(context);
        }
    }

    [Fact]
    public void CancelReleasesCooperativeWaitWithCanceledResult()
    {
        var memory = new FakeGuestMemory();
        memory.AddRegion(NameAddress, Encoding.UTF8.GetBytes("cancel-wake\0"));
        var handleBytes = new byte[sizeof(ulong)];
        var resultBytes = new byte[sizeof(ulong)];
        var waiterCountBytes = new byte[sizeof(uint)];
        memory.AddRegion(HandleAddress, handleBytes);
        memory.AddRegion(ResultAddress, resultBytes);
        memory.AddRegion(WaiterCountAddress, waiterCountBytes);
        var context = new CpuContext(memory, Generation.Gen5);
        context[CpuRegister.Rdi] = HandleAddress;
        context[CpuRegister.Rsi] = NameAddress;
        context[CpuRegister.Rdx] = 0;
        context[CpuRegister.Rcx] = 0;
        context[CpuRegister.R8] = 0;
        Assert.Equal(
            (int)OrbisGen2Result.ORBIS_GEN2_OK,
            KernelEventFlagCompatExports.KernelCreateEventFlag(context));
        var handle = BinaryPrimitives.ReadUInt64LittleEndian(handleBytes);
        var previousGuestThread = GuestThreadExecution.EnterGuestThread(0x5678);

        try
        {
            context[CpuRegister.Rdi] = handle;
            context[CpuRegister.Rsi] = 1;
            context[CpuRegister.Rdx] = 2;
            context[CpuRegister.Rcx] = ResultAddress;
            context[CpuRegister.R8] = 0;
            Assert.Equal(
                (int)OrbisGen2Result.ORBIS_GEN2_OK,
                KernelEventFlagCompatExports.KernelWaitEventFlag(context));
            Assert.True(GuestThreadExecution.TryConsumeCurrentThreadBlock(
                out _,
                out _,
                out _,
                out _,
                out var resumeHandler,
                out var wakeHandler,
                out _));
            Assert.NotNull(resumeHandler);
            Assert.NotNull(wakeHandler);

            context[CpuRegister.Rdi] = handle;
            context[CpuRegister.Rsi] = 0x80;
            context[CpuRegister.Rdx] = WaiterCountAddress;
            Assert.Equal(
                (int)OrbisGen2Result.ORBIS_GEN2_OK,
                KernelEventFlagCompatExports.KernelCancelEventFlag(context));
            Assert.Equal(1u, BinaryPrimitives.ReadUInt32LittleEndian(waiterCountBytes));
            Assert.True(wakeHandler!());
            Assert.Equal(
                (int)OrbisGen2Result.ORBIS_GEN2_ERROR_CANCELED,
                resumeHandler!());
        }
        finally
        {
            GuestThreadExecution.RestoreGuestThread(previousGuestThread);
            context[CpuRegister.Rdi] = handle;
            _ = KernelEventFlagCompatExports.KernelDeleteEventFlag(context);
        }
    }
}
