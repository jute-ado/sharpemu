// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using Xunit;

namespace SharpEmu.Libs.Tests;

public sealed class GuestAddressTests
{
    [Theory]
    [InlineData(0UL, 0UL, 0UL)]
    [InlineData(0x1000UL, 0x20UL, 0x1020UL)]
    [InlineData(ulong.MaxValue - 1, 1UL, ulong.MaxValue)]
    public void TryAddAcceptsRepresentableAddresses(ulong address, ulong offset, ulong expected)
    {
        Assert.True(GuestAddress.TryAdd(address, offset, out var result));
        Assert.Equal(expected, result);
    }

    [Fact]
    public void TryAddRejectsWrappedAddress()
    {
        Assert.False(GuestAddress.TryAdd(ulong.MaxValue, 1, out var result));
        Assert.Equal(0uL, result);
    }

    [Theory]
    [InlineData(0UL, 0UL, true)]
    [InlineData(ulong.MaxValue, 0UL, true)]
    [InlineData(ulong.MaxValue, 1UL, true)]
    [InlineData(ulong.MaxValue - 3, 4UL, true)]
    [InlineData(ulong.MaxValue, 2UL, false)]
    [InlineData(ulong.MaxValue - 3, 5UL, false)]
    public void RangeValidationHandlesEmptyExactAndWrappedRanges(
        ulong address,
        ulong length,
        bool expected)
    {
        Assert.Equal(expected, GuestAddress.IsRangeValid(address, length));
    }
}
