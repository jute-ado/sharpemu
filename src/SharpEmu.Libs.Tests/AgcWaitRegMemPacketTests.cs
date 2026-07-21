// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using SharpEmu.HLE;
using SharpEmu.Libs.Agc;
using Xunit;

namespace SharpEmu.Libs.Tests;

public sealed class AgcWaitRegMemPacketTests
{
    private const ulong CommandBufferAddress = 0x5000;
    private const ulong CommandStorageAddress = 0x6000;
    private const ulong StackAddress = 0x7000;
    private const ulong WaitAddress = 0x8004;

    [Fact]
    public void DcbWaitRegMem32EmitsCanonicalProsperoPacket()
    {
        var (context, _) = CreateContext(reference: 0x1122_3344, mask: 0xFFFF_00FF, pollCycles: 160);
        context[CpuRegister.Rdi] = CommandBufferAddress;
        context[CpuRegister.Rsi] = 0;
        context[CpuRegister.Rdx] = 3;
        context[CpuRegister.Rcx] = 0;
        context[CpuRegister.R8] = 2;
        context[CpuRegister.R9] = WaitAddress;

        Assert.Equal(0, AgcExports.DcbWaitRegMem(context));
        Assert.Equal(CommandStorageAddress, context[CpuRegister.Rax]);
        Assert.True(context.TryReadUInt64(CommandBufferAddress + 0x10, out var cursor));
        Assert.Equal(CommandStorageAddress + 7 * sizeof(uint), cursor);

        Assert.Equal(7u, ((Read(context, 0) >> 16) & 0x3FFFu) + 2);
        Assert.Equal(0x10u, (Read(context, 0) >> 8) & 0xFFu);
        Assert.Equal((uint)WaitAddress, Read(context, 4));
        Assert.Equal(0xFFFF_00FFu, Read(context, 12));
        Assert.Equal(0x1122_3344u, Read(context, 16));
        Assert.Equal(0x0400_0013u, Read(context, 20));
        Assert.Equal(10u, Read(context, 24));
    }

    [Fact]
    public void PatchReferenceUpdatesCanonicalWait32ReferenceField()
    {
        var (context, _) = CreateContext(reference: 1, mask: uint.MaxValue, pollCycles: 16);
        context[CpuRegister.Rdi] = CommandBufferAddress;
        context[CpuRegister.Rsi] = 0;
        context[CpuRegister.Rdx] = 3;
        context[CpuRegister.Rcx] = 0;
        context[CpuRegister.R8] = 0;
        context[CpuRegister.R9] = WaitAddress;
        Assert.Equal(0, AgcExports.DcbWaitRegMem(context));

        context[CpuRegister.Rdi] = CommandStorageAddress;
        context[CpuRegister.Rsi] = 0xAABB_CCDD;
        Assert.Equal(0, AgcExports.WaitRegMemPatchReference(context));
        Assert.True(context.TryReadUInt32(CommandStorageAddress + 16, out var reference));
        Assert.Equal(0xAABB_CCDDu, reference);
        Assert.True(context.TryReadUInt32(CommandStorageAddress + 20, out var control));
        Assert.Equal(0x13u, control);
    }

    [Fact]
    public void AcbWaitRegMem32UsesCanonicalControlAndPollEncoding()
    {
        var (context, _) = CreateContext(reference: 0, mask: 0, pollCycles: 0);
        Assert.True(context.TryWriteUInt64(StackAddress + 8, 0x00FF_00FF));
        Assert.True(context.TryWriteUInt32(StackAddress + 16, 160));
        context[CpuRegister.Rdi] = CommandBufferAddress;
        context[CpuRegister.Rsi] = 0;
        context[CpuRegister.Rdx] = 5;
        context[CpuRegister.Rcx] = 3;
        context[CpuRegister.R8] = WaitAddress;
        context[CpuRegister.R9] = 0x5566_7788;

        Assert.Equal(0, AgcExports.AcbWaitRegMem(context));
        Assert.Equal(7u, ((Read(context, 0) >> 16) & 0x3FFFu) + 2);
        Assert.Equal(0x00FF_00FFu, Read(context, 12));
        Assert.Equal(0x5566_7788u, Read(context, 16));
        Assert.Equal(0x0600_0015u, Read(context, 20));
        Assert.Equal(10u, Read(context, 24));
    }

    [Fact]
    public void DcbWaitRegMem64UsesCanonicalControlAndPollEncoding()
    {
        const ulong reference = 0x1122_3344_5566_7788;
        const ulong mask = 0xFFFF_0000_FFFF_00FF;
        var (context, _) = CreateContext(reference, mask, pollCycles: 160);
        context[CpuRegister.Rdi] = CommandBufferAddress;
        context[CpuRegister.Rsi] = 1;
        context[CpuRegister.Rdx] = 5;
        context[CpuRegister.Rcx] = 1;
        context[CpuRegister.R8] = 3;
        context[CpuRegister.R9] = WaitAddress + 4;

        Assert.Equal(0, AgcExports.DcbWaitRegMem(context));
        Assert.Equal(9u, ((Read(context, 0) >> 16) & 0x3FFFu) + 2);
        Assert.True(context.TryReadUInt64(CommandStorageAddress + 12, out var encodedMask));
        Assert.Equal(mask, encodedMask);
        Assert.True(context.TryReadUInt64(CommandStorageAddress + 20, out var encodedReference));
        Assert.Equal(reference, encodedReference);
        Assert.Equal(0x0600_0115u, Read(context, 28));
        Assert.Equal(10u, Read(context, 32));
    }

    private static (CpuContext Context, FakeGuestMemory Memory) CreateContext(
        ulong reference,
        ulong mask,
        uint pollCycles)
    {
        var commandBuffer = new byte[0x38];
        BinaryPrimitives.WriteUInt64LittleEndian(commandBuffer.AsSpan(0x10), CommandStorageAddress);
        BinaryPrimitives.WriteUInt64LittleEndian(commandBuffer.AsSpan(0x18), CommandStorageAddress + 0x100);
        var stack = new byte[4 * sizeof(ulong)];
        BinaryPrimitives.WriteUInt64LittleEndian(stack.AsSpan(8), reference);
        BinaryPrimitives.WriteUInt64LittleEndian(stack.AsSpan(16), mask);
        BinaryPrimitives.WriteUInt32LittleEndian(stack.AsSpan(24), pollCycles);
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
}
