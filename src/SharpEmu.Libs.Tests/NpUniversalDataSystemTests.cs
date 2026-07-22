// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Text;
using SharpEmu.HLE;
using SharpEmu.Libs.Np;
using Xunit;

namespace SharpEmu.Libs.Tests;

public sealed class NpUniversalDataSystemTests
{
    private const int InvalidArgument = unchecked((int)0x80553102);
    private const ulong ArrayAddress = 0x1000;
    private const ulong ValueAddress = 0x2000;
    private const ulong KeyAddress = 0x3000;
    private const ulong EventNameAddress = 0x4000;
    private const ulong EventOutAddress = 0x5000;
    private const ulong PropertyOutAddress = 0x6000;
    private const ulong SerializedEventAddress = 0x7000;
    private const ulong SerializedSizeAddress = 0x8000;
    private const ulong SerializedBufferAddress = 0x9000;

    [Fact]
    public void ArraySetStringValidatesDestinationAndBoundedString()
    {
        var memory = new FakeGuestMemory();
        memory.AddRegion(ArrayAddress, new byte[1]);
        memory.AddRegion(ValueAddress, Encoding.UTF8.GetBytes("telemetry\0"));
        var context = new CpuContext(memory, Generation.Gen5);
        context[CpuRegister.Rdi] = ArrayAddress;
        context[CpuRegister.Rsi] = ValueAddress;

        Assert.Equal(
            0,
            NpUniversalDataSystemExports
                .NpUniversalDataSystemEventPropertyArraySetString(context));
        Assert.Equal(0UL, context[CpuRegister.Rax]);

        context[CpuRegister.Rdi] = 0;
        Assert.Equal(
            InvalidArgument,
            NpUniversalDataSystemExports
                .NpUniversalDataSystemEventPropertyArraySetString(context));
        Assert.Equal(unchecked((ulong)InvalidArgument), context[CpuRegister.Rax]);

        context[CpuRegister.Rdi] = ArrayAddress;
        context[CpuRegister.Rsi] = 0x3000;
        Assert.Equal(
            (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT,
            NpUniversalDataSystemExports
                .NpUniversalDataSystemEventPropertyArraySetString(context));
    }

    [Fact]
    public void CreateEventWritesDistinctMappedEventAndPropertyPointers()
    {
        var memory = new FakeGuestMemory();
        memory.AddRegion(
            EventNameAddress,
            Encoding.UTF8.GetBytes("gameplay\0"));
        memory.AddRegion(EventOutAddress, new byte[sizeof(ulong)]);
        memory.AddRegion(PropertyOutAddress, new byte[sizeof(ulong)]);
        var context = new CpuContext(memory, Generation.Gen5);
        context[CpuRegister.Rdi] = EventNameAddress;
        context[CpuRegister.Rdx] = EventOutAddress;
        context[CpuRegister.Rcx] = PropertyOutAddress;

        Assert.Equal(
            0,
            NpUniversalDataSystemExports
                .NpUniversalDataSystemCreateEvent(context));
        Assert.True(context.TryReadUInt64(EventOutAddress, out var eventAddress));
        Assert.True(
            context.TryReadUInt64(
                PropertyOutAddress,
                out var propertyAddress));
        Assert.NotEqual(0UL, eventAddress);
        Assert.NotEqual(0UL, propertyAddress);
        Assert.NotEqual(eventAddress, propertyAddress);
        Span<byte> probe = stackalloc byte[1];
        Assert.True(memory.TryRead(eventAddress, probe));
        Assert.True(memory.TryRead(propertyAddress, probe));
    }

    [Fact]
    public void CreateEventPropertyObjectWritesAUsableOpaqueObject()
    {
        var memory = new FakeGuestMemory();
        memory.AddRegion(PropertyOutAddress, new byte[sizeof(ulong)]);
        var context = new CpuContext(memory, Generation.Gen5);
        context[CpuRegister.Rdi] = PropertyOutAddress;

        Assert.Equal(
            0,
            NpUniversalDataSystemExports
                .NpUniversalDataSystemCreateEventPropertyObject(context));
        Assert.True(
            context.TryReadUInt64(
                PropertyOutAddress,
                out var propertyAddress));
        Assert.NotEqual(0UL, propertyAddress);
        Span<byte> probe = stackalloc byte[1];
        Assert.True(memory.TryRead(propertyAddress, probe));

        context[CpuRegister.Rdi] = 0;
        Assert.Equal(
            InvalidArgument,
            NpUniversalDataSystemExports
                .NpUniversalDataSystemCreateEventPropertyObject(context));
    }

    [Fact]
    public void DestroyEventPropertyObjectReleasesCreatedObjectAndValidatesAddress()
    {
        var memory = new FakeGuestMemory();
        memory.AddRegion(PropertyOutAddress, new byte[sizeof(ulong)]);
        var context = new CpuContext(memory, Generation.Gen5);
        context[CpuRegister.Rdi] = PropertyOutAddress;

        Assert.Equal(
            0,
            NpUniversalDataSystemExports
                .NpUniversalDataSystemCreateEventPropertyObject(context));
        Assert.True(context.TryReadUInt64(PropertyOutAddress, out var propertyAddress));
        Assert.Equal(1, memory.GuestAllocationCount);

        context[CpuRegister.Rdi] = propertyAddress;
        Assert.Equal(
            0,
            NpUniversalDataSystemExports
                .NpUniversalDataSystemDestroyEventPropertyObject(context));
        Assert.Equal(0, memory.GuestAllocationCount);
        Span<byte> probe = stackalloc byte[1];
        Assert.False(memory.TryRead(propertyAddress, probe));

        context[CpuRegister.Rdi] = 0;
        Assert.Equal(
            InvalidArgument,
            NpUniversalDataSystemExports
                .NpUniversalDataSystemDestroyEventPropertyObject(context));

        context[CpuRegister.Rdi] = 0xDEAD_0000;
        Assert.Equal(
            (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT,
            NpUniversalDataSystemExports
                .NpUniversalDataSystemDestroyEventPropertyObject(context));
    }

    [Fact]
    public void CreateHandleUsesOnlyItsDocumentedOutputPointer()
    {
        var memory = new FakeGuestMemory();
        memory.AddRegion(EventOutAddress, new byte[sizeof(int)]);
        var context = new CpuContext(memory, Generation.Gen5);
        context[CpuRegister.Rdi] = EventOutAddress;

        Assert.Equal(
            0,
            NpUniversalDataSystemExports
                .NpUniversalDataSystemCreateHandle(context));
        Assert.True(context.TryReadInt32(EventOutAddress, out var handle));
        Assert.True(handle > 0);

        context[CpuRegister.Rdi] = 0;
        context[CpuRegister.Rsi] = EventOutAddress;
        Assert.Equal(
            InvalidArgument,
            NpUniversalDataSystemExports
                .NpUniversalDataSystemCreateHandle(context));
    }

    [Fact]
    public void ObjectSetArrayCreatesAUsableArrayWhenValueIsNull()
    {
        var memory = new FakeGuestMemory();
        memory.AddRegion(ArrayAddress, new byte[1]);
        memory.AddRegion(KeyAddress, Encoding.UTF8.GetBytes("items\0"));
        memory.AddRegion(ValueAddress, Encoding.UTF8.GetBytes("entry\0"));
        memory.AddRegion(PropertyOutAddress, new byte[sizeof(ulong)]);
        var context = new CpuContext(memory, Generation.Gen5);
        context[CpuRegister.Rdi] = ArrayAddress;
        context[CpuRegister.Rsi] = KeyAddress;
        context[CpuRegister.Rcx] = PropertyOutAddress;

        Assert.Equal(
            0,
            NpUniversalDataSystemExports
                .NpUniversalDataSystemEventPropertyObjectSetArray(context));
        Assert.True(
            context.TryReadUInt64(
                PropertyOutAddress,
                out var createdArrayAddress));
        Assert.NotEqual(0UL, createdArrayAddress);

        context[CpuRegister.Rdi] = createdArrayAddress;
        context[CpuRegister.Rsi] = ValueAddress;
        Assert.Equal(
            0,
            NpUniversalDataSystemExports
                .NpUniversalDataSystemEventPropertyArraySetString(context));
    }

    [Fact]
    public void ObjectSetStringValidatesTheObjectKeyAndValueAbi()
    {
        var memory = new FakeGuestMemory();
        memory.AddRegion(ArrayAddress, new byte[1]);
        memory.AddRegion(KeyAddress, Encoding.UTF8.GetBytes("key\0"));
        memory.AddRegion(ValueAddress, Encoding.UTF8.GetBytes("value\0"));
        var context = new CpuContext(memory, Generation.Gen5);
        context[CpuRegister.Rdi] = ArrayAddress;
        context[CpuRegister.Rsi] = KeyAddress;
        context[CpuRegister.Rdx] = ValueAddress;

        Assert.Equal(
            0,
            NpUniversalDataSystemExports
                .NpUniversalDataSystemEventPropertyObjectSetString(context));

        context[CpuRegister.Rdi] = 0;
        Assert.Equal(
            InvalidArgument,
            NpUniversalDataSystemExports
                .NpUniversalDataSystemEventPropertyObjectSetString(context));
    }

    [Fact]
    public void ArraySetStringExportMetadataIsExact()
    {
        ExportMetadataAssert.Exact(
            "4llLk7YJRTE",
            "sceNpUniversalDataSystemEventPropertyArraySetString",
            "libSceNpUniversalDataSystem",
            Generation.Gen4 | Generation.Gen5);
    }

    [Fact]
    public void CreateEventPropertyArrayExportMetadataIsExact()
    {
        ExportMetadataAssert.Exact(
            "Hm7qubT3b70",
            "sceNpUniversalDataSystemCreateEventPropertyArray",
            "libSceNpUniversalDataSystem",
            Generation.Gen4 | Generation.Gen5);
    }

    [Fact]
    public void CreateEventPropertyObjectExportMetadataIsExact()
    {
        ExportMetadataAssert.Exact(
            "s6W4Zl4Slgk",
            "sceNpUniversalDataSystemCreateEventPropertyObject",
            "libSceNpUniversalDataSystem",
            Generation.Gen4 | Generation.Gen5);
    }

    [Fact]
    public void DestroyEventPropertyObjectExportMetadataIsExact()
    {
        ExportMetadataAssert.Exact(
            "kKUH0Viib3c",
            "sceNpUniversalDataSystemDestroyEventPropertyObject",
            "libSceNpUniversalDataSystem",
            Generation.Gen4 | Generation.Gen5);
    }

    [Fact]
    public void EventSerializationReportsRequiredSizeAndWritesJson()
    {
        var memory = new FakeGuestMemory();
        memory.AddRegion(SerializedEventAddress, new byte[1]);
        memory.AddRegion(SerializedSizeAddress, new byte[sizeof(ulong)]);
        memory.AddRegion(SerializedBufferAddress, new byte[3]);
        var context = new CpuContext(memory, Generation.Gen5);
        context[CpuRegister.Rdi] = SerializedEventAddress;
        context[CpuRegister.Rsi] = SerializedSizeAddress;

        Assert.Equal(
            0,
            NpUniversalDataSystemExports
                .NpUniversalDataSystemEventEstimateSize(context));
        Assert.True(
            context.TryReadUInt64(SerializedSizeAddress, out var estimatedSize));
        Assert.Equal(3UL, estimatedSize);

        context[CpuRegister.Rsi] = SerializedBufferAddress;
        context[CpuRegister.Rdx] = 3;
        context[CpuRegister.Rcx] = SerializedSizeAddress;
        Assert.Equal(
            0,
            NpUniversalDataSystemExports
                .NpUniversalDataSystemEventToString(context));
        Span<byte> serialized = stackalloc byte[3];
        Assert.True(memory.TryRead(SerializedBufferAddress, serialized));
        Assert.Equal("{}\0"u8.ToArray(), serialized.ToArray());
        Assert.True(
            context.TryReadUInt64(SerializedSizeAddress, out var serializedSize));
        Assert.Equal(3UL, serializedSize);
    }

    [Fact]
    public void EventToStringBoundsWritesAndSupportsSizeOnlyQueries()
    {
        var memory = new FakeGuestMemory();
        memory.AddRegion(SerializedEventAddress, new byte[1]);
        memory.AddRegion(SerializedSizeAddress, new byte[sizeof(ulong)]);
        memory.AddRegion(SerializedBufferAddress, [0xCC, 0xCC, 0xCC]);
        var context = new CpuContext(memory, Generation.Gen5);
        context[CpuRegister.Rdi] = SerializedEventAddress;
        context[CpuRegister.Rsi] = SerializedBufferAddress;
        context[CpuRegister.Rdx] = 2;
        context[CpuRegister.Rcx] = SerializedSizeAddress;

        Assert.Equal(
            0,
            NpUniversalDataSystemExports
                .NpUniversalDataSystemEventToString(context));
        Span<byte> bounded = stackalloc byte[3];
        Assert.True(memory.TryRead(SerializedBufferAddress, bounded));
        Assert.Equal(new byte[] { (byte)'{', 0, 0xCC }, bounded.ToArray());

        context[CpuRegister.Rsi] = 0;
        context[CpuRegister.Rdx] = 0;
        Assert.Equal(
            0,
            NpUniversalDataSystemExports
                .NpUniversalDataSystemEventToString(context));
        Assert.True(
            context.TryReadUInt64(SerializedSizeAddress, out var requiredSize));
        Assert.Equal(3UL, requiredSize);
    }

    [Theory]
    [InlineData("+s14jq-KGYw", "sceNpUniversalDataSystemEventEstimateSize")]
    [InlineData("vj6CQGWtEBg", "sceNpUniversalDataSystemEventToString")]
    public void EventSerializationExportMetadataIsExact(string nid, string name)
    {
        ExportMetadataAssert.Exact(
            nid,
            name,
            "libSceNpUniversalDataSystem",
            Generation.Gen4 | Generation.Gen5);
    }
}
