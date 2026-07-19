// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using SharpEmu.HLE;
using SharpEmu.Libs.Kernel;
using SharpEmu.Libs.VideoOut;
using Xunit;

namespace SharpEmu.Libs.Tests;

public sealed class VideoOutOutputModeEventTests
{
    private const ulong HandleAddress = 0x1000;
    private const ulong EventAddress = 0x2000;
    private const ulong CountAddress = 0x3000;
    private const ulong TimeoutAddress = 0x4000;
    private const ulong OutputModeEventId = 8;
    private const short VideoOutFilter = -13;
    private const ulong UserData = 0x1234_5678_9ABC_DEF0;
    private const int OrbisVideoOutErrorInvalidHandle = unchecked((int)0x8029000B);
    private const int OrbisVideoOutErrorInvalidEventQueue = unchecked((int)0x8029000C);

    [Fact]
    public void AddOutputModeEventPublishesCurrentAndConfiguredModes()
    {
        var fixture = CreateFixture();
        try
        {
            fixture.Context[CpuRegister.Rdi] = fixture.Equeue;
            fixture.Context[CpuRegister.Rsi] = unchecked((ulong)fixture.VideoOutHandle);
            fixture.Context[CpuRegister.Rdx] = UserData;

            Assert.Equal(
                (int)OrbisGen2Result.ORBIS_GEN2_OK,
                VideoOutExports.VideoOutAddOutputModeEvent(fixture.Context));
            AssertOutputModeEvent(fixture, expectedMode: 1);

            fixture.Context[CpuRegister.Rdi] = unchecked((ulong)fixture.VideoOutHandle);
            fixture.Context[CpuRegister.Rsi] = 1;
            fixture.Context[CpuRegister.Rdx] = 0;
            fixture.Context[CpuRegister.Rcx] = 0;
            fixture.Context[CpuRegister.R8] = 0;

            Assert.Equal(
                (int)OrbisGen2Result.ORBIS_GEN2_OK,
                VideoOutExports.VideoOutConfigureOutput(fixture.Context));
            AssertOutputModeEvent(fixture, expectedMode: 1);
        }
        finally
        {
            fixture.Dispose();
        }
    }

    [Fact]
    public void AddOutputModeEventRejectsUnknownPortBeforeQueue()
    {
        var context = new CpuContext(new FakeGuestMemory(), Generation.Gen5);
        context[CpuRegister.Rdi] = ulong.MaxValue;
        context[CpuRegister.Rsi] = int.MaxValue;

        Assert.Equal(
            OrbisVideoOutErrorInvalidHandle,
            VideoOutExports.VideoOutAddOutputModeEvent(context));
    }

    [Fact]
    public void AddOutputModeEventRejectsUnknownQueue()
    {
        var context = new CpuContext(new FakeGuestMemory(), Generation.Gen5);
        context[CpuRegister.Rdi] = ulong.MaxValue;
        context[CpuRegister.Rsi] = unchecked((ulong)OpenVideoOut(context));
        try
        {
            Assert.Equal(
                OrbisVideoOutErrorInvalidEventQueue,
                VideoOutExports.VideoOutAddOutputModeEvent(context));
        }
        finally
        {
            CloseVideoOut(context, unchecked((int)context[CpuRegister.Rsi]));
        }
    }

    [Fact]
    public void AddOutputModeEventExportMetadataIsExact()
    {
        ExportMetadataAssert.Exact(
            "kmSe30JTs+E",
            "sceVideoOutAddOutputModeEvent",
            "libSceVideoOut",
            Generation.Gen5);
    }

    private static Fixture CreateFixture()
    {
        var handleBytes = new byte[sizeof(ulong)];
        var eventBytes = new byte[0x20];
        var countBytes = new byte[sizeof(uint)];
        var timeoutBytes = new byte[sizeof(uint)];
        var memory = new FakeGuestMemory();
        memory.AddRegion(HandleAddress, handleBytes);
        memory.AddRegion(EventAddress, eventBytes);
        memory.AddRegion(CountAddress, countBytes);
        memory.AddRegion(TimeoutAddress, timeoutBytes);
        var context = new CpuContext(memory, Generation.Gen5);

        context[CpuRegister.Rdi] = HandleAddress;
        Assert.Equal(
            (int)OrbisGen2Result.ORBIS_GEN2_OK,
            KernelEventQueueCompatExports.KernelCreateEqueue(context));
        var equeue = BinaryPrimitives.ReadUInt64LittleEndian(handleBytes);
        var videoOutHandle = OpenVideoOut(context);

        return new Fixture(
            context,
            equeue,
            videoOutHandle,
            eventBytes,
            countBytes);
    }

    private static int OpenVideoOut(CpuContext context)
    {
        context[CpuRegister.Rdi] = 0;
        context[CpuRegister.Rsi] = 0;
        context[CpuRegister.Rdx] = 0;
        var handle = VideoOutExports.VideoOutOpen(context);
        Assert.True(handle > 0);
        return handle;
    }

    private static void CloseVideoOut(CpuContext context, int handle)
    {
        context[CpuRegister.Rdi] = unchecked((ulong)handle);
        _ = VideoOutExports.VideoOutClose(context);
    }

    private static void AssertOutputModeEvent(Fixture fixture, ulong expectedMode)
    {
        fixture.Context[CpuRegister.Rdi] = fixture.Equeue;
        fixture.Context[CpuRegister.Rsi] = EventAddress;
        fixture.Context[CpuRegister.Rdx] = 1;
        fixture.Context[CpuRegister.Rcx] = CountAddress;
        fixture.Context[CpuRegister.R8] = TimeoutAddress;

        Assert.Equal(
            (int)OrbisGen2Result.ORBIS_GEN2_OK,
            KernelEventQueueCompatExports.KernelWaitEqueue(fixture.Context));
        Assert.Equal(1u, BinaryPrimitives.ReadUInt32LittleEndian(fixture.CountBytes));
        Assert.Equal(
            OutputModeEventId,
            BinaryPrimitives.ReadUInt64LittleEndian(fixture.EventBytes));
        Assert.Equal(
            VideoOutFilter,
            BinaryPrimitives.ReadInt16LittleEndian(fixture.EventBytes.AsSpan(0x08)));
        Assert.Equal(
            expectedMode,
            BinaryPrimitives.ReadUInt64LittleEndian(fixture.EventBytes.AsSpan(0x10)) >> 16);
        Assert.Equal(
            UserData,
            BinaryPrimitives.ReadUInt64LittleEndian(fixture.EventBytes.AsSpan(0x18)));
    }

    private sealed record Fixture(
        CpuContext Context,
        ulong Equeue,
        int VideoOutHandle,
        byte[] EventBytes,
        byte[] CountBytes) : IDisposable
    {
        public void Dispose()
        {
            CloseVideoOut(Context, VideoOutHandle);
            Context[CpuRegister.Rdi] = Equeue;
            _ = KernelEventQueueCompatExports.KernelDeleteEqueue(Context);
        }
    }
}
