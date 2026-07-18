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
}
