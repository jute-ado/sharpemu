// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using SharpEmu.HLE;
using SharpEmu.Libs.Kernel;
using Xunit;

namespace SharpEmu.Libs.Tests;

public sealed class LibcBoundedStringCopyTests
{
    private const ulong DestinationAddress = 0x1000;
    private const ulong SourceAddress = 0x10_0000;
    private const byte Canary = 0xA5;

    [Fact]
    public void StrncpyDoesNotAllocateFromHuge64BitCount()
    {
        var context = CreateContext(new FakeGuestMemory(), 1UL << 63);

        Assert.Equal(
            (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT,
            KernelMemoryCompatExports.Strncpy(context));
    }

    [Fact]
    public void WcsncpyDoesNotRejectGuestCountBecauseItExceedsHostArrayLimits()
    {
        var context = CreateContext(new FakeGuestMemory(), 1UL << 63);

        Assert.Equal(
            (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT,
            KernelMemoryCompatExports.Wcsncpy(context));
    }

    [Fact]
    public void StrncpyStreamsChunksPadsAfterTerminatorAndPreservesCanaries()
    {
        const int count = 40_000;
        var memory = new FakeGuestMemory();
        var destination = Enumerable.Repeat(Canary, count + 16).ToArray();
        memory.AddRegion(DestinationAddress, destination);
        memory.AddRegion(SourceAddress, [(byte)'o', (byte)'k', 0]);
        var context = CreateContext(memory, count, DestinationAddress + 8);

        Assert.Equal(0, KernelMemoryCompatExports.Strncpy(context));
        Assert.Equal(DestinationAddress + 8, context[CpuRegister.Rax]);
        Assert.All(destination.AsSpan(0, 8).ToArray(), value => Assert.Equal(Canary, value));
        Assert.Equal((byte)'o', destination[8]);
        Assert.Equal((byte)'k', destination[9]);
        Assert.All(destination.AsSpan(10, count - 2).ToArray(), value => Assert.Equal(0, value));
        Assert.All(destination.AsSpan(count + 8).ToArray(), value => Assert.Equal(Canary, value));
    }

    [Fact]
    public void StrncpyCopiesExactlyCountBytesWhenSourceIsNotTerminated()
    {
        var memory = new FakeGuestMemory();
        var destination = new byte[3];
        memory.AddRegion(DestinationAddress, destination);
        memory.AddRegion(SourceAddress, [(byte)'a', (byte)'b', (byte)'c']);
        var context = CreateContext(memory, 3);

        Assert.Equal(0, KernelMemoryCompatExports.Strncpy(context));
        Assert.Equal("abc", System.Text.Encoding.ASCII.GetString(destination));
    }

    [Fact]
    public void StrncpyRejectsSourceAddressWrapInsteadOfContinuingAtZero()
    {
        var memory = new FakeGuestMemory();
        var destination = new[] { Canary, Canary };
        memory.AddRegion(DestinationAddress, destination);
        memory.AddRegion(ulong.MaxValue, [(byte)'A']);
        memory.AddRegion(0, [0]);
        var context = CreateContext(memory, 2);
        context[CpuRegister.Rsi] = ulong.MaxValue;

        Assert.Equal(
            (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT,
            KernelMemoryCompatExports.Strncpy(context));
        Assert.Equal(new[] { Canary, Canary }, destination);
    }

    [Fact]
    public void WcsncpyStreamsAndPadsCompleteWideElements()
    {
        const int count = 10_000;
        var memory = new FakeGuestMemory();
        var destination = Enumerable.Repeat(Canary, (count * sizeof(ushort)) + 16).ToArray();
        var source = new byte[3 * sizeof(ushort)];
        BinaryPrimitives.WriteUInt16LittleEndian(source, 'A');
        BinaryPrimitives.WriteUInt16LittleEndian(source.AsSpan(2), 'B');
        memory.AddRegion(DestinationAddress, destination);
        memory.AddRegion(SourceAddress, source);
        var context = CreateContext(memory, count, DestinationAddress + 8);

        Assert.Equal(0, KernelMemoryCompatExports.Wcsncpy(context));
        Assert.Equal(DestinationAddress + 8, context[CpuRegister.Rax]);
        Assert.All(destination.AsSpan(0, 8).ToArray(), value => Assert.Equal(Canary, value));
        Assert.Equal((ushort)'A', BinaryPrimitives.ReadUInt16LittleEndian(destination.AsSpan(8)));
        Assert.Equal((ushort)'B', BinaryPrimitives.ReadUInt16LittleEndian(destination.AsSpan(10)));
        Assert.All(destination.AsSpan(12, (count * sizeof(ushort)) - 4).ToArray(), value => Assert.Equal(0, value));
        Assert.All(destination.AsSpan((count * sizeof(ushort)) + 8).ToArray(), value => Assert.Equal(Canary, value));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ZeroCountAllowsNullPointers(bool wide)
    {
        var context = new CpuContext(new FakeGuestMemory(), Generation.Gen5);

        var result = wide
            ? KernelMemoryCompatExports.Wcsncpy(context)
            : KernelMemoryCompatExports.Strncpy(context);

        Assert.Equal(0, result);
        Assert.Equal(0UL, context[CpuRegister.Rax]);
    }

    [Theory]
    [InlineData("6sJWiWSRuqk", "strncpy")]
    [InlineData("0nV21JjYCH8", "wcsncpy")]
    public void ExportMetadataIsExact(string nid, string exportName)
    {
        ExportMetadataAssert.Exact(
            nid,
            exportName,
            "libc",
            Generation.Gen4 | Generation.Gen5);
    }

    private static CpuContext CreateContext(
        FakeGuestMemory memory,
        ulong count,
        ulong destination = DestinationAddress)
    {
        var context = new CpuContext(memory, Generation.Gen5);
        context[CpuRegister.Rdi] = destination;
        context[CpuRegister.Rsi] = SourceAddress;
        context[CpuRegister.Rdx] = count;
        return context;
    }
}
