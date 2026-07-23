// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using SharpEmu.HLE;
using SharpEmu.Libs.Kernel;
using Xunit;

namespace SharpEmu.Libs.Tests;

[Collection(KernelMemorySessionStateCollection.Name)]
public sealed class KernelMunmapRangeTests : IDisposable
{
    private const ulong OutputAddress = 0x1000;

    public KernelMunmapRangeTests()
    {
        KernelMemoryLifecycle.ResetRuntimeState();
    }

    public void Dispose()
    {
        KernelMemoryLifecycle.ResetRuntimeState();
    }

    [Fact]
    public void MunmapRemovesContainedRangeAndPreservesReservationEdges()
    {
        const ulong reservationStart = 0x0000_0013_C000_0000;
        const ulong reservationLength = 0x0350_0000;
        const ulong unmapStart = reservationStart + 0x0014_C000;
        const ulong unmapLength = 0x0320_0000;
        var unmapEnd = unmapStart + unmapLength;
        var reservationEnd = reservationStart + reservationLength;
        var context = CreateContext();
        KernelMemoryCompatExports.RegisterReservedVirtualRange(
            reservationStart,
            reservationLength);

        context[CpuRegister.Rdi] = unmapStart;
        context[CpuRegister.Rsi] = unmapLength;

        Assert.Equal(
            (int)OrbisGen2Result.ORBIS_GEN2_OK,
            KernelMemoryCompatExports.KernelMunmap(context));
        AssertRegion(context, reservationStart, reservationStart, unmapStart);
        Assert.Equal(
            (int)OrbisGen2Result.ORBIS_GEN2_ERROR_DELETED,
            QueryRegion(context, unmapStart, out _, out _));
        AssertRegion(context, unmapEnd, unmapEnd, reservationEnd);
    }

    [Fact]
    public void PartialFlexibleUnmapRestoresOnlyReleasedCapacity()
    {
        const ulong mappingAddress = 0x0000_0006_0000_0000;
        const ulong mappingLength = 0xC000;
        const ulong releasedLength = 0x4000;
        var context = CreateContext();
        var configuredCapacity = QueryAvailableFlexibleMemory(context);

        Assert.True(context.TryWriteUInt64(OutputAddress, mappingAddress));
        context[CpuRegister.Rdi] = OutputAddress;
        context[CpuRegister.Rsi] = mappingLength;
        context[CpuRegister.Rdx] = 0x03;
        context[CpuRegister.Rcx] = 0x10;
        Assert.Equal(
            (int)OrbisGen2Result.ORBIS_GEN2_OK,
            KernelMemoryCompatExports.KernelMapNamedFlexibleMemory(context));
        Assert.Equal(
            configuredCapacity - mappingLength,
            QueryAvailableFlexibleMemory(context));

        context[CpuRegister.Rdi] = mappingAddress + releasedLength;
        context[CpuRegister.Rsi] = releasedLength;
        Assert.Equal(
            (int)OrbisGen2Result.ORBIS_GEN2_OK,
            KernelMemoryCompatExports.KernelMunmap(context));

        Assert.Equal(
            configuredCapacity - (mappingLength - releasedLength),
            QueryAvailableFlexibleMemory(context));
    }

    [Fact]
    public void FixedFlexibleRemapDoesNotConsumeCapacityTwice()
    {
        const ulong mappingAddress = 0x0000_0006_1000_0000;
        const ulong mappingLength = 0xC000;
        var context = CreateContext();
        var configuredCapacity = QueryAvailableFlexibleMemory(context);

        AssertFlexibleMap(context, mappingAddress, mappingLength);
        AssertFlexibleMap(context, mappingAddress, mappingLength);

        Assert.Equal(
            configuredCapacity - mappingLength,
            QueryAvailableFlexibleMemory(context));
    }

    [Fact]
    public void PartiallyOverlappingFixedFlexibleMapConsumesOnlyNewBytes()
    {
        const ulong mappingAddress = 0x0000_0006_2000_0000;
        var context = CreateContext();
        var configuredCapacity = QueryAvailableFlexibleMemory(context);

        AssertFlexibleMap(context, mappingAddress, 0xC000);
        AssertFlexibleMap(context, mappingAddress + 0x8000, 0x8000);

        Assert.Equal(
            configuredCapacity - 0x10000,
            QueryAvailableFlexibleMemory(context));
    }

    [Fact]
    public void FlexibleMapBeyondAvailableCapacityFailsWithoutConsumingBudget()
    {
        const ulong configuredCapacity = 0x8000;
        const ulong firstAddress = 0x0000_0006_3000_0000;
        const ulong rejectedAddress = 0x0000_0006_4000_0000;
        var context = CreateContext();
        KernelMemoryLifecycle.ConfigureFlexibleMemorySize(configuredCapacity);
        AssertFlexibleMap(context, firstAddress, 0x6000);

        Assert.True(context.TryWriteUInt64(OutputAddress, rejectedAddress));
        context[CpuRegister.Rdi] = OutputAddress;
        context[CpuRegister.Rsi] = 0x4000;
        context[CpuRegister.Rdx] = 0x03;
        context[CpuRegister.Rcx] = 0x10;

        Assert.Equal(
            (int)OrbisGen2Result.ORBIS_GEN2_ERROR_NOT_FOUND,
            KernelMemoryCompatExports.KernelMapNamedFlexibleMemory(context));
        Assert.Equal(0x2000UL, QueryAvailableFlexibleMemory(context));
        Assert.Equal(
            (int)OrbisGen2Result.ORBIS_GEN2_ERROR_DELETED,
            QueryRegion(context, rejectedAddress, out _, out _));
    }

    [Fact]
    public void ConfiguredFlexibleMemorySizeControlsAvailableCapacity()
    {
        const ulong configuredSize = 438UL * 1024 * 1024;
        var context = CreateContext();

        KernelMemoryLifecycle.ConfigureFlexibleMemorySize(configuredSize);

        Assert.Equal(configuredSize, QueryConfiguredFlexibleMemory(context));
        Assert.Equal(configuredSize, QueryAvailableFlexibleMemory(context));
    }

    [Fact]
    public void StartupReservationReducesAvailableCapacityAndBlocksReconfiguration()
    {
        const ulong configuredCapacity = 0x20_0000;
        const ulong startupReservation = 0x10_4000;
        var context = CreateContext();
        KernelMemoryLifecycle.ConfigureFlexibleMemorySize(configuredCapacity);

        Assert.True(KernelMemoryLifecycle.TryReserveStartupFlexibleMemory(startupReservation));

        Assert.Equal(
            configuredCapacity - startupReservation,
            QueryAvailableFlexibleMemory(context));
        Assert.Throws<InvalidOperationException>(
            () => KernelMemoryLifecycle.ConfigureFlexibleMemorySize(configuredCapacity + 0x4000));
    }

    [Fact]
    public void RejectedStartupReservationLeavesCapacityUnchanged()
    {
        const ulong configuredCapacity = 0x20_0000;
        var context = CreateContext();
        KernelMemoryLifecycle.ConfigureFlexibleMemorySize(configuredCapacity);

        Assert.False(
            KernelMemoryLifecycle.TryReserveStartupFlexibleMemory(configuredCapacity + 1));

        Assert.Equal(configuredCapacity, QueryAvailableFlexibleMemory(context));
        KernelMemoryLifecycle.ConfigureFlexibleMemorySize(configuredCapacity + 0x4000);
    }

    [Fact]
    public void ResetRestoresDefaultFlexibleMemoryCapacity()
    {
        var context = CreateContext();
        var defaultSize = QueryConfiguredFlexibleMemory(context);
        KernelMemoryLifecycle.ConfigureFlexibleMemorySize(438UL * 1024 * 1024);

        KernelMemoryLifecycle.ResetRuntimeState();

        Assert.Equal(defaultSize, QueryConfiguredFlexibleMemory(context));
        Assert.Equal(defaultSize, QueryAvailableFlexibleMemory(context));
    }

    [Fact]
    public void MunmapCanSpanAdjacentTrackedRegions()
    {
        const ulong reservationStart = 0x0000_0012_8000_0000;
        const ulong regionLength = 0x8000;
        const ulong unmapStart = reservationStart + 0x4000;
        const ulong unmapLength = 0x8000;
        var context = CreateContext();
        KernelMemoryCompatExports.RegisterReservedVirtualRange(
            reservationStart,
            regionLength);
        KernelMemoryCompatExports.RegisterReservedVirtualRange(
            reservationStart + regionLength,
            regionLength);

        context[CpuRegister.Rdi] = unmapStart;
        context[CpuRegister.Rsi] = unmapLength;

        Assert.Equal(
            (int)OrbisGen2Result.ORBIS_GEN2_OK,
            KernelMemoryCompatExports.KernelMunmap(context));
        AssertRegion(
            context,
            reservationStart,
            reservationStart,
            unmapStart);
        AssertRegion(
            context,
            unmapStart + unmapLength,
            unmapStart + unmapLength,
            reservationStart + (2 * regionLength));
    }

    [Fact]
    public void MunmapWithTrackingGapFailsWithoutChangingCoveredRegions()
    {
        const ulong reservationStart = 0x0000_0012_9000_0000;
        const ulong regionLength = 0x4000;
        const ulong secondRegionStart = reservationStart + 0x8000;
        var context = CreateContext();
        KernelMemoryCompatExports.RegisterReservedVirtualRange(
            reservationStart,
            regionLength);
        KernelMemoryCompatExports.RegisterReservedVirtualRange(
            secondRegionStart,
            regionLength);

        context[CpuRegister.Rdi] = reservationStart;
        context[CpuRegister.Rsi] = 0xC000;

        Assert.Equal(
            (int)OrbisGen2Result.ORBIS_GEN2_ERROR_NOT_FOUND,
            KernelMemoryCompatExports.KernelMunmap(context));
        AssertRegion(
            context,
            reservationStart,
            reservationStart,
            reservationStart + regionLength);
        AssertRegion(
            context,
            secondRegionStart,
            secondRegionStart,
            secondRegionStart + regionLength);
    }

    private static CpuContext CreateContext()
    {
        var memory = new FakeGuestMemory();
        memory.AddRegion(OutputAddress, new byte[72]);
        return new CpuContext(memory, Generation.Gen5);
    }

    private static void AssertFlexibleMap(CpuContext context, ulong address, ulong length)
    {
        Assert.True(context.TryWriteUInt64(OutputAddress, address));
        context[CpuRegister.Rdi] = OutputAddress;
        context[CpuRegister.Rsi] = length;
        context[CpuRegister.Rdx] = 0x03;
        context[CpuRegister.Rcx] = 0x10;
        Assert.Equal(
            (int)OrbisGen2Result.ORBIS_GEN2_OK,
            KernelMemoryCompatExports.KernelMapNamedFlexibleMemory(context));
    }

    private static ulong QueryAvailableFlexibleMemory(CpuContext context)
    {
        context[CpuRegister.Rdi] = OutputAddress;
        Assert.Equal(
            (int)OrbisGen2Result.ORBIS_GEN2_OK,
            KernelMemoryCompatExports.KernelAvailableFlexibleMemorySize(context));
        Span<byte> value = stackalloc byte[sizeof(ulong)];
        Assert.True(context.Memory.TryRead(OutputAddress, value));
        return BinaryPrimitives.ReadUInt64LittleEndian(value);
    }

    private static ulong QueryConfiguredFlexibleMemory(CpuContext context)
    {
        context[CpuRegister.Rdi] = OutputAddress;
        Assert.Equal(
            (int)OrbisGen2Result.ORBIS_GEN2_OK,
            KernelMemoryCompatExports.KernelConfiguredFlexibleMemorySize(context));
        Span<byte> value = stackalloc byte[sizeof(ulong)];
        Assert.True(context.Memory.TryRead(OutputAddress, value));
        return BinaryPrimitives.ReadUInt64LittleEndian(value);
    }

    private static void AssertRegion(
        CpuContext context,
        ulong queryAddress,
        ulong expectedStart,
        ulong expectedEnd)
    {
        Assert.Equal(
            (int)OrbisGen2Result.ORBIS_GEN2_OK,
            QueryRegion(context, queryAddress, out var start, out var end));
        Assert.Equal(expectedStart, start);
        Assert.Equal(expectedEnd, end);
    }

    private static int QueryRegion(
        CpuContext context,
        ulong address,
        out ulong start,
        out ulong end)
    {
        context[CpuRegister.Rdi] = address;
        context[CpuRegister.Rsi] = 0;
        context[CpuRegister.Rdx] = OutputAddress;
        context[CpuRegister.Rcx] = 72;
        var result = KernelMemoryCompatExports.KernelVirtualQuery(context);
        Span<byte> payload = stackalloc byte[16];
        Assert.True(context.Memory.TryRead(OutputAddress, payload));
        start = BinaryPrimitives.ReadUInt64LittleEndian(payload[..8]);
        end = BinaryPrimitives.ReadUInt64LittleEndian(payload[8..]);
        return result;
    }
}
