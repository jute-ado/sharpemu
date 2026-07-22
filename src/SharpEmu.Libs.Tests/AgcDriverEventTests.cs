// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using System.Reflection;
using SharpEmu.HLE;
using SharpEmu.Libs.Agc;
using SharpEmu.Libs.Kernel;
using Xunit;

namespace SharpEmu.Libs.Tests;

public sealed class AgcDriverEventTests
{
    private const ulong EventAddress = 0x1000;

    [Theory]
    [InlineData(0UL)]
    [InlineData(EventAddress)]
    public void EventAccessorsReturnZeroForMissingEvent(ulong eventAddress)
    {
        var context = new CpuContext(new FakeGuestMemory(), Generation.Gen5);
        context[CpuRegister.Rdi] = eventAddress;
        context[CpuRegister.Rax] = ulong.MaxValue;

        Assert.Equal(0, AgcExports.DriverGetEqEventType(context));
        Assert.Equal(0UL, context[CpuRegister.Rax]);

        context[CpuRegister.Rax] = ulong.MaxValue;
        Assert.Equal(0, AgcExports.DriverGetEqContextId(context));
        Assert.Equal(0UL, context[CpuRegister.Rax]);
    }

    [Fact]
    public void GraphicsEventUsesIdentifierForTypeAndDataForContextId()
    {
        const ulong ident = 0x1122_3344_F123_4567;
        const ulong data = 0x8877_6655_89AB_CDEF;
        var context = CreateContext(ident, KernelEventQueueCompatExports.KernelEventFilterGraphics, data);

        Assert.Equal(unchecked((int)ident), AgcExports.DriverGetEqEventType(context));
        Assert.Equal(unchecked((ulong)(int)ident), context[CpuRegister.Rax]);

        Assert.Equal(unchecked((int)(uint)data), AgcExports.DriverGetEqContextId(context));
        Assert.Equal(unchecked((uint)data), context[CpuRegister.Rax]);
    }

    [Fact]
    public void NonGraphicsEventUsesDataForTypeAndIdentifierForContextId()
    {
        const ulong ident = 0x1122_3344_89AB_CDEF;
        const ulong data = 0x8877_6655_F123_4567;
        var context = CreateContext(ident, KernelEventQueueCompatExports.KernelEventFilterUser, data);

        Assert.Equal(unchecked((int)data), AgcExports.DriverGetEqEventType(context));
        Assert.Equal(unchecked((ulong)(int)data), context[CpuRegister.Rax]);

        Assert.Equal(unchecked((int)(uint)ident), AgcExports.DriverGetEqContextId(context));
        Assert.Equal(unchecked((uint)ident), context[CpuRegister.Rax]);
    }

    [Theory]
    [InlineData(nameof(AgcExports.DriverGetEqEventType), "5CdQTZIQPxM", "sceAgcDriverGetEqEventType")]
    [InlineData(nameof(AgcExports.DriverGetEqContextId), "Zw7uUVPulbw", "sceAgcDriverGetEqContextId")]
    public void EventAccessorExportsHaveExpectedMetadata(
        string methodName,
        string expectedNid,
        string expectedExportName)
    {
        var method = typeof(AgcExports).GetMethod(methodName, BindingFlags.Public | BindingFlags.Static);
        var export = Assert.Single(method!.GetCustomAttributes<SysAbiExportAttribute>());

        Assert.Equal(expectedNid, export.Nid);
        Assert.Equal(expectedExportName, export.ExportName);
        Assert.Equal(Generation.Gen5, export.Target);
        Assert.Equal("libSceAgcDriver", export.LibraryName);
    }

    private static CpuContext CreateContext(ulong ident, short filter, ulong data)
    {
        var eventBytes = new byte[0x20];
        BinaryPrimitives.WriteUInt64LittleEndian(eventBytes.AsSpan(0x00), ident);
        BinaryPrimitives.WriteInt16LittleEndian(eventBytes.AsSpan(0x08), filter);
        BinaryPrimitives.WriteUInt64LittleEndian(eventBytes.AsSpan(0x10), data);

        var memory = new FakeGuestMemory();
        memory.AddRegion(EventAddress, eventBytes);
        var context = new CpuContext(memory, Generation.Gen5);
        context[CpuRegister.Rdi] = EventAddress;
        return context;
    }
}
