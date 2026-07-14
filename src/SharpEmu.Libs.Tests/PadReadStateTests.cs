// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using SharpEmu.HLE;
using SharpEmu.Libs.Pad;
using Xunit;

namespace SharpEmu.Libs.Tests;

public sealed class PadReadStateTests
{
    private const ulong DataAddress = 0x3000;
    private const int PrimaryPadHandle = 1;
    private const int PadDataSize = 0x78;
    private const int OrbisPadErrorInvalidHandle = unchecked((int)0x80920003);

    [Fact]
    public void EncoderMapsHostStateToGuestPadLayout()
    {
        var data = Enumerable.Repeat((byte)0xFF, PadDataSize).ToArray();
        var state = new PadState(
            Connected: true,
            Buttons: 0xA1B2_C3D4,
            LeftX: 1,
            LeftY: 2,
            RightX: 3,
            RightY: 4,
            L2: 5,
            R2: 6);

        PadExports.EncodePadData(data, state, 0x1122_3344_5566_7788);

        Assert.Equal(0xA1B2_C3D4u, BinaryPrimitives.ReadUInt32LittleEndian(data));
        Assert.Equal(new byte[] { 1, 2, 3, 4, 5, 6 }, data[0x04..0x0A]);
        Assert.Equal(0, data[0x0A]);
        Assert.Equal(1.0f, BinaryPrimitives.ReadSingleLittleEndian(data.AsSpan(0x18)));
        Assert.Equal(1, data[0x4C]);
        Assert.Equal(
            0x1122_3344_5566_7788UL,
            BinaryPrimitives.ReadUInt64LittleEndian(data.AsSpan(0x50)));
        Assert.Equal(1, data[0x68]);
        Assert.Equal(0, data[^1]);
    }

    [Fact]
    public void ReadStateWritesCompleteConnectedSample()
    {
        var data = new byte[PadDataSize];
        var memory = new FakeGuestMemory();
        memory.AddRegion(DataAddress, data);
        var context = CreateContext(memory, PrimaryPadHandle, DataAddress);

        var result = PadExports.PadReadState(context);

        AssertResult(context, (int)OrbisGen2Result.ORBIS_GEN2_OK, result);
        Assert.Equal(1.0f, BinaryPrimitives.ReadSingleLittleEndian(data.AsSpan(0x18)));
        Assert.Equal(1, data[0x4C]);
        Assert.NotEqual(0UL, BinaryPrimitives.ReadUInt64LittleEndian(data.AsSpan(0x50)));
        Assert.Equal(1, data[0x68]);
    }

    [Fact]
    public void ReadStateRejectsInvalidHandleBeforeReadingMemory()
    {
        var context = CreateContext(new FakeGuestMemory(), handle: 2, DataAddress);

        var result = PadExports.PadReadState(context);

        AssertResult(context, OrbisPadErrorInvalidHandle, result);
    }

    [Fact]
    public void ReadStateRejectsNullOutput()
    {
        var context = CreateContext(new FakeGuestMemory(), PrimaryPadHandle, 0);

        var result = PadExports.PadReadState(context);

        AssertResult(
            context,
            (int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT,
            result);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(PadDataSize - 1)]
    public void ReadStateRejectsUnavailableCompleteOutput(int mappedLength)
    {
        var memory = new FakeGuestMemory();
        if (mappedLength != 0)
        {
            memory.AddRegion(DataAddress, new byte[mappedLength]);
        }

        var context = CreateContext(memory, PrimaryPadHandle, DataAddress);

        var result = PadExports.PadReadState(context);

        AssertResult(
            context,
            (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT,
            result);
    }

    private static CpuContext CreateContext(
        FakeGuestMemory memory,
        int handle,
        ulong dataAddress)
    {
        var context = new CpuContext(memory, Generation.Gen5);
        context[CpuRegister.Rdi] = unchecked((ulong)handle);
        context[CpuRegister.Rsi] = dataAddress;
        return context;
    }

    private static void AssertResult(CpuContext context, int expected, int actual)
    {
        Assert.Equal(expected, actual);
        Assert.Equal(unchecked((ulong)expected), context[CpuRegister.Rax]);
    }
}
