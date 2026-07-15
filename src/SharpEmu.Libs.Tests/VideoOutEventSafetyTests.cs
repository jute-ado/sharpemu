// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using SharpEmu.HLE;
using SharpEmu.Libs.VideoOut;
using Xunit;

namespace SharpEmu.Libs.Tests;

public sealed class VideoOutEventSafetyTests
{
    private const ulong EventAddress = 0x1000;
    private const ulong DataAddress = 0x2000;
    private const short VideoOutFilter = -13;
    private const ulong VblankEventId = 0x5;
    private const ulong FlipEventId = 0x6;

    [Fact]
    public void GetEventIdReadsCompleteEventFields()
    {
        var eventRecord = new byte[0x0A];
        BinaryPrimitives.WriteUInt64LittleEndian(eventRecord, VblankEventId);
        BinaryPrimitives.WriteInt16LittleEndian(eventRecord.AsSpan(0x08), VideoOutFilter);
        var memory = new FakeGuestMemory();
        memory.AddRegion(EventAddress, eventRecord);
        var context = CreateContext(memory, EventAddress);

        Assert.Equal(0, VideoOutExports.VideoOutGetEventId(context));
    }

    [Fact]
    public void GetEventIdRejectsWrappedEventFields()
    {
        var ident = new byte[sizeof(ulong)];
        BinaryPrimitives.WriteUInt64LittleEndian(ident, VblankEventId);
        var filter = new byte[sizeof(short)];
        BinaryPrimitives.WriteInt16LittleEndian(filter, VideoOutFilter);
        var memory = new FakeGuestMemory();
        memory.AddRegion(ulong.MaxValue - 7, ident);
        memory.AddRegion(0, filter);
        var context = CreateContext(memory, ulong.MaxValue - 7);

        Assert.Equal(
            (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT,
            VideoOutExports.VideoOutGetEventId(context));
    }

    [Fact]
    public void GetEventDataReadsCompleteEventFields()
    {
        var eventRecord = new byte[0x18];
        BinaryPrimitives.WriteUInt64LittleEndian(eventRecord, FlipEventId);
        BinaryPrimitives.WriteInt16LittleEndian(eventRecord.AsSpan(0x08), VideoOutFilter);
        BinaryPrimitives.WriteUInt64LittleEndian(eventRecord.AsSpan(0x10), 0x1234_0000);
        var output = new byte[sizeof(ulong)];
        var memory = new FakeGuestMemory();
        memory.AddRegion(EventAddress, eventRecord);
        memory.AddRegion(DataAddress, output);
        var context = CreateContext(memory, EventAddress, DataAddress);

        Assert.Equal(
            (int)OrbisGen2Result.ORBIS_GEN2_OK,
            VideoOutExports.VideoOutGetEventData(context));
        Assert.Equal(0x1234ul, BinaryPrimitives.ReadUInt64LittleEndian(output));
    }

    [Fact]
    public void GetEventDataRejectsWrappedEventFieldsBeforeOutputMutation()
    {
        var ident = new byte[sizeof(ulong)];
        BinaryPrimitives.WriteUInt64LittleEndian(ident, FlipEventId);
        var wrappedFields = new byte[0x10];
        BinaryPrimitives.WriteInt16LittleEndian(wrappedFields, VideoOutFilter);
        BinaryPrimitives.WriteUInt64LittleEndian(wrappedFields.AsSpan(0x08), 0x1234_0000);
        var output = Enumerable.Repeat((byte)0xCC, sizeof(ulong)).ToArray();
        var memory = new FakeGuestMemory();
        memory.AddRegion(ulong.MaxValue - 7, ident);
        memory.AddRegion(0, wrappedFields);
        memory.AddRegion(DataAddress, output);
        var context = CreateContext(memory, ulong.MaxValue - 7, DataAddress);

        Assert.Equal(
            (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT,
            VideoOutExports.VideoOutGetEventData(context));
        Assert.All(output, value => Assert.Equal(0xCC, value));
    }

    private static CpuContext CreateContext(
        FakeGuestMemory memory,
        ulong eventAddress,
        ulong dataAddress = 0)
    {
        var context = new CpuContext(memory, Generation.Gen5);
        context[CpuRegister.Rdi] = eventAddress;
        context[CpuRegister.Rsi] = dataAddress;
        return context;
    }
}
