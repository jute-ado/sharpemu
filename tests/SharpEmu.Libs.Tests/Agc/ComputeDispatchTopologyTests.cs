// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.Agc;
using Xunit;

namespace SharpEmu.Libs.Tests.Agc;

public sealed class ComputeDispatchTopologyTests
{
    [Fact]
    public void GroupEndCoordinatesAreRelativeToTheProgrammedBase()
    {
        var topology = Resolve(
            endX: 13,
            endY: 9,
            endZ: 4,
            initiator: 1,
            baseX: 10,
            baseY: 7,
            baseZ: 3);

        Assert.Equal((3u, 2u, 1u), topology.GroupCounts);
        Assert.Equal((10u, 7u, 3u), topology.BaseGroups);
        Assert.Equal((uint.MaxValue, uint.MaxValue, uint.MaxValue), topology.ThreadLimits);
        Assert.Equal(64u, topology.WaveLaneCount);
    }

    [Fact]
    public void ForceStartAtZeroIgnoresProgrammedBase()
    {
        var topology = Resolve(
            endX: 3,
            endY: 2,
            endZ: 1,
            initiator: 1 | (1u << 2),
            baseX: 100,
            baseY: 200,
            baseZ: 300);

        Assert.Equal((3u, 2u, 1u), topology.GroupCounts);
        Assert.Equal((0u, 0u, 0u), topology.BaseGroups);
    }

    [Fact]
    public void ThreadDimensionsRoundUpAndPreserveExactExclusiveLimits()
    {
        var topology = Resolve(
            endX: 20,
            endY: 19,
            endZ: 7,
            initiator: 1 | (1u << 5) | (1u << 15),
            baseX: 2,
            baseY: 1,
            baseZ: 1,
            localX: 8,
            localY: 8,
            localZ: 4);

        Assert.Equal((1u, 2u, 1u), topology.GroupCounts);
        Assert.Equal((2u, 1u, 1u), topology.BaseGroups);
        Assert.Equal((20u, 19u, 7u), topology.ThreadLimits);
        Assert.Equal(32u, topology.WaveLaneCount);
    }

    [Fact]
    public void PartialFinalGroupsProduceExactExclusiveLimits()
    {
        var topology = Resolve(
            endX: 3,
            endY: 4,
            endZ: 2,
            initiator: 1 | (1u << 1),
            baseX: 1,
            baseY: 2,
            baseZ: 1,
            localX: 8,
            localY: 4,
            localZ: 2,
            partialX: 4,
            partialY: 3,
            partialZ: 1);

        Assert.Equal((2u, 2u, 1u), topology.GroupCounts);
        Assert.Equal((20u, 15u, 3u), topology.ThreadLimits);
    }

    [Theory]
    [InlineData(0u, 4u)]
    [InlineData(9u, 4u)]
    public void InvalidPartialGroupSizesAreRejected(
        uint partialX,
        uint localX)
    {
        Assert.False(
            AgcExports.TryResolveComputeDispatchTopology(
                new AgcExports.ComputeDispatchArguments(
                    1,
                    1,
                    1,
                    1 | (1u << 1),
                    0,
                    0,
                    0,
                    localX,
                    1,
                    1,
                    partialX,
                    1,
                    1,
                    IsIndirect: false),
                out _,
                out var error));
        Assert.Contains("partial", error, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(0u, 0u, 4u)]
    [InlineData(4u, 4u, 4u)]
    public void GroupEndMustBeAfterBase(
        uint endX,
        uint baseX,
        uint localX)
    {
        Assert.False(
            AgcExports.TryResolveComputeDispatchTopology(
                new AgcExports.ComputeDispatchArguments(
                    endX,
                    1,
                    1,
                    1,
                    baseX,
                    0,
                    0,
                    localX,
                    1,
                    1,
                    0,
                    0,
                    0,
                    IsIndirect: false),
                out _,
                out var error));
        Assert.Contains("end", error, StringComparison.Ordinal);
    }

    [Fact]
    public void ThreadEndMustBeAfterBaseThread()
    {
        Assert.False(
            AgcExports.TryResolveComputeDispatchTopology(
                new AgcExports.ComputeDispatchArguments(
                    16,
                    1,
                    1,
                    1 | (1u << 5),
                    2,
                    0,
                    0,
                    8,
                    1,
                    1,
                    0,
                    0,
                    0,
                    IsIndirect: true),
                out _,
                out var error));
        Assert.Contains("thread end", error, StringComparison.Ordinal);
    }

    private static AgcExports.ComputeDispatchTopology Resolve(
        uint endX,
        uint endY,
        uint endZ,
        uint initiator,
        uint baseX = 0,
        uint baseY = 0,
        uint baseZ = 0,
        uint localX = 1,
        uint localY = 1,
        uint localZ = 1,
        uint partialX = 0,
        uint partialY = 0,
        uint partialZ = 0)
    {
        Assert.True(
            AgcExports.TryResolveComputeDispatchTopology(
                new AgcExports.ComputeDispatchArguments(
                    endX,
                    endY,
                    endZ,
                    initiator,
                    baseX,
                    baseY,
                    baseZ,
                    localX,
                    localY,
                    localZ,
                    partialX,
                    partialY,
                    partialZ,
                    IsIndirect: false),
                out var topology,
                out var error),
            error);
        return topology;
    }
}
