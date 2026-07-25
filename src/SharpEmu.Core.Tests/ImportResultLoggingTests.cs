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

    [Theory]
    [InlineData("9UK1vLZQft4", unchecked((int)0x8002000B))]
    [InlineData("gEpBkcwxUjw", -1)]
    [InlineData("ZP4e7rlzOUk", unchecked((int)0x809F0008))]
    public void RuntimeProbeControlResultsAreExpected(string nid, int result)
    {
        Assert.True(
            DirectExecutionBackend.IsExpectedImportResult(
                nid,
                (OrbisGen2Result)result));
    }

    [Theory]
    [InlineData("different-nid", unchecked((int)0x8002000B))]
    [InlineData("gEpBkcwxUjw", unchecked((int)0x80020016))]
    [InlineData("ZP4e7rlzOUk", unchecked((int)0x809F0007))]
    public void RuntimeProbeExpectationsRequireMatchingNidAndResult(
        string nid,
        int result)
    {
        Assert.False(
            DirectExecutionBackend.IsExpectedImportResult(
                nid,
                (OrbisGen2Result)result));
    }

    [Theory]
    [InlineData("1-LFLmRFxxM", unchecked((int)0x80020011))]
    [InlineData("u1GRHp+oWoY", unchecked((int)0x80920007))]
    public void FilesystemAndControllerProbeResultsAreExpected(
        string nid,
        int result)
    {
        Assert.True(
            DirectExecutionBackend.IsExpectedImportResult(
                nid,
                (OrbisGen2Result)result));
    }

    [Theory]
    [InlineData("different-nid", unchecked((int)0x80020011))]
    [InlineData("1-LFLmRFxxM", unchecked((int)0x80020002))]
    [InlineData("different-nid", unchecked((int)0x80920007))]
    [InlineData("u1GRHp+oWoY", unchecked((int)0x80920008))]
    public void FilesystemAndControllerProbeExpectationsRequireMatchingNidAndResult(
        string nid,
        int result)
    {
        Assert.False(
            DirectExecutionBackend.IsExpectedImportResult(
                nid,
                (OrbisGen2Result)result));
    }

    [Fact]
    public void PthreadSemaphoreTryAgainIsExpectedControlFlow()
    {
        Assert.True(
            DirectExecutionBackend.IsExpectedImportResult(
                "H2a+IN9TP0E",
                OrbisGen2Result.ORBIS_GEN2_ERROR_TRY_AGAIN));
    }

    [Fact]
    public void KernelPollSemaphoreBusyIsExpectedControlFlow()
    {
        Assert.True(
            DirectExecutionBackend.IsExpectedImportResult(
                "12wOHk8ywb0",
                OrbisGen2Result.ORBIS_GEN2_ERROR_BUSY));
    }

    [Theory]
    [InlineData("different-nid", (int)OrbisGen2Result.ORBIS_GEN2_ERROR_BUSY)]
    [InlineData("12wOHk8ywb0", (int)OrbisGen2Result.ORBIS_GEN2_ERROR_TRY_AGAIN)]
    public void KernelPollSemaphoreExpectationRequiresMatchingNidAndResult(
        string nid,
        int result)
    {
        Assert.False(
            DirectExecutionBackend.IsExpectedImportResult(
                nid,
                (OrbisGen2Result)result));
    }

    [Fact]
    public void KernelWaitSemaphoreTimeoutIsExpectedControlFlow()
    {
        Assert.True(
            DirectExecutionBackend.IsExpectedImportResult(
                "Zxa0VhQVTsk",
                OrbisGen2Result.ORBIS_GEN2_ERROR_TIMED_OUT));
    }

    [Theory]
    [InlineData(
        "different-nid",
        (int)OrbisGen2Result.ORBIS_GEN2_ERROR_TIMED_OUT)]
    [InlineData(
        "Zxa0VhQVTsk",
        (int)OrbisGen2Result.ORBIS_GEN2_ERROR_BUSY)]
    public void KernelWaitSemaphoreExpectationRequiresMatchingNidAndResult(
        string nid,
        int result)
    {
        Assert.False(
            DirectExecutionBackend.IsExpectedImportResult(
                nid,
                (OrbisGen2Result)result));
    }

    [Theory]
    [InlineData("BmMjYxmew1w", (int)OrbisGen2Result.ORBIS_GEN2_ERROR_TIMED_OUT)]
    [InlineData("upoVrzMHFeE", (int)OrbisGen2Result.ORBIS_GEN2_ERROR_BUSY)]
    public void CanonicalPthreadControlResultsAreExpected(string nid, int result)
    {
        Assert.True(
            DirectExecutionBackend.IsExpectedImportResult(
                nid,
                (OrbisGen2Result)result));
    }

    [Theory]
    [InlineData("BmMjYxmew1w", (int)OrbisGen2Result.ORBIS_GEN2_ERROR_BUSY)]
    [InlineData("upoVrzMHFeE", (int)OrbisGen2Result.ORBIS_GEN2_ERROR_TIMED_OUT)]
    public void CanonicalPthreadExpectationsRequireMatchingResults(string nid, int result)
    {
        Assert.False(
            DirectExecutionBackend.IsExpectedImportResult(
                nid,
                (OrbisGen2Result)result));
    }

    [Theory]
    [InlineData(true, false, true)]
    [InlineData(false, true, true)]
    [InlineData(true, true, true)]
    [InlineData(false, false, false)]
    public void TeardownImportResultsAreNotCompatibilityWarnings(
        bool backendTeardownRequested,
        bool blockingShutdownRequested,
        bool expected)
    {
        Assert.Equal(
            expected,
            DirectExecutionBackend.ShouldSuppressImportResultDuringTeardown(
                backendTeardownRequested,
                blockingShutdownRequested));
    }
}
