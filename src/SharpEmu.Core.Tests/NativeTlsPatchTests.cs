// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Core.Cpu.Native;
using Xunit;

namespace SharpEmu.Core.Tests;

public sealed class NativeTlsPatchTests
{
    [Theory]
    [InlineData(0x0000_0008_0000_0070UL)]
    [InlineData(0x0000_0008_0540_C7D0UL)]
    [InlineData(0x0000_0008_07FF_FFFFUL)]
    public void CanonicalGuestEntryScansEntireGuestImageRange(ulong entryPoint)
    {
        var range = DirectExecutionBackend.GetTlsPatchScanRange(entryPoint);

        Assert.Equal(0x0000_0008_0000_0000UL, range.Start);
        Assert.Equal(0x0000_0008_8000_0000UL, range.End);
    }

    [Fact]
    public void NoncanonicalEntryKeepsBoundedFallbackScan()
    {
        const ulong entryPoint = 0x0000_0200_0000_0000UL;

        var range = DirectExecutionBackend.GetTlsPatchScanRange(entryPoint);

        Assert.Equal(entryPoint, range.Start);
        Assert.Equal(entryPoint + 0x0200_0000UL, range.End);
    }

    [Fact]
    public void FallbackScanDoesNotOverflowAddressSpace()
    {
        var range = DirectExecutionBackend.GetTlsPatchScanRange(ulong.MaxValue - 0x1000UL);

        Assert.Equal(ulong.MaxValue - 0x1000UL, range.Start);
        Assert.Equal(ulong.MaxValue, range.End);
    }

    [Theory]
    [InlineData(0x1000UL, 0x2000UL, 0x2000UL)]
    [InlineData(0x0000_0008_0000_0000UL, 0x0000_0008_8000_0000UL, 0x0000_0008_0100_0000UL)]
    [InlineData(ulong.MaxValue - 0x0200_0000UL, ulong.MaxValue, ulong.MaxValue - 0x0100_0000UL)]
    [InlineData(0x2000UL, 0x1000UL, 0x2000UL)]
    public void ExecutableScanChunksHaveBoundedNonoverflowingEnds(
        ulong regionStart,
        ulong regionEnd,
        ulong expectedEnd)
    {
        Assert.Equal(
            expectedEnd,
            DirectExecutionBackend.GetTlsScanChunkEnd(regionStart, regionEnd));
    }
}
