// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using SharpEmu.HLE;
using SharpEmu.Libs.Kernel;
using SharpEmu.Libs.VideoOut;
using Xunit;

namespace SharpEmu.Libs.Tests;

public sealed class VideoOutVrrStatusTests
{
    private const ulong HandleAddress = 0x1000;
    private const int OrbisVideoOutErrorInvalidHandle = unchecked((int)0x8029000B);
    private const int OrbisVideoOutErrorInvalidEventQueue = unchecked((int)0x8029000C);

    [Fact]
    public void AddVrrStatusFlagsPrivilegeSucceedsWithoutGuestArguments()
    {
        var context = new CpuContext(new FakeGuestMemory(), Generation.Gen5);
        context[CpuRegister.Rdi] = ulong.MaxValue;
        context[CpuRegister.Rsi] = ulong.MaxValue;
        context[CpuRegister.Rdx] = ulong.MaxValue;
        context[CpuRegister.Rcx] = ulong.MaxValue;
        context[CpuRegister.R8] = ulong.MaxValue;
        context[CpuRegister.R9] = ulong.MaxValue;

        Assert.Equal(
            (int)OrbisGen2Result.ORBIS_GEN2_OK,
            VideoOutExports.VideoOutAddVrrStatusFlagsPrivilege(context));
    }

    [Fact]
    public void AddVrrStatusFlagsPrivilegeExportMetadataIsExact()
    {
        ExportMetadataAssert.Exact(
            "kP2L8t3j-aM",
            "sceVideoOutAddVrrStatusFlagsPrivilege",
            "libSceVideoOutVrrStatus",
            Generation.Gen5);
    }

    [Fact]
    public void AddVrrActiveStatusEventRegistersKnownPortAndQueue()
    {
        var handleBytes = new byte[sizeof(ulong)];
        var memory = new FakeGuestMemory();
        memory.AddRegion(HandleAddress, handleBytes);
        var context = new CpuContext(memory, Generation.Gen5);

        context[CpuRegister.Rdi] = HandleAddress;
        Assert.Equal(
            (int)OrbisGen2Result.ORBIS_GEN2_OK,
            KernelEventQueueCompatExports.KernelCreateEqueue(context));
        var equeue = BinaryPrimitives.ReadUInt64LittleEndian(handleBytes);

        context[CpuRegister.Rdi] = 0;
        context[CpuRegister.Rsi] = 0;
        context[CpuRegister.Rdx] = 0;
        var videoOutHandle = VideoOutExports.VideoOutOpen(context);
        Assert.True(videoOutHandle > 0);

        try
        {
            context[CpuRegister.Rdi] = equeue;
            context[CpuRegister.Rsi] = unchecked((ulong)videoOutHandle);
            context[CpuRegister.Rdx] = 0x1234_5678_9ABC_DEF0;

            Assert.Equal(
                (int)OrbisGen2Result.ORBIS_GEN2_OK,
                VideoOutExports.VideoOutAddVrrActiveStatusEvent(context));
        }
        finally
        {
            context[CpuRegister.Rdi] = unchecked((ulong)videoOutHandle);
            _ = VideoOutExports.VideoOutClose(context);
            context[CpuRegister.Rdi] = equeue;
            _ = KernelEventQueueCompatExports.KernelDeleteEqueue(context);
        }
    }

    [Fact]
    public void AddVrrActiveStatusEventRejectsUnknownPortBeforeQueue()
    {
        var context = new CpuContext(new FakeGuestMemory(), Generation.Gen5);
        context[CpuRegister.Rdi] = ulong.MaxValue;
        context[CpuRegister.Rsi] = int.MaxValue;

        Assert.Equal(
            OrbisVideoOutErrorInvalidHandle,
            VideoOutExports.VideoOutAddVrrActiveStatusEvent(context));
    }

    [Fact]
    public void AddVrrActiveStatusEventRejectsUnknownQueue()
    {
        var context = new CpuContext(new FakeGuestMemory(), Generation.Gen5);
        context[CpuRegister.Rdi] = 0;
        context[CpuRegister.Rsi] = 0;
        context[CpuRegister.Rdx] = 0;
        var videoOutHandle = VideoOutExports.VideoOutOpen(context);
        Assert.True(videoOutHandle > 0);

        try
        {
            context[CpuRegister.Rdi] = ulong.MaxValue;
            context[CpuRegister.Rsi] = unchecked((ulong)videoOutHandle);

            Assert.Equal(
                OrbisVideoOutErrorInvalidEventQueue,
                VideoOutExports.VideoOutAddVrrActiveStatusEvent(context));
        }
        finally
        {
            context[CpuRegister.Rdi] = unchecked((ulong)videoOutHandle);
            _ = VideoOutExports.VideoOutClose(context);
        }
    }

    [Fact]
    public void AddVrrActiveStatusEventExportMetadataIsExact()
    {
        ExportMetadataAssert.Exact(
            "LibwuIonIBw",
            "sceVideoOutAddVrrActiveStatusEvent",
            "libSceVideoOutVrrStatus",
            Generation.Gen5);
    }
}
