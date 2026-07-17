// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Core.Cpu.Native;
using SharpEmu.HLE;
using Xunit;

namespace SharpEmu.Core.Tests;

public sealed class ImportResultLoggingTests
{
    private const string PlayGoGetLocusNid = "uWIYLFkkwqk";
    private static readonly OrbisGen2Result PlayGoBadChunkId =
        (OrbisGen2Result)unchecked((int)0x80B2000C);

    [Fact]
    public void PlayGoGetLocusBadChunkIdIsExpectedControlFlow()
    {
        Assert.True(
            DirectExecutionBackend.IsExpectedImportResult(
                PlayGoGetLocusNid,
                PlayGoBadChunkId));
    }

    [Theory]
    [InlineData("different-nid", unchecked((int)0x80B2000C))]
    [InlineData(PlayGoGetLocusNid, unchecked((int)0x80B20004))]
    public void PlayGoExpectationRequiresMatchingNidAndResult(string nid, int result)
    {
        Assert.False(
            DirectExecutionBackend.IsExpectedImportResult(
                nid,
                (OrbisGen2Result)result));
    }

    [Fact]
    public void FopenNotFoundIsExpectedFileProbeControlFlow()
    {
        Assert.True(
            DirectExecutionBackend.IsExpectedImportResult(
                "xeYO4u7uyJ0",
                OrbisGen2Result.ORBIS_GEN2_ERROR_NOT_FOUND));
    }

    [Fact]
    public void DirectMemoryTryAgainIsExpectedAllocationProbeControlFlow()
    {
        Assert.True(
            DirectExecutionBackend.IsExpectedImportResult(
                "B+vc2AO2Zrc",
                OrbisGen2Result.ORBIS_GEN2_ERROR_TRY_AGAIN));
    }
}
