// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using SharpEmu.HLE;
using SharpEmu.Libs.VideoOut;
using Xunit;

namespace SharpEmu.Libs.Tests;

public sealed class VideoOutFlipStatusSafetyTests
{
    private const ulong StatusAddress = 0x1000;
    private const int FlipStatusSize = 0x28;

    [Fact]
    public void GetFlipStatusWritesCompleteStructure()
    {
        var status = Enumerable.Repeat((byte)0xCC, FlipStatusSize).ToArray();
        var memory = new FakeGuestMemory();
        memory.AddRegion(StatusAddress, status);

        WithOpenPort(memory, (context, handle) =>
        {
            context[CpuRegister.Rdi] = unchecked((ulong)handle);
            context[CpuRegister.Rsi] = StatusAddress;

            Assert.Equal(
                (int)OrbisGen2Result.ORBIS_GEN2_OK,
                VideoOutExports.VideoOutGetFlipStatus(context));
            Assert.Equal(0ul, BinaryPrimitives.ReadUInt64LittleEndian(status));
            Assert.Equal(0ul, BinaryPrimitives.ReadUInt64LittleEndian(status.AsSpan(0x08)));
            Assert.Equal(0ul, BinaryPrimitives.ReadUInt64LittleEndian(status.AsSpan(0x10)));
            Assert.Equal(0ul, BinaryPrimitives.ReadUInt64LittleEndian(status.AsSpan(0x18)));
            Assert.Equal(uint.MaxValue, BinaryPrimitives.ReadUInt32LittleEndian(status.AsSpan(0x20)));
            Assert.Equal(0u, BinaryPrimitives.ReadUInt32LittleEndian(status.AsSpan(0x24)));
        });
    }

    [Fact]
    public void GetFlipStatusRejectsWrappedStructureBeforeMutation()
    {
        var highStatus = Enumerable.Repeat((byte)0xA5, sizeof(ulong)).ToArray();
        var lowStatus = Enumerable.Repeat((byte)0x5A, FlipStatusSize - sizeof(ulong)).ToArray();
        var memory = new FakeGuestMemory();
        memory.AddRegion(ulong.MaxValue - 7, highStatus);
        memory.AddRegion(0, lowStatus);

        WithOpenPort(memory, (context, handle) =>
        {
            context[CpuRegister.Rdi] = unchecked((ulong)handle);
            context[CpuRegister.Rsi] = ulong.MaxValue - 7;

            Assert.Equal(
                (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT,
                VideoOutExports.VideoOutGetFlipStatus(context));
            Assert.All(highStatus, value => Assert.Equal(0xA5, value));
            Assert.All(lowStatus, value => Assert.Equal(0x5A, value));
        });
    }

    [Fact]
    public void GetFlipStatusRejectsUnmappedStructure()
    {
        var memory = new FakeGuestMemory();

        WithOpenPort(memory, (context, handle) =>
        {
            context[CpuRegister.Rdi] = unchecked((ulong)handle);
            context[CpuRegister.Rsi] = StatusAddress;

            Assert.Equal(
                (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT,
                VideoOutExports.VideoOutGetFlipStatus(context));
        });
    }

    private static void WithOpenPort(
        FakeGuestMemory memory,
        Action<CpuContext, int> action)
    {
        var context = new CpuContext(memory, Generation.Gen5);
        var handle = VideoOutExports.VideoOutOpen(context);
        Assert.True(handle > 0);

        try
        {
            action(context, handle);
        }
        finally
        {
            context[CpuRegister.Rdi] = unchecked((ulong)handle);
            _ = VideoOutExports.VideoOutClose(context);
        }
    }
}
