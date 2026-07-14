// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using SharpEmu.Libs.LibcStdio;
using Xunit;

namespace SharpEmu.Libs.Tests;

public sealed class LibcStdioBulkIoTests
{
    [Fact]
    public void FwriteRejectsOverflowingElementProduct()
    {
        var context = new CpuContext(new FakeGuestMemory(), Generation.Gen5);
        context[CpuRegister.Rdi] = 0x1000;
        context[CpuRegister.Rsi] = 1UL << 63;
        context[CpuRegister.Rdx] = 2;
        context[CpuRegister.Rcx] = ulong.MaxValue;

        var result = LibcStdioExports.Fwrite(context);

        Assert.Equal(
            (int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT,
            result);
        Assert.Equal(0UL, context[CpuRegister.Rax]);
    }

    [Theory]
    [InlineData(0UL, ulong.MaxValue, 0UL)]
    [InlineData(ulong.MaxValue, 0UL, 0UL)]
    [InlineData(1UL, ulong.MaxValue, ulong.MaxValue)]
    [InlineData(ulong.MaxValue, 1UL, ulong.MaxValue)]
    [InlineData(6_148_914_691_236_517_205UL, 3UL, ulong.MaxValue)]
    [InlineData(1UL << 63, 1UL, 1UL << 63)]
    public void TotalByteCountAcceptsRepresentableProducts(
        ulong elementSize,
        ulong elementCount,
        ulong expected)
    {
        Assert.True(LibcStdioExports.TryGetTotalByteCount(
            elementSize,
            elementCount,
            out var actual));
        Assert.Equal(expected, actual);
    }

    [Theory]
    [InlineData(ulong.MaxValue, 2UL)]
    [InlineData(1UL << 63, 2UL)]
    [InlineData((ulong.MaxValue / 3) + 1, 3UL)]
    public void TotalByteCountRejectsOverflowingProducts(
        ulong elementSize,
        ulong elementCount)
    {
        Assert.False(LibcStdioExports.TryGetTotalByteCount(
            elementSize,
            elementCount,
            out var actual));
        Assert.Equal(0UL, actual);
    }

    [Theory]
    [InlineData(0UL, 0UL, 0, 0UL)]
    [InlineData(0UL, ulong.MaxValue, 0, ulong.MaxValue)]
    [InlineData(0UL, ulong.MaxValue, 1, ulong.MaxValue)]
    [InlineData(ulong.MaxValue, 0UL, 1, ulong.MaxValue)]
    [InlineData(ulong.MaxValue - 1, 1UL, 1, ulong.MaxValue)]
    [InlineData(100UL, 20UL, 30, 120UL)]
    public void GuestRangeAcceptsCompleteRepresentableRanges(
        ulong baseAddress,
        ulong offset,
        int length,
        ulong expectedAddress)
    {
        Assert.True(LibcStdioExports.TryGetGuestRangeAddress(
            baseAddress,
            offset,
            length,
            out var actualAddress));
        Assert.Equal(expectedAddress, actualAddress);
    }

    [Theory]
    [InlineData(ulong.MaxValue, 1UL, 0)]
    [InlineData(ulong.MaxValue, 0UL, 2)]
    [InlineData(ulong.MaxValue - 1, 1UL, 2)]
    [InlineData(ulong.MaxValue - 10, 5UL, 7)]
    [InlineData(0UL, 0UL, -1)]
    public void GuestRangeRejectsWrappedOrInvalidRanges(
        ulong baseAddress,
        ulong offset,
        int length)
    {
        Assert.False(LibcStdioExports.TryGetGuestRangeAddress(
            baseAddress,
            offset,
            length,
            out var address));
        Assert.Equal(0UL, address);
    }
}
