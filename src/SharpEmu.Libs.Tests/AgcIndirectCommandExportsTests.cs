// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using SharpEmu.HLE;
using SharpEmu.Libs.Agc;
using Xunit;

namespace SharpEmu.Libs.Tests;

public sealed class AgcIndirectCommandExportsTests
{
    private const ulong CommandAddress = 0x2000;
    private const ulong CommandBufferAddress = 0x3000;
    private const ulong CommandStorageAddress = 0x4000;

    [Theory]
    [InlineData("whb1RL7K4Ss", "sceAgcSetCxRegIndirectPatchSetNumRegisters")]
    [InlineData("nCUgItdN2ms", "sceAgcSetShRegIndirectPatchSetNumRegisters")]
    [InlineData("fRG-JOH5+sI", "sceAgcSetUcRegIndirectPatchSetNumRegisters")]
    [InlineData("1q1titRBL6o", "sceAgcDcbDrawIndirect")]
    [InlineData("cxPZ4Wgvdj8", "sceAgcDcbDrawIndirectGetSize")]
    public void RegistersGen5AgcExports(string nid, string name) =>
        ExportMetadataAssert.Exact(nid, name, "libSceAgc", Generation.Gen5);

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void SetIndirectPatchNumRegisters_ReplacesCountAndMasksToFourteenBits(int registerSpace)
    {
        var bytes = new byte[4 * sizeof(uint)];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(sizeof(uint)), 7);
        var memory = new FakeGuestMemory();
        memory.AddRegion(CommandAddress, bytes);
        var context = new CpuContext(memory, Generation.Gen5);
        context[CpuRegister.Rdi] = CommandAddress;
        context[CpuRegister.Rsi] = 0x1_8005;

        var result = registerSpace switch
        {
            0 => AgcExports.SetCxRegIndirectPatchSetNumRegisters(context),
            1 => AgcExports.SetShRegIndirectPatchSetNumRegisters(context),
            _ => AgcExports.SetUcRegIndirectPatchSetNumRegisters(context),
        };

        Assert.Equal(0, result);
        Assert.Equal(0UL, context[CpuRegister.Rax]);
        Assert.True(context.TryReadUInt32(CommandAddress + sizeof(uint), out var count));
        Assert.Equal(5u, count);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void AddIndirectPatchRegisters_WrapsCountWithinFourteenBits(int registerSpace)
    {
        var bytes = new byte[4 * sizeof(uint)];
        BinaryPrimitives.WriteUInt32LittleEndian(
            bytes.AsSpan(sizeof(uint)),
            0x3FFE);
        var memory = new FakeGuestMemory();
        memory.AddRegion(CommandAddress, bytes);
        var context = new CpuContext(memory, Generation.Gen5);
        context[CpuRegister.Rdi] = CommandAddress;
        context[CpuRegister.Rsi] = 5;

        var result = registerSpace switch
        {
            0 => AgcExports.SetCxRegIndirectPatchAddRegisters(context),
            1 => AgcExports.SetShRegIndirectPatchAddRegisters(context),
            _ => AgcExports.SetUcRegIndirectPatchAddRegisters(context),
        };

        Assert.Equal(0, result);
        Assert.True(context.TryReadUInt32(CommandAddress + sizeof(uint), out var count));
        Assert.Equal(3u, count);
    }

    [Theory]
    [InlineData(0, 0x12u)]
    [InlineData(1, 0x11u)]
    [InlineData(2, 0x13u)]
    public void DcbSetRegistersIndirect_MasksCountToHardwareWidth(
        int registerSpace,
        uint packetRegister)
    {
        const ulong registersAddress = 0x5000;
        var memory = CreateCommandBufferMemory();
        var context = new CpuContext(memory, Generation.Gen5);
        context[CpuRegister.Rdi] = CommandBufferAddress;
        context[CpuRegister.Rsi] = registersAddress;
        context[CpuRegister.Rdx] = 0x4005;

        var result = registerSpace switch
        {
            0 => AgcExports.DcbSetCxRegistersIndirect(context),
            1 => AgcExports.DcbSetShRegistersIndirect(context),
            _ => AgcExports.DcbSetUcRegistersIndirect(context),
        };

        Assert.Equal(0, result);
        Assert.Equal(CommandStorageAddress, context[CpuRegister.Rax]);
        AssertPacketWord(context, 0, Pm4(4, 0x10, packetRegister));
        AssertPacketWord(context, 1, 5);
        AssertPacketWord(context, 2, (uint)registersAddress);
        AssertPacketWord(context, 3, (uint)(registersAddress >> 32));
    }

    [Fact]
    public void DcbDrawIndirect_EmitsDecodedFiveDwordPacket()
    {
        var memory = CreateCommandBufferMemory();
        var context = new CpuContext(memory, Generation.Gen5);
        context[CpuRegister.Rdi] = CommandBufferAddress;
        context[CpuRegister.Rsi] = 0x44;
        context[CpuRegister.Rdx] = CreateIndirectModifier();

        Assert.Equal(0, AgcExports.DcbDrawIndirect(context));
        Assert.Equal(CommandStorageAddress, context[CpuRegister.Rax]);
        AssertPacketWord(context, 0, Pm4(5, 0x24));
        AssertPacketWord(context, 1, 0x44);
        AssertPacketWord(context, 2, 0x111);
        AssertPacketWord(context, 3, 0x113);
        AssertPacketWord(context, 4, 2);
        Assert.True(context.TryReadUInt64(CommandBufferAddress + 0x10, out var cursor));
        Assert.Equal(CommandStorageAddress + (5UL * sizeof(uint)), cursor);
    }

    [Fact]
    public void DcbDrawIndexIndirect_DecodesIndexedPatchOffsetsAndInitiator()
    {
        var memory = CreateCommandBufferMemory();
        var context = new CpuContext(memory, Generation.Gen5);
        context[CpuRegister.Rdi] = CommandBufferAddress;
        context[CpuRegister.Rsi] = 0x88;
        context[CpuRegister.Rdx] = CreateIndirectModifier() & ~(1UL << 32);

        Assert.Equal(0, AgcExports.DcbDrawIndexIndirect(context));
        Assert.Equal(CommandStorageAddress, context[CpuRegister.Rax]);
        AssertPacketWord(context, 0, Pm4(5, 0x25));
        AssertPacketWord(context, 1, 0x88);
        AssertPacketWord(context, 2, 0x111 | (0x112u << 16));
        AssertPacketWord(context, 3, 0x0800_0113);
        AssertPacketWord(context, 4, 0x22);
    }

    [Fact]
    public void DcbDrawIndirect_ReturnsNullWithoutMutatingFullCommandBuffer()
    {
        var memory = CreateCommandBufferMemory(storageDwords: 4);
        var context = new CpuContext(memory, Generation.Gen5);
        context[CpuRegister.Rdi] = CommandBufferAddress;
        context[CpuRegister.Rsi] = 0x44;
        context[CpuRegister.Rdx] = CreateIndirectModifier();

        Assert.Equal(0, AgcExports.DcbDrawIndirect(context));
        Assert.Equal(0UL, context[CpuRegister.Rax]);
        Assert.True(context.TryReadUInt64(CommandBufferAddress + 0x10, out var cursor));
        Assert.Equal(CommandStorageAddress, cursor);
        AssertPacketWord(context, 0, 0);
    }

    [Fact]
    public void DcbDrawIndirectGetSize_ReturnsPacketSize()
    {
        var context = new CpuContext(new FakeGuestMemory(), Generation.Gen5);

        Assert.Equal(5 * sizeof(uint), AgcExports.DcbDrawIndirectGetSize(context));
        Assert.Equal(5UL * sizeof(uint), context[CpuRegister.Rax]);
    }

    private static FakeGuestMemory CreateCommandBufferMemory(uint storageDwords = 16)
    {
        var commandBuffer = new byte[0x38];
        BinaryPrimitives.WriteUInt64LittleEndian(
            commandBuffer.AsSpan(0x10),
            CommandStorageAddress);
        BinaryPrimitives.WriteUInt64LittleEndian(
            commandBuffer.AsSpan(0x18),
            CommandStorageAddress + (storageDwords * sizeof(uint)));
        var memory = new FakeGuestMemory();
        memory.AddRegion(CommandBufferAddress, commandBuffer);
        memory.AddRegion(CommandStorageAddress, new byte[storageDwords * sizeof(uint)]);
        return memory;
    }

    private static ulong CreateIndirectModifier() =>
        (3UL << 29) |
        (5UL << 9) |
        (6UL << 14) |
        (7UL << 19) |
        (1UL << 32) |
        0x107UL;

    private static void AssertPacketWord(CpuContext context, uint index, uint expected)
    {
        Assert.True(context.TryReadUInt32(
            CommandStorageAddress + ((ulong)index * sizeof(uint)),
            out var actual));
        Assert.Equal(expected, actual);
    }

    private static uint Pm4(uint dwordCount, uint opcode, uint register = 0) =>
        0xC000_0000u | ((dwordCount - 2) << 16) | (opcode << 8) | (register << 2);
}
