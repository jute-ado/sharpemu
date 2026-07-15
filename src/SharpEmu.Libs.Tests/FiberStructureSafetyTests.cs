// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using System.Text;
using SharpEmu.HLE;
using SharpEmu.Libs.Fiber;
using Xunit;

namespace SharpEmu.Libs.Tests;

public sealed class FiberStructureSafetyTests
{
    private const int FiberErrorInvalid = unchecked((int)0x80590004);
    private const ulong FiberAddress = 0x1000;
    private const ulong NameAddress = 0x2000;
    private const ulong InfoAddress = 0x3000;
    private const ulong EntryAddress = 0x4000;
    private const ulong StackAddress = 0x5000;
    private const ulong InitializeArgument = 0x1122_3344_5566_7788;
    private const string FiberName = "Dreaming Sarah";
    private const byte Canary = 0xA5;

    [Fact]
    public void InitializeAndGetInfoRoundTripFiberFields()
    {
        var fiber = new byte[108];
        var info = new byte[128];
        BinaryPrimitives.WriteUInt64LittleEndian(info, (ulong)info.Length);
        var memory = new FakeGuestMemory();
        memory.AddRegion(FiberAddress, fiber);
        memory.AddRegion(NameAddress, Encoding.UTF8.GetBytes($"{FiberName}\0"));
        memory.AddRegion(InfoAddress, info);
        var context = CreateInitializeContext(memory, FiberAddress);

        Assert.Equal(0, FiberExports.FiberInitialize(context));

        context[CpuRegister.Rdi] = FiberAddress;
        context[CpuRegister.Rsi] = InfoAddress;
        Assert.Equal(0, FiberExports.FiberGetInfo(context));
        Assert.Equal(EntryAddress, BinaryPrimitives.ReadUInt64LittleEndian(info.AsSpan(8)));
        Assert.Equal(InitializeArgument, BinaryPrimitives.ReadUInt64LittleEndian(info.AsSpan(16)));
        Assert.Equal(0uL, BinaryPrimitives.ReadUInt64LittleEndian(info.AsSpan(24)));
        Assert.Equal(0uL, BinaryPrimitives.ReadUInt64LittleEndian(info.AsSpan(32)));
        Assert.Equal(
            FiberName,
            Encoding.UTF8.GetString(info.AsSpan(40, Encoding.UTF8.GetByteCount(FiberName))));
        Assert.Equal(ulong.MaxValue, BinaryPrimitives.ReadUInt64LittleEndian(info.AsSpan(72)));

        context[CpuRegister.Rdi] = FiberAddress;
        Assert.Equal(0, FiberExports.FiberFinalize(context));
        Assert.Equal(3u, BinaryPrimitives.ReadUInt32LittleEndian(fiber.AsSpan(4)));
    }

    [Fact]
    public void InitializeRejectsWrappedControlBlockBeforeWriting()
    {
        var finalBytes = Enumerable.Repeat(Canary, 8).ToArray();
        var wrappedBytes = Enumerable.Repeat(Canary, 108).ToArray();
        var memory = new FakeGuestMemory();
        memory.AddRegion(ulong.MaxValue - 7, finalBytes);
        memory.AddRegion(0, wrappedBytes);
        memory.AddRegion(NameAddress, Encoding.UTF8.GetBytes($"{FiberName}\0"));
        var context = CreateInitializeContext(memory, ulong.MaxValue - 7);

        Assert.Equal(FiberErrorInvalid, FiberExports.FiberInitialize(context));
        Assert.All(finalBytes, value => Assert.Equal(Canary, value));
        Assert.All(wrappedBytes, value => Assert.Equal(Canary, value));
    }

    [Fact]
    public void GetInfoRejectsWrappedOutputBeforeWriting()
    {
        var fiber = new byte[108];
        var finalSize = new byte[sizeof(ulong)];
        BinaryPrimitives.WriteUInt64LittleEndian(finalSize, 128);
        var wrappedOutput = Enumerable.Repeat(Canary, 128).ToArray();
        var memory = new FakeGuestMemory();
        memory.AddRegion(FiberAddress, fiber);
        memory.AddRegion(NameAddress, Encoding.UTF8.GetBytes($"{FiberName}\0"));
        memory.AddRegion(ulong.MaxValue - 7, finalSize);
        memory.AddRegion(0, wrappedOutput);
        var context = CreateInitializeContext(memory, FiberAddress);
        Assert.Equal(0, FiberExports.FiberInitialize(context));

        context[CpuRegister.Rdi] = FiberAddress;
        context[CpuRegister.Rsi] = ulong.MaxValue - 7;
        Assert.Equal(FiberErrorInvalid, FiberExports.FiberGetInfo(context));
        Assert.All(wrappedOutput, value => Assert.Equal(Canary, value));
    }

    private static CpuContext CreateInitializeContext(FakeGuestMemory memory, ulong fiberAddress)
    {
        memory.AddRegion(StackAddress, new byte[32]);
        var context = new CpuContext(memory, Generation.Gen5);
        context[CpuRegister.Rdi] = fiberAddress;
        context[CpuRegister.Rsi] = NameAddress;
        context[CpuRegister.Rdx] = EntryAddress;
        context[CpuRegister.Rcx] = InitializeArgument;
        context[CpuRegister.Rsp] = StackAddress;
        return context;
    }
}
