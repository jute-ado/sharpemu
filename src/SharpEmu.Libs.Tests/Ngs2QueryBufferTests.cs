// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using SharpEmu.Libs.Ngs2;
using Xunit;

namespace SharpEmu.Libs.Tests;

public sealed class Ngs2QueryBufferTests
{
    private const int InvalidOutAddress = unchecked((int)0x804A0053);
    private const int InvalidOptionSize = unchecked((int)0x804A0081);
    private const int InvalidBufferAddress = unchecked((int)0x804A0207);
    private const ulong OptionAddress = 0x1000;
    private const ulong OutputAddress = 0x2000;
    private const ulong PreservedUserData = 0xA5A5_5A5A_1234_5678;

    [Fact]
    public void SystemQueryInitializesContextBufferInfoAndPreservesUserData()
    {
        var context = CreateContext();
        context[CpuRegister.Rdi] = 0;
        context[CpuRegister.Rsi] = OutputAddress;

        AssertCall(0, context, Ngs2Exports.Ngs2SystemQueryBufferSize);
        AssertInitializedOutput(context);
    }

    [Fact]
    public void RackQueryInitializesContextBufferInfoAndPreservesUserData()
    {
        var context = CreateContext();
        context[CpuRegister.Rdi] = 0x1000;
        context[CpuRegister.Rsi] = 0;
        context[CpuRegister.Rdx] = OutputAddress;

        AssertCall(0, context, Ngs2Exports.Ngs2RackQueryBufferSize);
        AssertInitializedOutput(context);
    }

    [Fact]
    public void QueriesReturnTheirAbiSpecificNullOutputErrors()
    {
        var context = CreateContext();
        context[CpuRegister.Rsi] = 0;
        AssertCall(
            InvalidOutAddress,
            context,
            Ngs2Exports.Ngs2SystemQueryBufferSize);

        context[CpuRegister.Rdx] = 0;
        AssertCall(
            InvalidBufferAddress,
            context,
            Ngs2Exports.Ngs2RackQueryBufferSize);
    }

    [Fact]
    public void QueriesRejectUndersizedOptionsWithoutChangingOutput()
    {
        var context = CreateContext();
        Assert.True(context.TryWriteUInt64(OptionAddress, 63));
        context[CpuRegister.Rdi] = OptionAddress;
        context[CpuRegister.Rsi] = OutputAddress;
        AssertCall(
            InvalidOptionSize,
            context,
            Ngs2Exports.Ngs2SystemQueryBufferSize);
        AssertPoisonedOutput(context);

        Assert.True(context.TryWriteUInt64(OptionAddress, 127));
        context[CpuRegister.Rdi] = 0x1000;
        context[CpuRegister.Rsi] = OptionAddress;
        context[CpuRegister.Rdx] = OutputAddress;
        AssertCall(
            InvalidOptionSize,
            context,
            Ngs2Exports.Ngs2RackQueryBufferSize);
        AssertPoisonedOutput(context);
    }

    private static CpuContext CreateContext()
    {
        var memory = new FakeGuestMemory();
        memory.AddRegion(OptionAddress, new byte[128]);
        var output = Enumerable.Repeat((byte)0xA5, 64).ToArray();
        BitConverter.TryWriteBytes(output.AsSpan(56), PreservedUserData);
        memory.AddRegion(OutputAddress, output);
        return new CpuContext(memory, Generation.Gen5);
    }

    private static void AssertInitializedOutput(CpuContext context)
    {
        Assert.True(context.TryReadUInt64(OutputAddress, out var hostBuffer));
        Assert.True(context.TryReadUInt64(OutputAddress + 8, out var hostBufferSize));
        Assert.Equal(0UL, hostBuffer);
        Assert.True(hostBufferSize > 0);

        for (ulong offset = 16; offset < 56; offset += sizeof(ulong))
        {
            Assert.True(context.TryReadUInt64(OutputAddress + offset, out var reserved));
            Assert.Equal(0UL, reserved);
        }

        Assert.True(context.TryReadUInt64(OutputAddress + 56, out var userData));
        Assert.Equal(PreservedUserData, userData);
    }

    private static void AssertPoisonedOutput(CpuContext context)
    {
        for (ulong offset = 0; offset < 56; offset += sizeof(ulong))
        {
            Assert.True(context.TryReadUInt64(OutputAddress + offset, out var value));
            Assert.Equal(0xA5A5_A5A5_A5A5_A5A5UL, value);
        }

        Assert.True(context.TryReadUInt64(OutputAddress + 56, out var userData));
        Assert.Equal(PreservedUserData, userData);
    }

    private static void AssertCall(
        int expected,
        CpuContext context,
        Func<CpuContext, int> export)
    {
        Assert.Equal(expected, export(context));
        Assert.Equal(unchecked((ulong)expected), context[CpuRegister.Rax]);
    }
}
