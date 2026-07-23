// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using SharpEmu.HLE;
using SharpEmu.Libs.Agc;
using Xunit;

namespace SharpEmu.Libs.Tests;

public sealed class AgcJumpPacketTests
{
    private const ulong CommandBufferAddress = 0x5000;
    private const ulong CommandStorageAddress = 0x6000;

    [Fact]
    public void DcbJumpUsesFiveArgumentAbiAndEmitsCanonicalIndirectBufferPacket()
    {
        const ulong targetAddress = 0x0000_1234_9ABC_DEF3;
        var context = CreateContext();
        context[CpuRegister.Rdi] = CommandBufferAddress;
        context[CpuRegister.Rsi] = 1;
        context[CpuRegister.Rdx] = 2;
        context[CpuRegister.Rcx] = targetAddress;
        context[CpuRegister.R8] = 0x12_3456;

        Assert.Equal(0, AgcExports.DcbJump(context));
        Assert.Equal(CommandStorageAddress, context[CpuRegister.Rax]);
        Assert.True(context.TryReadUInt64(CommandBufferAddress + 0x10, out var cursor));
        Assert.Equal(CommandStorageAddress + 4 * sizeof(uint), cursor);
        Assert.Equal(0xC002_3F00u, Read(context, 0));
        Assert.Equal(0x9ABC_DEF0u, Read(context, 4));
        Assert.Equal(0x0000_1234u, Read(context, 8));
        Assert.Equal(0x2F32_3456u, Read(context, 12));
    }

    [Fact]
    public void AcbJumpUsesObservedFourArgumentAbiAndEmitsIndirectBufferPacket()
    {
        const ulong targetAddress = 0x0000_0010_2AA3_CA00;
        var context = CreateContext();
        context[CpuRegister.Rdi] = CommandBufferAddress;
        context[CpuRegister.Rsi] = 0;
        context[CpuRegister.Rdx] = targetAddress;
        context[CpuRegister.Rcx] = 0x18C;
        context[CpuRegister.R8] = ulong.MaxValue;

        Assert.Equal(0, AgcExports.AcbJump(context));
        Assert.Equal(CommandStorageAddress, context[CpuRegister.Rax]);
        Assert.True(context.TryReadUInt64(CommandBufferAddress + 0x10, out var cursor));
        Assert.Equal(CommandStorageAddress + 4 * sizeof(uint), cursor);
        Assert.Equal(0xC002_3F00u, Read(context, 0));
        Assert.Equal(0x2AA3_CA00u, Read(context, 4));
        Assert.Equal(0x0000_0010u, Read(context, 8));
        Assert.Equal(0x0F20_018Cu, Read(context, 12));
    }

    [Fact]
    public void AcbJumpReturnsNullWithoutAdvancingWhenPacketDoesNotFit()
    {
        var context = CreateContext(storageBytes: 3 * sizeof(uint));
        context[CpuRegister.Rdi] = CommandBufferAddress;
        context[CpuRegister.Rdx] = 0x8000;
        context[CpuRegister.Rcx] = 4;

        Assert.Equal(0, AgcExports.AcbJump(context));
        Assert.Equal(0UL, context[CpuRegister.Rax]);
        Assert.True(context.TryReadUInt64(CommandBufferAddress + 0x10, out var cursor));
        Assert.Equal(CommandStorageAddress, cursor);
    }

    [Fact]
    public void AcbJumpExportsUseExactGen5Metadata()
    {
        ExportMetadataAssert.Exact(
            "e1DFTg+Sd8U",
            "sceAgcAcbJump",
            "libSceAgc",
            Generation.Gen5);
        ExportMetadataAssert.Exact(
            "b-oySn+G2tE",
            "sceAgcAcbJumpGetSize",
            "libSceAgc",
            Generation.Gen5);
    }

    private static CpuContext CreateContext(int storageBytes = 0x100)
    {
        var commandBuffer = new byte[0x38];
        BinaryPrimitives.WriteUInt64LittleEndian(commandBuffer.AsSpan(0x10), CommandStorageAddress);
        BinaryPrimitives.WriteUInt64LittleEndian(
            commandBuffer.AsSpan(0x18),
            CommandStorageAddress + (ulong)storageBytes);
        var memory = new FakeGuestMemory();
        memory.AddRegion(CommandBufferAddress, commandBuffer);
        memory.AddRegion(CommandStorageAddress, new byte[storageBytes]);
        return new CpuContext(memory, Generation.Gen5);
    }

    private static uint Read(CpuContext context, ulong offset)
    {
        Assert.True(context.TryReadUInt32(CommandStorageAddress + offset, out var value));
        return value;
    }
}
