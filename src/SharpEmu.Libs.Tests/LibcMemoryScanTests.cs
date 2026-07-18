// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using SharpEmu.Libs.Kernel;
using Xunit;

namespace SharpEmu.Libs.Tests;

public sealed class LibcMemoryScanTests
{
    private const ulong LeftAddress = 0x1000;
    private const ulong RightAddress = 0x10_0000;
    private const int MaxTransferSize = 16 * 1024;

    [Fact]
    public void MemcmpScansLargeEqualRegionsInBoundedChunks()
    {
        const int count = 40_000;
        var left = CreatePattern(count);
        var right = left.ToArray();
        var inner = new FakeGuestMemory();
        inner.AddRegion(LeftAddress, left);
        inner.AddRegion(RightAddress, right);
        var memory = new CountingMemory(inner, MaxTransferSize);
        var context = CreateContext(memory, LeftAddress, RightAddress, count);

        Assert.Equal(0, KernelMemoryCompatExports.Memcmp(context));
        Assert.Equal(0UL, context[CpuRegister.Rax]);
        Assert.InRange(memory.ReadCalls, 1, 6);
        Assert.InRange(memory.LargestRead, 1, MaxTransferSize);
    }

    [Fact]
    public void MemcmpReturnsFirstDifferenceAcrossChunkBoundary()
    {
        const int count = 40_000;
        var left = CreatePattern(count);
        var right = left.ToArray();
        left[20_000] = 1;
        right[20_000] = 3;
        var inner = new FakeGuestMemory();
        inner.AddRegion(LeftAddress, left);
        inner.AddRegion(RightAddress, right);
        var context = CreateContext(new CountingMemory(inner, MaxTransferSize), LeftAddress, RightAddress, count);

        Assert.Equal(0, KernelMemoryCompatExports.Memcmp(context));
        Assert.Equal(unchecked((ulong)-2L), context[CpuRegister.Rax]);
    }

    [Fact]
    public void MemcmpRejectsAddressWrapInsteadOfContinuingAtZero()
    {
        var memory = new FakeGuestMemory();
        memory.AddRegion(ulong.MaxValue, [1]);
        memory.AddRegion(0, [2]);
        memory.AddRegion(RightAddress, [1, 2]);
        var context = CreateContext(memory, ulong.MaxValue, RightAddress, 2);

        Assert.Equal(
            (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT,
            KernelMemoryCompatExports.Memcmp(context));
    }

    [Fact]
    public void MemchrScansLargeRegionInBoundedChunks()
    {
        const int count = 40_000;
        const byte needle = 0xFE;
        var bytes = new byte[count];
        bytes[^1] = needle;
        var inner = new FakeGuestMemory();
        inner.AddRegion(LeftAddress, bytes);
        var memory = new CountingMemory(inner, MaxTransferSize);
        var context = CreateContext(memory, LeftAddress, needle, count);

        Assert.Equal(0, KernelMemoryCompatExports.Memchr(context));
        Assert.Equal(LeftAddress + count - 1, context[CpuRegister.Rax]);
        Assert.InRange(memory.ReadCalls, 1, 3);
        Assert.InRange(memory.LargestRead, 1, MaxTransferSize);
    }

    [Fact]
    public void MemchrRejectsAddressWrapInsteadOfContinuingAtZero()
    {
        var memory = new FakeGuestMemory();
        memory.AddRegion(ulong.MaxValue, [1]);
        memory.AddRegion(0, [2]);
        var context = CreateContext(memory, ulong.MaxValue, 2, 2);

        Assert.Equal(
            (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT,
            KernelMemoryCompatExports.Memchr(context));
    }

    [Fact]
    public void StrstrFindsMatchAcrossGuestPageBoundary()
    {
        const ulong haystack = 0x1FFB;
        const ulong needle = 0x4000;
        var memory = new FakeGuestMemory();
        memory.AddRegion(
            haystack,
            "abcde-needle-suffix\0"u8.ToArray());
        memory.AddRegion(needle, "needle\0"u8.ToArray());
        var context = CreateContext(memory, haystack, needle, 0);

        Assert.Equal(0, KernelMemoryCompatExports.Strstr(context));
        Assert.Equal(haystack + 6, context[CpuRegister.Rax]);
    }

    [Fact]
    public void StrstrReturnsMatchWithoutReadingUnreadableTail()
    {
        const ulong haystack = 0x3000;
        const ulong needle = 0x5000;
        var memory = new FakeGuestMemory();
        memory.AddRegion(haystack, "prefix-needle"u8.ToArray());
        memory.AddRegion(needle, "needle\0"u8.ToArray());
        var context = CreateContext(memory, haystack, needle, 0);

        Assert.Equal(0, KernelMemoryCompatExports.Strstr(context));
        Assert.Equal(haystack + 7, context[CpuRegister.Rax]);
    }

    [Fact]
    public void StrstrReportsFaultBeforeMatchAndNullWhenAbsent()
    {
        const ulong haystack = 0x3000;
        const ulong needle = 0x5000;
        var memory = new FakeGuestMemory();
        memory.AddRegion(haystack, "no match here\0"u8.ToArray());
        memory.AddRegion(needle, "needle\0"u8.ToArray());
        var context = CreateContext(memory, haystack, needle, 0);

        Assert.Equal(0, KernelMemoryCompatExports.Strstr(context));
        Assert.Equal(0UL, context[CpuRegister.Rax]);

        context[CpuRegister.Rdi] = 0x6000;
        Assert.Equal(
            (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT,
            KernelMemoryCompatExports.Strstr(context));
    }

    [Fact]
    public void StrstrEmptyNeedleDoesNotReadHaystack()
    {
        const ulong needle = 0x5000;
        var inner = new FakeGuestMemory();
        inner.AddRegion(needle, [0]);
        var memory = new CountingMemory(inner, MaxTransferSize);
        var context = CreateContext(memory, 0xDEAD_BEEF, needle, 0);

        Assert.Equal(0, KernelMemoryCompatExports.Strstr(context));
        Assert.Equal(0xDEAD_BEEFUL, context[CpuRegister.Rax]);
        Assert.All(
            inner.ReadAddresses,
            address => Assert.Equal(needle, address));
    }

    [Fact]
    public void StrcasecmpScansLargeEqualStringsInPageBoundedChunks()
    {
        const int length = 40_000;
        var left = new byte[40_960];
        var right = new byte[40_960];
        for (var index = 0; index < length; index++)
        {
            left[index] = (byte)('A' + (index % 26));
            right[index] = (byte)('a' + (index % 26));
        }

        var inner = new FakeGuestMemory();
        inner.AddRegion(LeftAddress, left);
        inner.AddRegion(RightAddress, right);
        var memory = new CountingMemory(inner, MaxTransferSize);
        var context = CreateContext(
            memory,
            LeftAddress,
            RightAddress,
            0);

        Assert.Equal(0, KernelMemoryCompatExports.Strcasecmp(context));
        Assert.Equal(0UL, context[CpuRegister.Rax]);
        Assert.InRange(memory.ReadCalls, 2, 24);
        Assert.InRange(memory.LargestRead, 1, 4096);
    }

    [Fact]
    public void StrcmpReturnsExactDifferenceAcrossGuestPageBoundary()
    {
        const ulong left = 0x1FFD;
        const ulong right = 0x2FFD;
        var memory = new FakeGuestMemory();
        memory.AddRegion(left, [1, 2, 3, 4, 5, 0]);
        memory.AddRegion(right, [1, 2, 3, 7, 5, 0]);
        var context = CreateContext(memory, left, right, 0);

        Assert.Equal(0, KernelMemoryCompatExports.Strcmp(context));
        Assert.Equal(unchecked((ulong)-3L), context[CpuRegister.Rax]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ZeroCountAllowsNullPointer(bool compare)
    {
        var context = new CpuContext(new FakeGuestMemory(), Generation.Gen5);

        var result = compare
            ? KernelMemoryCompatExports.Memcmp(context)
            : KernelMemoryCompatExports.Memchr(context);

        Assert.Equal(0, result);
        Assert.Equal(0UL, context[CpuRegister.Rax]);
    }

    [Theory]
    [InlineData("DfivPArhucg", "memcmp")]
    [InlineData("8u8lPzUEq+U", "memchr")]
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
        ulong first,
        ulong second,
        ulong count)
    {
        var context = new CpuContext(memory, Generation.Gen5);
        context[CpuRegister.Rdi] = first;
        context[CpuRegister.Rsi] = second;
        context[CpuRegister.Rdx] = count;
        return context;
    }

    private static byte[] CreatePattern(int length)
    {
        var bytes = new byte[length];
        for (var index = 0; index < bytes.Length; index++)
        {
            bytes[index] = (byte)((index * 29) + 7);
        }

        return bytes;
    }

    private sealed class CountingMemory(ICpuMemory inner, int maxTransferSize) : ICpuMemory
    {
        public int ReadCalls { get; private set; }

        public int LargestRead { get; private set; }

        public bool TryRead(ulong virtualAddress, Span<byte> destination)
        {
            ReadCalls++;
            LargestRead = Math.Max(LargestRead, destination.Length);
            return destination.Length <= maxTransferSize && inner.TryRead(virtualAddress, destination);
        }

        public bool TryWrite(ulong virtualAddress, ReadOnlySpan<byte> source) =>
            inner.TryWrite(virtualAddress, source);
    }
}
