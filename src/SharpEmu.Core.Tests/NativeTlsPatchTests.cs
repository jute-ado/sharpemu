// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Core.Cpu.Native;
using SharpEmu.Core.Loader;
using SharpEmu.Core.Memory;
using Xunit;

namespace SharpEmu.Core.Tests;

public sealed class NativeTlsPatchTests
{
    [Fact]
    public void CandidateScanRejectsTlsPatternStartingInsidePriorInstruction()
    {
        byte[] bytes =
        [
            0x48, 0x8D, 0x1C, 0x64,
            0x48, 0x8B, 0x04, 0x25, 0x00, 0x00, 0x00, 0x00,
        ];
        Assert.True(
            NativeTlsInstructionDecoder.TryDecode(
                bytes.AsSpan(3),
                out var overlappingTlsLoad));
        Assert.Equal(NativeTlsInstructionKind.Load, overlappingTlsLoad.Kind);

        var candidates = DirectExecutionBackend.GetTlsPatchCandidates(
            bytes,
            bytes.Length,
            out var consumedLength);

        Assert.Empty(candidates);
        Assert.Equal(bytes.Length, consumedLength);
    }

    [Fact]
    public void CandidateScanFindsTlsPatternAtInstructionBoundary()
    {
        byte[] bytes =
        [
            0x48, 0x8D, 0x1C, 0x24,
            0x64, 0x48, 0x8B, 0x04, 0x25, 0x00, 0x00, 0x00, 0x00,
            0x90,
        ];

        var candidates = DirectExecutionBackend.GetTlsPatchCandidates(
            bytes,
            bytes.Length,
            out var consumedLength);

        var candidate = Assert.Single(candidates);
        Assert.Equal(4, candidate.Offset);
        Assert.Equal(NativeTlsInstructionKind.Load, candidate.Instruction.Kind);
        Assert.Equal(9, candidate.Instruction.Length);
        Assert.Equal(bytes.Length, consumedLength);
    }

    [Fact]
    public void CandidateScanConsumesInstructionAcrossChunkOwnershipBoundary()
    {
        byte[] bytes =
        [
            0x90, 0x90,
            0x64, 0x48, 0x8B, 0x04, 0x25, 0x00, 0x00, 0x00, 0x00,
        ];

        var candidates = DirectExecutionBackend.GetTlsPatchCandidates(
            bytes,
            ownedLength: 4,
            out var consumedLength);

        var candidate = Assert.Single(candidates);
        Assert.Equal(2, candidate.Offset);
        Assert.Equal(bytes.Length, consumedLength);
    }

    [Fact]
    public void RegisteredEntryScansOnlyExecutableRegionsOwnedByItsImage()
    {
        const ulong mainCodeAddress = 0x0000_0008_0000_0000UL;
        const ulong moduleCodeAddress = 0x0000_0008_0400_1000UL;
        const ulong moduleDataAddress = 0x0000_0008_0400_2000UL;
        const ulong moduleLateCodeAddress = 0x0000_0008_0400_3000UL;
        var memory = new VirtualMemory();
        var mainCode = MapRegion(
            memory,
            mainCodeAddress,
            ProgramHeaderFlags.Read | ProgramHeaderFlags.Execute);
        memory.RegisterImage([mainCode]);
        var moduleCode = MapRegion(
            memory,
            moduleCodeAddress,
            ProgramHeaderFlags.Read | ProgramHeaderFlags.Execute);
        var moduleData = MapRegion(
            memory,
            moduleDataAddress,
            ProgramHeaderFlags.Read | ProgramHeaderFlags.Write);
        var moduleLateCode = MapRegion(
            memory,
            moduleLateCodeAddress,
            ProgramHeaderFlags.Read | ProgramHeaderFlags.Execute);
        memory.RegisterImage([moduleCode, moduleData, moduleLateCode]);

        var ranges = DirectExecutionBackend.GetTlsPatchScanRegions(
            memory,
            moduleLateCodeAddress);

        Assert.Collection(
            ranges,
            first =>
            {
                Assert.Equal(moduleCodeAddress, first.Start);
                Assert.Equal(moduleCodeAddress + 0x100, first.End);
            },
            second =>
            {
                Assert.Equal(moduleLateCodeAddress, second.Start);
                Assert.Equal(moduleLateCodeAddress + 0x100, second.End);
            });
    }

    [Fact]
    public void UnregisteredEntryKeepsBoundedFallbackScan()
    {
        const ulong entryPoint = 0x0000_0008_0000_0070UL;

        var ranges = DirectExecutionBackend.GetTlsPatchScanRegions(
            new VirtualMemory(),
            entryPoint);

        var range = Assert.Single(ranges);
        Assert.Equal(entryPoint, range.Start);
        Assert.Equal(entryPoint + 0x0200_0000UL, range.End);
    }

    [Fact]
    public void AddressInGapBetweenImageRegionsUsesFallbackScan()
    {
        const ulong firstCodeAddress = 0x0000_0008_0000_1000UL;
        const ulong secondCodeAddress = 0x0000_0008_0000_3000UL;
        const ulong gapAddress = 0x0000_0008_0000_2000UL;
        var memory = new VirtualMemory();
        var firstCode = MapRegion(
            memory,
            firstCodeAddress,
            ProgramHeaderFlags.Read | ProgramHeaderFlags.Execute);
        var secondCode = MapRegion(
            memory,
            secondCodeAddress,
            ProgramHeaderFlags.Read | ProgramHeaderFlags.Execute);
        memory.RegisterImage([firstCode, secondCode]);

        var ranges = DirectExecutionBackend.GetTlsPatchScanRegions(
            memory,
            gapAddress);

        var range = Assert.Single(ranges);
        Assert.Equal(gapAddress, range.Start);
        Assert.Equal(gapAddress + 0x0200_0000UL, range.End);
    }

    [Fact]
    public void FallbackScanDoesNotOverflowAddressSpace()
    {
        var ranges = DirectExecutionBackend.GetTlsPatchScanRegions(
            new VirtualMemory(),
            ulong.MaxValue - 0x1000UL);

        var range = Assert.Single(ranges);
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

    private static VirtualMemoryRegion MapRegion(
        VirtualMemory memory,
        ulong address,
        ProgramHeaderFlags protection)
    {
        memory.Map(
            address,
            0x100,
            fileOffset: 0,
            ReadOnlySpan<byte>.Empty,
            protection);
        return new VirtualMemoryRegion(
            address,
            0x100,
            fileOffset: 0,
            fileSize: 0,
            protection);
    }
}
