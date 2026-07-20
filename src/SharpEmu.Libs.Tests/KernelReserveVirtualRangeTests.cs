// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using SharpEmu.HLE;
using SharpEmu.Libs.Kernel;
using Xunit;

namespace SharpEmu.Libs.Tests;

public sealed class KernelReserveVirtualRangeTests
{
    private const ulong AddressPointer = 0x1000;
    private const ulong RangeLength = 0x4000;

    [Fact]
    public void FixedReservationCanReplaceTheSameReservedRange()
    {
        var addressBytes = new byte[sizeof(ulong)];
        var memory = new FakeGuestMemory();
        memory.AddRegion(AddressPointer, addressBytes);
        var context = new CpuContext(memory, Generation.Gen5);
        context[CpuRegister.Rdi] = AddressPointer;
        context[CpuRegister.Rsi] = RangeLength;

        var initialResult = KernelRuntimeCompatExports.KernelReserveVirtualRange(context);
        var reservedAddress = BinaryPrimitives.ReadUInt64LittleEndian(addressBytes);
        context[CpuRegister.Rdx] = 0x10;

        var fixedResult = KernelRuntimeCompatExports.KernelReserveVirtualRange(context);

        Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_OK, initialResult);
        Assert.NotEqual(0UL, reservedAddress);
        Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_OK, fixedResult);
        Assert.Equal(reservedAddress, BinaryPrimitives.ReadUInt64LittleEndian(addressBytes));
    }
}
