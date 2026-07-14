// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using SharpEmu.HLE;
using SharpEmu.Libs.Agc;
using Xunit;

namespace SharpEmu.Libs.Tests;

public sealed class AgcCommandEncodingTests
{
    private const ulong CommandBufferAddress = 0x1000;
    private const ulong CommandStorageAddress = 0x2000;

    [Fact]
    public void DcbJumpEncodesTargetAndAdvancesCursor()
    {
        var fixture = CreateCommandBuffer(capacityDwords: 8);
        const ulong targetAddress = 0x1122_3344_5566_7788;
        fixture.Context[CpuRegister.Rdi] = CommandBufferAddress;
        fixture.Context[CpuRegister.Rsi] = targetAddress;

        Assert.Equal(
            (int)OrbisGen2Result.ORBIS_GEN2_OK,
            AgcExports.DcbJump(fixture.Context));
        Assert.Equal(CommandStorageAddress, fixture.Context[CpuRegister.Rax]);
        Assert.Equal(CommandStorageAddress + 3 * sizeof(uint), ReadCursor(fixture.Control));
        Assert.Equal(0xC001_106Cu, ReadDword(fixture.Commands, 0));
        Assert.Equal(0x5566_7788u, ReadDword(fixture.Commands, 1));
        Assert.Equal(0x1122_3344u, ReadDword(fixture.Commands, 2));
    }

    [Fact]
    public void DcbSetPredicationEncodesArgumentsAndAdvancesCursor()
    {
        var fixture = CreateCommandBuffer(capacityDwords: 8);
        fixture.Context[CpuRegister.Rdi] = CommandBufferAddress;
        fixture.Context[CpuRegister.Rsi] = 1;
        fixture.Context[CpuRegister.Rdx] = 3;
        fixture.Context[CpuRegister.Rcx] = 0;
        fixture.Context[CpuRegister.R8] = 0x1234_5678;

        Assert.Equal(
            (int)OrbisGen2Result.ORBIS_GEN2_OK,
            AgcExports.DcbSetPredication(fixture.Context));
        Assert.Equal(CommandStorageAddress, fixture.Context[CpuRegister.Rax]);
        Assert.Equal(CommandStorageAddress + 5 * sizeof(uint), ReadCursor(fixture.Control));
        Assert.Equal(0xC003_1068u, ReadDword(fixture.Commands, 0));
        Assert.Equal(1u, ReadDword(fixture.Commands, 1));
        Assert.Equal(3u, ReadDword(fixture.Commands, 2));
        Assert.Equal(0u, ReadDword(fixture.Commands, 3));
        Assert.Equal(0x1234_5678u, ReadDword(fixture.Commands, 4));
    }

    [Fact]
    public void DcbJumpRejectsInsufficientCapacityWithoutMovingCursor()
    {
        var fixture = CreateCommandBuffer(capacityDwords: 2);
        fixture.Context[CpuRegister.Rdi] = CommandBufferAddress;
        fixture.Context[CpuRegister.Rsi] = 0x1234;

        Assert.Equal(
            (int)OrbisGen2Result.ORBIS_GEN2_OK,
            AgcExports.DcbJump(fixture.Context));
        Assert.Equal(0UL, fixture.Context[CpuRegister.Rax]);
        Assert.Equal(CommandStorageAddress, ReadCursor(fixture.Control));
        Assert.Equal(0u, ReadDword(fixture.Commands, 0));
        Assert.Equal(0u, ReadDword(fixture.Commands, 1));
    }

    [Fact]
    public void SetPacketPredicationTogglesOnlyEnableBit()
    {
        var memory = new FakeGuestMemory();
        var packet = new byte[sizeof(uint)];
        BinaryPrimitives.WriteUInt32LittleEndian(packet, 0xC003_1068);
        memory.AddRegion(CommandStorageAddress, packet);
        var context = new CpuContext(memory, Generation.Gen5);
        context[CpuRegister.Rdi] = CommandStorageAddress;
        context[CpuRegister.Rsi] = 1;

        Assert.Equal(
            (int)OrbisGen2Result.ORBIS_GEN2_OK,
            AgcExports.SetPacketPredication(context));
        Assert.Equal(0xC003_1069u, BinaryPrimitives.ReadUInt32LittleEndian(packet));

        context[CpuRegister.Rsi] = 0;
        Assert.Equal(
            (int)OrbisGen2Result.ORBIS_GEN2_OK,
            AgcExports.SetPacketPredication(context));
        Assert.Equal(0xC003_1068u, BinaryPrimitives.ReadUInt32LittleEndian(packet));
    }

    private static CommandBufferFixture CreateCommandBuffer(uint capacityDwords)
    {
        var memory = new FakeGuestMemory();
        var control = new byte[0x40];
        var commands = new byte[checked((int)(capacityDwords * sizeof(uint)))];
        BinaryPrimitives.WriteUInt64LittleEndian(control.AsSpan(0x10), CommandStorageAddress);
        BinaryPrimitives.WriteUInt64LittleEndian(
            control.AsSpan(0x18),
            CommandStorageAddress + (ulong)commands.Length);
        memory.AddRegion(CommandBufferAddress, control);
        memory.AddRegion(CommandStorageAddress, commands);
        return new CommandBufferFixture(
            new CpuContext(memory, Generation.Gen5),
            control,
            commands);
    }

    private static ulong ReadCursor(byte[] control) =>
        BinaryPrimitives.ReadUInt64LittleEndian(control.AsSpan(0x10));

    private static uint ReadDword(byte[] commands, int index) =>
        BinaryPrimitives.ReadUInt32LittleEndian(commands.AsSpan(index * sizeof(uint)));

    private sealed record CommandBufferFixture(
        CpuContext Context,
        byte[] Control,
        byte[] Commands);
}
