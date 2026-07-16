// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using SharpEmu.Libs.Kernel;
using Xunit;

namespace SharpEmu.Libs.Tests;

public sealed class LibcMemoryCopyTests
{
    private const ulong BufferAddress = 0x1000;
    private const ulong SourceAddress = 0x10_0000;
    private const int MaxTransferSize = 256 * 1024;
    private const byte Canary = 0xA5;

    [Fact]
    public void MemcpyStreamsLargeCopyAndPreservesCanaries()
    {
        const int count = 600_000;
        var source = CreatePattern(count);
        var destination = Enumerable.Repeat(Canary, count + 16).ToArray();
        var inner = new FakeGuestMemory();
        inner.AddRegion(SourceAddress, source);
        inner.AddRegion(BufferAddress, destination);
        var memory = new MaxTransferMemory(inner, MaxTransferSize);
        var context = CreateContext(memory, BufferAddress + 8, SourceAddress, count);

        Assert.Equal(0, KernelMemoryCompatExports.Memcpy(context));
        Assert.Equal(BufferAddress + 8, context[CpuRegister.Rax]);
        Assert.Equal(source, destination.AsSpan(8, count).ToArray());
        Assert.All(destination.AsSpan(0, 8).ToArray(), value => Assert.Equal(Canary, value));
        Assert.All(destination.AsSpan(count + 8).ToArray(), value => Assert.Equal(Canary, value));
        Assert.InRange(memory.LargestRead, 1, MaxTransferSize);
        Assert.InRange(memory.LargestWrite, 1, MaxTransferSize);
    }

    [Theory]
    [InlineData(0, 1000)]
    [InlineData(1000, 0)]
    public void MemmovePreservesCrossChunkOverlappingCopies(int sourceOffset, int destinationOffset)
    {
        const int count = 600_000;
        var backing = CreatePattern(count + 2_000);
        var expected = backing.ToArray();
        Array.Copy(expected, sourceOffset, expected, destinationOffset, count);
        var inner = new FakeGuestMemory();
        inner.AddRegion(BufferAddress, backing);
        var memory = new MaxTransferMemory(inner, MaxTransferSize);
        var context = CreateContext(
            memory,
            BufferAddress + (ulong)destinationOffset,
            BufferAddress + (ulong)sourceOffset,
            count);

        Assert.Equal(0, KernelMemoryCompatExports.Memmove(context));
        Assert.Equal(expected, backing);
        Assert.InRange(memory.LargestRead, 1, MaxTransferSize);
        Assert.InRange(memory.LargestWrite, 1, MaxTransferSize);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Huge64BitCountReturnsGuestFaultWithoutHostAllocation(bool move)
    {
        var context = CreateContext(
            new FakeGuestMemory(),
            BufferAddress,
            SourceAddress,
            1UL << 63);

        var result = move
            ? KernelMemoryCompatExports.Memmove(context)
            : KernelMemoryCompatExports.Memcpy(context);

        Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT, result);
        Assert.Equal(BufferAddress, context[CpuRegister.Rax]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ZeroCountAllowsNullPointers(bool move)
    {
        var context = new CpuContext(new FakeGuestMemory(), Generation.Gen5);

        var result = move
            ? KernelMemoryCompatExports.Memmove(context)
            : KernelMemoryCompatExports.Memcpy(context);

        Assert.Equal(0, result);
        Assert.Equal(0UL, context[CpuRegister.Rax]);
    }

    [Theory]
    [InlineData("Q3VBxCXhUHs", "memcpy")]
    [InlineData("+P6FRGH4LfA", "memmove")]
    public void ExportMetadataIsExact(string nid, string exportName)
    {
        ExportMetadataAssert.Exact(
            nid,
            exportName,
            "libc",
            Generation.Gen4 | Generation.Gen5);
    }

    private static CpuContext CreateContext(
        ICpuMemory memory,
        ulong destination,
        ulong source,
        ulong count)
    {
        var context = new CpuContext(memory, Generation.Gen5);
        context[CpuRegister.Rdi] = destination;
        context[CpuRegister.Rsi] = source;
        context[CpuRegister.Rdx] = count;
        return context;
    }

    private static byte[] CreatePattern(int length)
    {
        var bytes = new byte[length];
        for (var index = 0; index < bytes.Length; index++)
        {
            bytes[index] = (byte)((index * 37) + 11);
        }

        return bytes;
    }

    private sealed class MaxTransferMemory(ICpuMemory inner, int maxTransferSize) : ICpuMemory
    {
        public int LargestRead { get; private set; }

        public int LargestWrite { get; private set; }

        public bool TryRead(ulong virtualAddress, Span<byte> destination)
        {
            LargestRead = Math.Max(LargestRead, destination.Length);
            return destination.Length <= maxTransferSize && inner.TryRead(virtualAddress, destination);
        }

        public bool TryWrite(ulong virtualAddress, ReadOnlySpan<byte> source)
        {
            LargestWrite = Math.Max(LargestWrite, source.Length);
            return source.Length <= maxTransferSize && inner.TryWrite(virtualAddress, source);
        }
    }
}
