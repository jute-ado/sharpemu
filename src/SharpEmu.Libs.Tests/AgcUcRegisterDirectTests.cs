// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using SharpEmu.HLE;
using SharpEmu.Libs.Agc;
using Xunit;

namespace SharpEmu.Libs.Tests;

public sealed class AgcUcRegisterDirectTests
{
    private const ulong GuestBase = 0x1_0000_0000;
    private const ulong CommandBufferAddress = GuestBase + 0x100;
    private const ulong CursorUp = GuestBase + 0x200;
    private const ulong CursorDown = GuestBase + 0x400;

    [Fact]
    public void ExportsHaveExactGen5Metadata()
    {
        ExportMetadataAssert.Exact(
            "w4-d0n60hdo",
            "sceAgcDcbSetUcRegisterDirect",
            "libSceAgc",
            Generation.Gen5);
        ExportMetadataAssert.Exact(
            "aP1Ki9G3++4",
            "sceAgcDcbSetUcRegisterDirectGetSize",
            "libSceAgc",
            Generation.Gen5);

        var gen4 = SharpEmu.Generated.SysAbiExportRegistry.CreateExports(
            Generation.Gen4);
        Assert.DoesNotContain(gen4, export => export.Nid == "w4-d0n60hdo");
        Assert.DoesNotContain(gen4, export => export.Nid == "aP1Ki9G3++4");
    }

    [Fact]
    public void SizeProbeReturnsExactThreeDwordPacketSize()
    {
        var context = new CpuContext(new FakeGuestMemory(), Generation.Gen5);
        context[CpuRegister.Rdi] = ulong.MaxValue;
        context[CpuRegister.Rsi] = ulong.MaxValue;

        Assert.Equal(12, AgcExports.DcbSetUcRegisterDirectGetSize(context));
        Assert.Equal(12UL, context[CpuRegister.Rax]);
    }

    [Fact]
    public void EmitsExactFirmwarePacketAndAdvancesCursor()
    {
        var memory = CreateMemory(CursorUp, CursorDown);
        var context = new CpuContext(memory, Generation.Gen5)
        {
            [CpuRegister.Rdi] = CommandBufferAddress,
            // Low dword is the UCONFIG register offset; high dword is its value.
            [CpuRegister.Rsi] = (0x20UL << 32) | 0xABCD_024AUL,
        };

        Assert.Equal(0, AgcExports.DcbSetUcRegisterDirect(context));
        Assert.Equal(CursorUp, context[CpuRegister.Rax]);

        Span<byte> packet = stackalloc byte[3 * sizeof(uint)];
        Assert.True(memory.TryRead(CursorUp, packet));
        Assert.Equal(
            0xC001_7900u,
            BinaryPrimitives.ReadUInt32LittleEndian(packet));
        Assert.Equal(
            0x024Au,
            BinaryPrimitives.ReadUInt32LittleEndian(packet[4..]));
        Assert.Equal(
            0x20u,
            BinaryPrimitives.ReadUInt32LittleEndian(packet[8..]));

        Span<byte> cursor = stackalloc byte[sizeof(ulong)];
        Assert.True(memory.TryRead(CommandBufferAddress + 0x10, cursor));
        Assert.Equal(
            CursorUp + (3UL * sizeof(uint)),
            BinaryPrimitives.ReadUInt64LittleEndian(cursor));
    }

    [Fact]
    public void NullOrFullCommandBufferFailsWithoutAdvancingCursor()
    {
        var memory = CreateMemory(CursorDown - sizeof(uint), CursorDown);
        var context = new CpuContext(memory, Generation.Gen5)
        {
            [CpuRegister.Rdi] = 0,
            [CpuRegister.Rsi] = (0x20UL << 32) | 0x024AUL,
        };
        Assert.Equal(0, AgcExports.DcbSetUcRegisterDirect(context));
        Assert.Equal(0UL, context[CpuRegister.Rax]);

        context[CpuRegister.Rdi] = CommandBufferAddress;
        Assert.Equal(0, AgcExports.DcbSetUcRegisterDirect(context));
        Assert.Equal(0UL, context[CpuRegister.Rax]);

        Span<byte> cursor = stackalloc byte[sizeof(ulong)];
        Assert.True(memory.TryRead(CommandBufferAddress + 0x10, cursor));
        Assert.Equal(
            CursorDown - sizeof(uint),
            BinaryPrimitives.ReadUInt64LittleEndian(cursor));
    }

    private static FakeGuestMemory CreateMemory(
        ulong cursorUp,
        ulong cursorDown)
    {
        var storage = new byte[0x1000];
        BinaryPrimitives.WriteUInt64LittleEndian(
            storage.AsSpan(0x100 + 0x10),
            cursorUp);
        BinaryPrimitives.WriteUInt64LittleEndian(
            storage.AsSpan(0x100 + 0x18),
            cursorDown);
        var memory = new FakeGuestMemory();
        memory.AddRegion(GuestBase, storage);
        return memory;
    }
}
