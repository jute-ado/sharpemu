// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using SharpEmu.Core.Loader;
using SharpEmu.Core.Memory;
using Xunit;

namespace SharpEmu.Core.Tests;

public sealed class SelfLoaderTests
{
    private const int ElfHeaderSize = 64;
    private const int ProgramHeaderSize = 56;

    [Fact]
    public void LoadsMinimalElf64Image()
    {
        var memory = new VirtualMemory();

        var image = new SelfLoader().Load(CreateElf(), memory);

        Assert.False(image.IsSelf);
        Assert.Empty(image.ProgramHeaders);
        Assert.Empty(image.MappedRegions);
        Assert.Equal(0x0000_0008_0000_0000UL, image.EntryPoint);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(4)]
    [InlineData(ElfHeaderSize - 1)]
    public void RejectsTruncatedElfHeaders(int length)
    {
        var exception = Record.Exception(() =>
            new SelfLoader().Load(new byte[length], new VirtualMemory()));

        Assert.IsType<InvalidDataException>(exception);
    }

    [Fact]
    public void RejectsProgramHeaderOffsetsOutsideTheImage()
    {
        var elf = CreateElf(programHeaderOffset: ulong.MaxValue, programHeaderCount: 1);

        var exception = Record.Exception(() =>
            new SelfLoader().Load(elf, new VirtualMemory()));

        Assert.IsType<InvalidDataException>(exception);
    }

    [Fact]
    public void RejectsTruncatedProgramHeaderTables()
    {
        var elf = CreateElf(
            programHeaderOffset: ElfHeaderSize,
            programHeaderCount: 1);

        Assert.Throws<InvalidDataException>(() =>
            new SelfLoader().Load(elf, new VirtualMemory()));
    }

    [Fact]
    public void RejectsSegmentsWhoseFileSizeExceedsMemorySize()
    {
        var elf = CreateElfWithLoadSegment(
            fileOffset: ElfHeaderSize + ProgramHeaderSize,
            virtualAddress: 0,
            fileSize: 2,
            memorySize: 1,
            payload: [0xAA, 0xBB]);

        Assert.Throws<InvalidDataException>(() =>
            new SelfLoader().Load(elf, new VirtualMemory()));
    }

    [Fact]
    public void RejectsOverflowingVirtualAddressRanges()
    {
        var elf = CreateElfWithLoadSegment(
            fileOffset: ElfHeaderSize + ProgramHeaderSize,
            virtualAddress: ulong.MaxValue - 1,
            fileSize: 1,
            memorySize: 4,
            payload: [0xAA]);

        Assert.Throws<InvalidDataException>(() =>
            new SelfLoader().Load(elf, new VirtualMemory()));
    }

    [Fact]
    public void RejectedImagesDoNotClearExistingGuestMemory()
    {
        var memory = new VirtualMemory();
        memory.Map(
            virtualAddress: 0x1000,
            memorySize: 4,
            fileOffset: 0,
            fileData: new byte[] { 1, 2, 3, 4 },
            protection: ProgramHeaderFlags.Read | ProgramHeaderFlags.Write);
        var malformed = CreateElf();
        malformed[0] = 0;

        Assert.Throws<InvalidDataException>(() =>
            new SelfLoader().Load(malformed, memory));

        var region = Assert.Single(memory.SnapshotRegions());
        Assert.Equal(0x1000UL, region.VirtualAddress);
        Span<byte> contents = stackalloc byte[4];
        Assert.True(memory.TryRead(0x1000, contents));
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, contents.ToArray());
    }

    [Fact]
    public void MutatedElfHeadersNeverLeakArithmeticExceptions()
    {
        var random = new Random(0x5E1F);
        for (var iteration = 0; iteration < 256; iteration++)
        {
            var image = new byte[random.Next(ElfHeaderSize, 512)];
            random.NextBytes(image);
            image[0] = 0x7F;
            image[1] = (byte)'E';
            image[2] = (byte)'L';
            image[3] = (byte)'F';
            image[4] = 2;
            image[5] = 1;
            BinaryPrimitives.WriteUInt16LittleEndian(
                image.AsSpan(54),
                ProgramHeaderSize);
            BinaryPrimitives.WriteUInt16LittleEndian(
                image.AsSpan(56),
                (ushort)random.Next(1, 8));

            var exception = Record.Exception(() =>
                new SelfLoader().Load(image, new VirtualMemory()));

            if (exception is not null)
            {
                Assert.IsType<InvalidDataException>(exception);
            }
        }
    }

    [Fact]
    public void MapsLoadSegmentAndZeroFillsRemainingMemory()
    {
        var elf = CreateElfWithLoadSegment(
            fileOffset: ElfHeaderSize + ProgramHeaderSize,
            virtualAddress: 0x2000,
            fileSize: 4,
            memorySize: 8,
            payload: [1, 2, 3, 4]);
        var memory = new VirtualMemory();

        var image = new SelfLoader().Load(elf, memory);

        var region = Assert.Single(image.MappedRegions);
        Assert.Equal(0x0000_0008_0000_2000UL, region.VirtualAddress);
        Assert.Equal(8UL, region.MemorySize);
        Span<byte> mapped = stackalloc byte[8];
        Assert.True(memory.TryRead(region.VirtualAddress, mapped));
        Assert.Equal(new byte[] { 1, 2, 3, 4, 0, 0, 0, 0 }, mapped.ToArray());
    }

    private static byte[] CreateElf(
        ulong programHeaderOffset = 0,
        ushort programHeaderCount = 0,
        byte abiVersion = 2)
    {
        var image = new byte[ElfHeaderSize];
        image[0] = 0x7F;
        image[1] = (byte)'E';
        image[2] = (byte)'L';
        image[3] = (byte)'F';
        image[4] = 2;
        image[5] = 1;
        image[6] = 1;
        image[8] = abiVersion;
        BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(16), 2);
        BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(18), 0x3E);
        BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(20), 1);
        BinaryPrimitives.WriteUInt64LittleEndian(image.AsSpan(32), programHeaderOffset);
        BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(52), ElfHeaderSize);
        BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(54), ProgramHeaderSize);
        BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(56), programHeaderCount);
        return image;
    }

    private static byte[] CreateElfWithLoadSegment(
        ulong fileOffset,
        ulong virtualAddress,
        ulong fileSize,
        ulong memorySize,
        byte[] payload)
    {
        var image = CreateElf(
            programHeaderOffset: ElfHeaderSize,
            programHeaderCount: 1);
        Array.Resize(ref image, checked((int)fileOffset + payload.Length));
        var header = image.AsSpan(ElfHeaderSize, ProgramHeaderSize);
        BinaryPrimitives.WriteUInt32LittleEndian(header, (uint)ProgramHeaderType.Load);
        BinaryPrimitives.WriteUInt32LittleEndian(header[4..], (uint)(
            ProgramHeaderFlags.Read |
            ProgramHeaderFlags.Execute));
        BinaryPrimitives.WriteUInt64LittleEndian(header[8..], fileOffset);
        BinaryPrimitives.WriteUInt64LittleEndian(header[16..], virtualAddress);
        BinaryPrimitives.WriteUInt64LittleEndian(header[32..], fileSize);
        BinaryPrimitives.WriteUInt64LittleEndian(header[40..], memorySize);
        BinaryPrimitives.WriteUInt64LittleEndian(header[48..], 1);
        payload.CopyTo(image.AsSpan(checked((int)fileOffset)));
        return image;
    }
}
