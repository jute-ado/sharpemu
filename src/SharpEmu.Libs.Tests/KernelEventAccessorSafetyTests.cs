// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using SharpEmu.HLE;
using SharpEmu.Libs.Kernel;
using Xunit;

namespace SharpEmu.Libs.Tests;

public sealed class KernelEventAccessorSafetyTests
{
    private const ulong EventAddress = 0x1000;

    [Theory]
    [InlineData(EventAccessor.Filter, 0x08UL, 0x1234UL)]
    [InlineData(EventAccessor.Data, 0x10UL, 0x1122_3344_5566_7788UL)]
    [InlineData(EventAccessor.UserData, 0x18UL, 0x8877_6655_4433_2211UL)]
    public void EventAccessorsReadRepresentableFields(
        EventAccessor accessor,
        ulong offset,
        ulong value)
    {
        var field = new byte[sizeof(ulong)];
        BinaryPrimitives.WriteUInt64LittleEndian(field, value);
        var memory = new FakeGuestMemory();
        memory.AddRegion(EventAddress + offset, field);
        var context = CreateContext(memory, EventAddress);

        Assert.Equal(
            (int)OrbisGen2Result.ORBIS_GEN2_OK,
            Invoke(accessor, context));
        Assert.Equal(value, context[CpuRegister.Rax]);
    }

    [Theory]
    [InlineData(EventAccessor.Filter, 0x08UL, 0x1234UL)]
    [InlineData(EventAccessor.Data, 0x10UL, 0x1122_3344_5566_7788UL)]
    [InlineData(EventAccessor.UserData, 0x18UL, 0x8877_6655_4433_2211UL)]
    public void EventAccessorsDoNotWrapFieldsToAddressZero(
        EventAccessor accessor,
        ulong offset,
        ulong wrappedValue)
    {
        var field = new byte[sizeof(ulong)];
        BinaryPrimitives.WriteUInt64LittleEndian(field, wrappedValue);
        var memory = new FakeGuestMemory();
        memory.AddRegion(0, field);
        var context = CreateContext(memory, ulong.MaxValue - offset + 1);

        Assert.Equal(
            (int)OrbisGen2Result.ORBIS_GEN2_OK,
            Invoke(accessor, context));
        Assert.Equal(0uL, context[CpuRegister.Rax]);
    }

    private static CpuContext CreateContext(FakeGuestMemory memory, ulong eventAddress)
    {
        var context = new CpuContext(memory, Generation.Gen5);
        context[CpuRegister.Rdi] = eventAddress;
        return context;
    }

    private static int Invoke(EventAccessor accessor, CpuContext context)
        => accessor switch
        {
            EventAccessor.Filter => KernelEventQueueCompatExports.KernelGetEventFilter(context),
            EventAccessor.Data => KernelEventQueueCompatExports.KernelGetEventData(context),
            EventAccessor.UserData => KernelEventQueueCompatExports.KernelGetEventUserData(context),
            _ => throw new ArgumentOutOfRangeException(nameof(accessor)),
        };

    public enum EventAccessor
    {
        Filter,
        Data,
        UserData,
    }
}
