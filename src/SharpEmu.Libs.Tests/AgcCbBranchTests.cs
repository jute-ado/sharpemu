// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using SharpEmu.HLE;
using SharpEmu.Libs.Agc;
using Xunit;

namespace SharpEmu.Libs.Tests;

public sealed class AgcCbBranchTests
{
    private const ulong CommandBufferAddress = 0x5000;
    private const ulong CommandStorageAddress = 0x6000;
    private const ulong StackAddress = 0x7000;

    [Fact]
    public void CbBranchEmitsCanonicalConditionalIndirectBufferPacket()
    {
        const ulong compareAddress = 0x0000_0012_3456_7007;
        const ulong mask = 0xFFFF_0000_FFFF_00FF;
        const ulong reference = 0x1122_3344_5566_7788;
        const ulong thenAddress = 0x0000_0023_4567_8003;
        const ulong elseAddress = 0x0000_0034_5678_9002;
        var (context, _) = CreateContext(
            cachePolicy1: 2,
            thenAddress,
            thenDwords: 0x12345,
            cachePolicy2: 3,
            elseAddress,
            elseDwords: 0x54321);
        context[CpuRegister.Rdi] = CommandBufferAddress;
        context[CpuRegister.Rsi] = 2;
        context[CpuRegister.Rdx] = 6;
        context[CpuRegister.Rcx] = compareAddress;
        context[CpuRegister.R8] = mask;
        context[CpuRegister.R9] = reference;

        Assert.Equal(0, AgcExports.CbBranch(context));
        Assert.Equal(CommandStorageAddress, context[CpuRegister.Rax]);
        Assert.True(context.TryReadUInt64(
            CommandBufferAddress + 0x10,
            out var nextCursor));
        Assert.Equal(CommandStorageAddress + 14 * sizeof(uint), nextCursor);

        Assert.Equal(Pm4(14, 0x3F), Read(context, 0));
        Assert.Equal(0x602u, Read(context, 4));
        Assert.Equal(unchecked((uint)compareAddress) & ~7u, Read(context, 8));
        Assert.Equal((uint)(compareAddress >> 32), Read(context, 12));
        Assert.Equal(unchecked((uint)mask), Read(context, 16));
        Assert.Equal((uint)(mask >> 32), Read(context, 20));
        Assert.Equal(unchecked((uint)reference), Read(context, 24));
        Assert.Equal((uint)(reference >> 32), Read(context, 28));
        Assert.Equal(unchecked((uint)thenAddress) & ~3u, Read(context, 32));
        Assert.Equal((uint)(thenAddress >> 32), Read(context, 36));
        Assert.Equal(0x2001_2345u, Read(context, 40));
        Assert.Equal(unchecked((uint)elseAddress) & ~3u, Read(context, 44));
        Assert.Equal((uint)(elseAddress >> 32), Read(context, 48));
        Assert.Equal(0x3005_4321u, Read(context, 52));
    }

    [Fact]
    public void CbBranchGetSizeMatchesPacket()
    {
        var context = new CpuContext(new FakeGuestMemory(), Generation.Gen5);

        Assert.Equal(14 * sizeof(uint), AgcExports.CbBranchGetSize(context));
        Assert.Equal(14UL * sizeof(uint), context[CpuRegister.Rax]);
    }

    private static (CpuContext Context, FakeGuestMemory Memory) CreateContext(
        ulong cachePolicy1,
        ulong thenAddress,
        ulong thenDwords,
        ulong cachePolicy2,
        ulong elseAddress,
        ulong elseDwords)
    {
        var commandBuffer = new byte[0x38];
        BinaryPrimitives.WriteUInt64LittleEndian(
            commandBuffer.AsSpan(0x10),
            CommandStorageAddress);
        BinaryPrimitives.WriteUInt64LittleEndian(
            commandBuffer.AsSpan(0x18),
            CommandStorageAddress + 0x100);
        var stack = new byte[7 * sizeof(ulong)];
        BinaryPrimitives.WriteUInt64LittleEndian(stack.AsSpan(8), cachePolicy1);
        BinaryPrimitives.WriteUInt64LittleEndian(stack.AsSpan(16), thenAddress);
        BinaryPrimitives.WriteUInt64LittleEndian(stack.AsSpan(24), thenDwords);
        BinaryPrimitives.WriteUInt64LittleEndian(stack.AsSpan(32), cachePolicy2);
        BinaryPrimitives.WriteUInt64LittleEndian(stack.AsSpan(40), elseAddress);
        BinaryPrimitives.WriteUInt64LittleEndian(stack.AsSpan(48), elseDwords);
        var memory = new FakeGuestMemory();
        memory.AddRegion(CommandBufferAddress, commandBuffer);
        memory.AddRegion(CommandStorageAddress, new byte[0x100]);
        memory.AddRegion(StackAddress, stack);
        var context = new CpuContext(memory, Generation.Gen5);
        context[CpuRegister.Rsp] = StackAddress;
        return (context, memory);
    }

    private static uint Read(CpuContext context, ulong offset)
    {
        Assert.True(context.TryReadUInt32(CommandStorageAddress + offset, out var value));
        return value;
    }

    private static uint Pm4(uint dwordCount, uint opcode) =>
        0xC000_0000u | ((dwordCount - 2) << 16) | (opcode << 8);
}
