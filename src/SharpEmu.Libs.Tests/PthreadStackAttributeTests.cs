// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using SharpEmu.Libs.Kernel;
using Xunit;

namespace SharpEmu.Libs.Tests;

[Collection(PthreadRuntimeStateCollection.Name)]
public sealed class PthreadStackAttributeTests
{
    private const ulong StackStart = 0x70_0000;
    private const ulong StackSize = 0x20_0000;
    private const ulong AttrAddress = 0x10_0000;
    private const ulong StackAddressOut = 0x11_0000;
    private const ulong StackSizeOut = 0x12_0000;

    [Fact]
    public void AttrGetHydratesCurrentGuestThreadStackRange()
    {
        var memory = new FakeGuestMemory();
        memory.AddRegion(StackStart, new byte[StackSize]);
        memory.AddRegion(AttrAddress, new byte[sizeof(ulong)]);
        memory.AddRegion(StackAddressOut, new byte[sizeof(ulong)]);
        memory.AddRegion(StackSizeOut, new byte[sizeof(ulong)]);
        memory.RegisterStackRange(StackStart, StackSize);
        var context = new CpuContext(memory, Generation.Gen5);
        context[CpuRegister.Rsp] = StackStart + StackSize - 0x80;
        var thread = KernelPthreadState.CreateThreadHandle("current-stack-test");
        var previousThread = GuestThreadExecution.EnterGuestThread(thread);

        KernelPthreadLifecycle.ResetRuntimeState();
        try
        {
            context[CpuRegister.Rdi] = thread;
            context[CpuRegister.Rsi] = AttrAddress;
            Assert.Equal(
                (int)OrbisGen2Result.ORBIS_GEN2_OK,
                KernelPthreadExtendedCompatExports.PthreadAttrGet(context));

            context[CpuRegister.Rdi] = AttrAddress;
            context[CpuRegister.Rsi] = StackAddressOut;
            Assert.Equal(
                (int)OrbisGen2Result.ORBIS_GEN2_OK,
                KernelPthreadExtendedCompatExports.PthreadAttrGetstackaddr(context));

            context[CpuRegister.Rsi] = StackSizeOut;
            Assert.Equal(
                (int)OrbisGen2Result.ORBIS_GEN2_OK,
                KernelPthreadExtendedCompatExports.PthreadAttrGetstacksize(context));

            Assert.True(context.TryReadUInt64(StackAddressOut, out var stackAddress));
            Assert.True(context.TryReadUInt64(StackSizeOut, out var stackSize));
            Assert.Equal(StackStart, stackAddress);
            Assert.Equal(StackSize, stackSize);
        }
        finally
        {
            GuestThreadExecution.RestoreGuestThread(previousThread);
            KernelPthreadExtendedCompatExports.ReleaseThreadState(thread);
            _ = KernelPthreadState.ReleaseThreadHandle(thread);
            KernelPthreadLifecycle.ResetRuntimeState();
        }
    }

    [Fact]
    public void AttrGetDoesNotApplyCurrentStackRangeToAnotherThread()
    {
        var memory = new FakeGuestMemory();
        memory.AddRegion(StackStart, new byte[StackSize]);
        memory.AddRegion(AttrAddress, new byte[sizeof(ulong)]);
        memory.AddRegion(StackAddressOut, new byte[sizeof(ulong)]);
        memory.RegisterStackRange(StackStart, StackSize);
        var context = new CpuContext(memory, Generation.Gen5);
        context[CpuRegister.Rsp] = StackStart + StackSize - 0x80;
        var currentThread = KernelPthreadState.CreateThreadHandle("current-stack-test");
        var otherThread = KernelPthreadState.CreateThreadHandle("other-stack-test");
        var previousThread = GuestThreadExecution.EnterGuestThread(currentThread);

        KernelPthreadLifecycle.ResetRuntimeState();
        try
        {
            context[CpuRegister.Rdi] = otherThread;
            context[CpuRegister.Rsi] = AttrAddress;
            Assert.Equal(
                (int)OrbisGen2Result.ORBIS_GEN2_OK,
                KernelPthreadExtendedCompatExports.PthreadAttrGet(context));

            context[CpuRegister.Rdi] = AttrAddress;
            context[CpuRegister.Rsi] = StackAddressOut;
            Assert.Equal(
                (int)OrbisGen2Result.ORBIS_GEN2_OK,
                KernelPthreadExtendedCompatExports.PthreadAttrGetstackaddr(context));

            Assert.True(context.TryReadUInt64(StackAddressOut, out var stackAddress));
            Assert.Equal(0UL, stackAddress);
        }
        finally
        {
            GuestThreadExecution.RestoreGuestThread(previousThread);
            KernelPthreadExtendedCompatExports.ReleaseThreadState(currentThread);
            KernelPthreadExtendedCompatExports.ReleaseThreadState(otherThread);
            _ = KernelPthreadState.ReleaseThreadHandle(currentThread);
            _ = KernelPthreadState.ReleaseThreadHandle(otherThread);
            KernelPthreadLifecycle.ResetRuntimeState();
        }
    }
}
