// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using SharpEmu.HLE;
using SharpEmu.Libs.Agc;
using Xunit;

namespace SharpEmu.Libs.Tests;

public sealed class AgcCommandAllocationSafetyTests
{
    [Fact]
    public void DcbSetFlipRejectsHostInaccessibleCommandBuffer()
    {
        var context = new CpuContext(
            new HostInaccessibleMemory(),
            Generation.Gen5)
        {
            [CpuRegister.Rdi] = 0x6000_0000,
            [CpuRegister.Rsi] = 1,
            [CpuRegister.Rdx] = 0,
        };

        Assert.Equal(0, AgcExports.DcbSetFlip(context));
        Assert.Equal(0UL, context[CpuRegister.Rax]);
    }

    [Fact]
    public void DcbSetFlipRejectsCommandBufferThatBecomesHostWriteInaccessible()
    {
        const ulong commandBufferAddress = 0x6000_0000;
        const ulong cursorUp = 0x6000_0100;
        var storage = new byte[0x200];
        BinaryPrimitives.WriteUInt64LittleEndian(
            storage.AsSpan(0x10),
            cursorUp);
        BinaryPrimitives.WriteUInt64LittleEndian(
            storage.AsSpan(0x18),
            cursorUp + 0x80);
        var readableMemory = new FakeGuestMemory();
        readableMemory.AddRegion(commandBufferAddress, storage);
        var context = new CpuContext(
            new HostWriteInaccessibleMemory(readableMemory),
            Generation.Gen5)
        {
            [CpuRegister.Rdi] = commandBufferAddress,
            [CpuRegister.Rsi] = 1,
            [CpuRegister.Rdx] = 0,
        };

        Assert.Equal(0, AgcExports.DcbSetFlip(context));
        Assert.Equal(0UL, context[CpuRegister.Rax]);
    }

    private sealed class HostInaccessibleMemory : ICpuMemory
    {
        public bool TryRead(
            ulong virtualAddress,
            Span<byte> destination) =>
            throw new AccessViolationException(
                "Simulated host page protection race.");

        public bool TryWrite(
            ulong virtualAddress,
            ReadOnlySpan<byte> source) =>
            throw new AccessViolationException(
                "Simulated host page protection race.");
    }

    private sealed class HostWriteInaccessibleMemory(
        ICpuMemory readableMemory) : ICpuMemory
    {
        public bool TryRead(
            ulong virtualAddress,
            Span<byte> destination) =>
            readableMemory.TryRead(virtualAddress, destination);

        public bool TryWrite(
            ulong virtualAddress,
            ReadOnlySpan<byte> source) =>
            throw new AccessViolationException(
                "Simulated host page protection race.");
    }
}
