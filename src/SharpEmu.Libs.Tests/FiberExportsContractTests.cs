// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using SharpEmu.HLE;
using SharpEmu.Libs.Fiber;
using Xunit;

namespace SharpEmu.Libs.Tests;

public sealed class FiberExportsContractTests
{
    private const ulong BaseAddress = 0x3_0000_0000;
    private const ulong FiberAddress = BaseAddress;
    private const ulong NameAddress = BaseAddress + 0x200;
    private const ulong ContextAddress = BaseAddress + 0x400;
    private const ulong InfoAddress = BaseAddress + 0x800;
    private const ulong EntryAddress = 0x4_0000_1000;
    private const int ErrorNull = unchecked((int)0x80590001);
    private const int ErrorAlignment = unchecked((int)0x80590002);
    private const int ErrorRange = unchecked((int)0x80590003);
    private const int ErrorInvalid = unchecked((int)0x80590004);
    private const int ErrorPermission = unchecked((int)0x80590005);

    public FiberExportsContractTests()
    {
        FiberExports.ResetRuntimeState();
    }

    [Fact]
    public void OptParamInitializeRejectsNullOutput()
    {
        var context = CreateContext();

        Assert.Equal(
            ErrorNull,
            FiberExports.FiberOptParamInitialize(context));
    }

    [Fact]
    public void GetSelfRejectsNullOutput()
    {
        var context = CreateContext();

        Assert.Equal(ErrorNull, FiberExports.FiberGetSelf(context));
    }

    [Fact]
    public void GetSelfRejectsCallsOutsideAnActiveFiber()
    {
        var context = CreateContext();
        context[CpuRegister.Rdi] = BaseAddress + 0x100;

        Assert.Equal(
            ErrorPermission,
            FiberExports.FiberGetSelf(context));
    }

    [Fact]
    public void GetInfoRejectsNullOutput()
    {
        var context = CreateContext();
        context[CpuRegister.Rdi] = FiberAddress;

        Assert.Equal(ErrorNull, FiberExports.FiberGetInfo(context));
    }

    [Theory]
    [InlineData(0UL, NameAddress, EntryAddress)]
    [InlineData(FiberAddress, 0UL, EntryAddress)]
    [InlineData(FiberAddress, NameAddress, 0UL)]
    public void InitializeRejectsNullRequiredArguments(
        ulong fiberAddress,
        ulong nameAddress,
        ulong entryAddress)
    {
        var context = CreateContext();
        WriteCString(context, NameAddress, "Fiber");
        SetInitializeArguments(
            context,
            fiberAddress,
            nameAddress,
            entryAddress);

        Assert.Equal(
            ErrorNull,
            FiberExports.FiberInitialize(context));
    }

    [Fact]
    public void InitializeRejectsMisalignedControlBlock()
    {
        var context = CreateContext();
        WriteCString(context, NameAddress, "Fiber");
        SetInitializeArguments(
            context,
            FiberAddress + 4,
            NameAddress,
            EntryAddress);

        Assert.Equal(
            ErrorAlignment,
            FiberExports.FiberInitialize(context));
    }

    [Fact]
    public void InitializeRejectsContextBelowMinimumSize()
    {
        var context = CreateContext();
        WriteCString(context, NameAddress, "Fiber");
        SetInitializeArguments(
            context,
            FiberAddress,
            NameAddress,
            EntryAddress);
        context[CpuRegister.R8] = ContextAddress;
        context[CpuRegister.R9] = 256;

        Assert.Equal(
            ErrorRange,
            FiberExports.FiberInitialize(context));
    }

    [Fact]
    public void InitializeRejectsContextAddressWithoutSize()
    {
        var context = CreateContext();
        WriteCString(context, NameAddress, "Fiber");
        SetInitializeArguments(
            context,
            FiberAddress,
            NameAddress,
            EntryAddress);
        context[CpuRegister.R8] = ContextAddress;

        Assert.Equal(
            ErrorInvalid,
            FiberExports.FiberInitialize(context));
    }

    [Fact]
    public void InitializeWritesTheDocumentedControlBlockLayout()
    {
        const ulong argument = 0xDEAD;
        const ulong contextSize = 512;
        const string name = "TestFiber";
        var context = CreateContext();
        WriteCString(context, NameAddress, name);
        SetInitializeArguments(
            context,
            FiberAddress,
            NameAddress,
            EntryAddress);
        context[CpuRegister.Rcx] = argument;
        context[CpuRegister.R8] = ContextAddress;
        context[CpuRegister.R9] = contextSize;

        Assert.Equal(0, FiberExports.FiberInitialize(context));

        Assert.Equal(0xDEF1649Cu, ReadUInt32(context, FiberAddress));
        Assert.Equal(2u, ReadUInt32(context, FiberAddress + 4));
        Assert.Equal(EntryAddress, ReadUInt64(context, FiberAddress + 8));
        Assert.Equal(argument, ReadUInt64(context, FiberAddress + 16));
        Assert.Equal(ContextAddress, ReadUInt64(context, FiberAddress + 24));
        Assert.Equal(contextSize, ReadUInt64(context, FiberAddress + 32));
        AssertInlineName(context, FiberAddress + 40, name);
        Assert.Equal(0UL, ReadUInt64(context, FiberAddress + 72));
        Assert.Equal(0u, ReadUInt32(context, FiberAddress + 80));
        Assert.Equal(ContextAddress, ReadUInt64(context, FiberAddress + 88));
        Assert.Equal(
            ContextAddress + contextSize,
            ReadUInt64(context, FiberAddress + 96));
        Assert.Equal(0xB37592A0u, ReadUInt32(context, FiberAddress + 104));
        Assert.Equal(
            0x7149F2CA7149F2CAUL,
            ReadUInt64(context, ContextAddress));
    }

    [Fact]
    public void GetInfoRejectsUnexpectedStructureSize()
    {
        var context = CreateContext();
        WriteCString(context, NameAddress, "Fiber");
        SetInitializeArguments(
            context,
            FiberAddress,
            NameAddress,
            EntryAddress);
        context[CpuRegister.R8] = ContextAddress;
        context[CpuRegister.R9] = 512;
        Assert.Equal(0, FiberExports.FiberInitialize(context));
        WriteUInt64(context, InfoAddress, 64);
        context[CpuRegister.Rdi] = FiberAddress;
        context[CpuRegister.Rsi] = InfoAddress;

        Assert.Equal(ErrorInvalid, FiberExports.FiberGetInfo(context));
    }

    private static CpuContext CreateContext() =>
        new(
            new FakeCpuMemory(BaseAddress, 0x2000),
            Generation.Gen5);

    private static void SetInitializeArguments(
        CpuContext context,
        ulong fiberAddress,
        ulong nameAddress,
        ulong entryAddress)
    {
        context[CpuRegister.Rdi] = fiberAddress;
        context[CpuRegister.Rsi] = nameAddress;
        context[CpuRegister.Rdx] = entryAddress;
    }

    private static void WriteUInt64(
        CpuContext context,
        ulong address,
        ulong value)
    {
        Span<byte> bytes = stackalloc byte[sizeof(ulong)];
        BinaryPrimitives.WriteUInt64LittleEndian(bytes, value);
        Assert.True(context.Memory.TryWrite(address, bytes));
    }

    private static void WriteCString(
        CpuContext context,
        ulong address,
        string value)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(value + "\0");
        Assert.True(context.Memory.TryWrite(address, bytes));
    }

    private static uint ReadUInt32(CpuContext context, ulong address)
    {
        Span<byte> bytes = stackalloc byte[sizeof(uint)];
        Assert.True(context.Memory.TryRead(address, bytes));
        return BinaryPrimitives.ReadUInt32LittleEndian(bytes);
    }

    private static ulong ReadUInt64(CpuContext context, ulong address)
    {
        Span<byte> bytes = stackalloc byte[sizeof(ulong)];
        Assert.True(context.Memory.TryRead(address, bytes));
        return BinaryPrimitives.ReadUInt64LittleEndian(bytes);
    }

    private static void AssertInlineName(
        CpuContext context,
        ulong address,
        string expected)
    {
        Span<byte> bytes = stackalloc byte[32];
        Assert.True(context.Memory.TryRead(address, bytes));
        var terminator = bytes.IndexOf((byte)0);
        Assert.True(terminator >= 0);
        Assert.Equal(
            expected,
            System.Text.Encoding.UTF8.GetString(bytes[..terminator]));
    }
}
