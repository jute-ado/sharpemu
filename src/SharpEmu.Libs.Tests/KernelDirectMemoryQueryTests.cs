// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using SharpEmu.HLE;
using SharpEmu.Libs.Kernel;
using Xunit;

namespace SharpEmu.Libs.Tests;

public sealed class KernelDirectMemoryQueryTests
{
    private const ulong OutputAddress = 0x1000;
    private const ulong DirectMemorySize = 13_824UL * 1024 * 1024;
    private const ulong SearchSize = 0x0100_0000;
    private const ulong AllocationLength = 0x4000;
    private const int MemoryType = 7;

    [Fact]
    public void GetDirectMemorySizeReportsPs5ApplicationCapacity()
    {
        var context = new CpuContext(new FakeGuestMemory(), Generation.Gen5);

        var result = KernelMemoryCompatExports.KernelGetDirectMemorySize(context);

        Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_OK, result);
        Assert.Equal(DirectMemorySize, context[CpuRegister.Rax]);
    }

    [Fact]
    public void FindNextReturnsFirstAllocationAfterOffset()
    {
        var (context, output, allocationStart) = CreateAllocatedContext(0x1800_0000);
        context[CpuRegister.Rdi] = allocationStart - 0x1000;
        context[CpuRegister.Rsi] = 1;
        context[CpuRegister.Rdx] = OutputAddress;
        context[CpuRegister.Rcx] = 24;

        var result = KernelMemoryCompatExports.KernelDirectMemoryQuery(context);

        Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_OK, result);
        AssertQueryInfo(output, allocationStart);
    }

    [Fact]
    public void ExactQueryReturnsContainingAllocation()
    {
        var (context, output, allocationStart) = CreateAllocatedContext(0x1900_0000);
        context[CpuRegister.Rdi] = allocationStart + 0x1000;
        context[CpuRegister.Rsi] = 0;
        context[CpuRegister.Rdx] = OutputAddress;
        context[CpuRegister.Rcx] = 24;

        var result = KernelMemoryCompatExports.KernelDirectMemoryQuery(context);

        Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_OK, result);
        AssertQueryInfo(output, allocationStart);
    }

    [Fact]
    public void QueryWithoutFindNextReturnsKernelAccessErrorForGap()
    {
        var (context, output, allocationStart) = CreateAllocatedContext(0x1A00_0000);
        context[CpuRegister.Rdi] = allocationStart - 0x1000;
        context[CpuRegister.Rsi] = 0;
        context[CpuRegister.Rdx] = OutputAddress;
        context[CpuRegister.Rcx] = 24;

        var result = KernelMemoryCompatExports.KernelDirectMemoryQuery(context);

        Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_ERROR_DELETED, result);
        Assert.All(output, value => Assert.Equal(0xCC, value));
    }

    [Fact]
    public void FindNextAfterLastAllocationReturnsTerminalRange()
    {
        var (context, output) = CreateEmptyContext();
        context[CpuRegister.Rdi] = DirectMemorySize - 1;
        context[CpuRegister.Rsi] = 1;
        context[CpuRegister.Rdx] = OutputAddress;
        context[CpuRegister.Rcx] = 24;

        var result = KernelMemoryCompatExports.KernelDirectMemoryQuery(context);

        Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_OK, result);
        Assert.Equal(DirectMemorySize, BinaryPrimitives.ReadUInt64LittleEndian(output.AsSpan(0, 8)));
        Assert.Equal(DirectMemorySize, BinaryPrimitives.ReadUInt64LittleEndian(output.AsSpan(8, 8)));
        Assert.Equal(0, BinaryPrimitives.ReadInt32LittleEndian(output.AsSpan(16, 4)));
        Assert.All(output.AsSpan(20, 4).ToArray(), value => Assert.Equal(0xCC, value));
    }

    [Theory]
    [InlineData(-1L, 0, 24)]
    [InlineData(0L, 2, 24)]
    [InlineData(0L, 0, 23)]
    [InlineData(0L, 0, 25)]
    public void QueryRejectsNegativeOffsetsUnknownFlagsAndNonExactSizes(
        long offset,
        ulong flags,
        ulong infoSize)
    {
        var (context, output) = CreateEmptyContext();
        context[CpuRegister.Rdi] = unchecked((ulong)offset);
        context[CpuRegister.Rsi] = flags;
        context[CpuRegister.Rdx] = OutputAddress;
        context[CpuRegister.Rcx] = infoSize;

        var result = KernelMemoryCompatExports.KernelDirectMemoryQuery(context);

        Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT, result);
        Assert.All(output, value => Assert.Equal(0xCC, value));
    }

    private static (CpuContext Context, byte[] Output) CreateEmptyContext()
    {
        var output = Enumerable.Repeat((byte)0xCC, 24).ToArray();
        var memory = new FakeGuestMemory();
        memory.AddRegion(OutputAddress, output);
        return (new CpuContext(memory, Generation.Gen5), output);
    }

    private static (CpuContext Context, byte[] Output, ulong AllocationStart) CreateAllocatedContext(
        ulong searchStart)
    {
        var output = Enumerable.Repeat((byte)0xCC, 24).ToArray();
        var memory = new FakeGuestMemory();
        memory.AddRegion(OutputAddress, output);
        var context = new CpuContext(memory, Generation.Gen5);
        var searchEnd = searchStart + SearchSize;
        context[CpuRegister.Rdi] = searchStart;
        context[CpuRegister.Rsi] = searchEnd;
        context[CpuRegister.Rdx] = AllocationLength;
        context[CpuRegister.Rcx] = 0x4000;
        context[CpuRegister.R8] = MemoryType;
        context[CpuRegister.R9] = OutputAddress;

        var result = KernelMemoryCompatExports.KernelAllocateDirectMemory(context);
        Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_OK, result);
        var allocationStart = BinaryPrimitives.ReadUInt64LittleEndian(output);
        Assert.InRange(allocationStart, searchStart, searchEnd - AllocationLength);
        Array.Fill(output, (byte)0xCC);
        return (context, output, allocationStart);
    }

    private static void AssertQueryInfo(byte[] output, ulong allocationStart)
    {
        Assert.Equal(allocationStart, BinaryPrimitives.ReadUInt64LittleEndian(output.AsSpan(0, 8)));
        Assert.Equal(allocationStart + AllocationLength, BinaryPrimitives.ReadUInt64LittleEndian(output.AsSpan(8, 8)));
        Assert.Equal(MemoryType, BinaryPrimitives.ReadInt32LittleEndian(output.AsSpan(16, 4)));
        Assert.All(output.AsSpan(20, 4).ToArray(), value => Assert.Equal(0xCC, value));
    }
}
