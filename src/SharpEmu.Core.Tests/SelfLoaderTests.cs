// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using SharpEmu.Core.Cpu;
using SharpEmu.Core.Loader;
using SharpEmu.Core.Memory;
using SharpEmu.Core.Runtime;
using SharpEmu.HLE;
using Xunit;

namespace SharpEmu.Core.Tests;

[Collection(PhysicalVirtualMemoryTestCollection.Name)]
public sealed class SelfLoaderTests
{
    private const int ElfHeaderSize = 64;
    private const int ProgramHeaderSize = 56;
    private const int SelfHeaderSize = 32;
    private const int SelfSegmentSize = 32;
    private const int SyntheticSelfPayloadOffset = 0x200;
    private const int DynamicEntrySize = 16;
    private const long DynamicTagInit = 0x0C;
    private const long DynamicTagInitArray = 0x19;
    private const long DynamicTagInitArraySize = 0x1B;
    private const long DynamicTagStringTable = 0x05;
    private const long DynamicTagSymbolTable = 0x06;
    private const long DynamicTagRela = 0x07;
    private const long DynamicTagRelaSize = 0x08;
    private const long DynamicTagStringTableSize = 0x0A;
    private const long DynamicTagPs5NeededModule = 0x61000045;
    private const long DynamicTagPs5ImportLibrary = 0x61000049;
    private const int ElfRelocationSize = 24;
    private const uint Absolute64RelocationType = 1;
    private const uint RelativeRelocationType = 8;
    private const uint TlsModuleIdRelocationType = 16;
    private const uint TlsDtpOffsetRelocationType = 17;
    private const uint TlsTpOffsetRelocationType = 18;
    private const int SyntheticRelocationTargetVirtualAddress = 0x80;
    private const ulong SyntheticImportStubBaseAddress = 0x0000_7000_0000_0000UL;
    private const ulong SyntheticImportStubAddressStride = 0x0100_0000UL;
    private const ulong SyntheticImportStubSlotSize = 0x10UL;

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

    [Fact]
    public void LoadsGnuEhFrameMetadataForGuestUnwinding()
    {
        const int loadSize = 0x200;
        const int ehFrameOffset = 0x80;
        const int ehFrameHeaderOffset = 0xC0;
        var payloadOffset = ElfHeaderSize + (2 * ProgramHeaderSize);
        var elf = CreateElf(
            programHeaderOffset: ElfHeaderSize,
            programHeaderCount: 2);
        Array.Resize(ref elf, payloadOffset + loadSize);

        var loadHeader = elf.AsSpan(ElfHeaderSize, ProgramHeaderSize);
        BinaryPrimitives.WriteUInt32LittleEndian(loadHeader, (uint)ProgramHeaderType.Load);
        BinaryPrimitives.WriteUInt32LittleEndian(loadHeader[4..], (uint)ProgramHeaderFlags.Read);
        BinaryPrimitives.WriteUInt64LittleEndian(loadHeader[8..], (ulong)payloadOffset);
        BinaryPrimitives.WriteUInt64LittleEndian(loadHeader[32..], loadSize);
        BinaryPrimitives.WriteUInt64LittleEndian(loadHeader[40..], loadSize);
        BinaryPrimitives.WriteUInt64LittleEndian(loadHeader[48..], 8);

        var unwindHeader = elf.AsSpan(ElfHeaderSize + ProgramHeaderSize, ProgramHeaderSize);
        BinaryPrimitives.WriteUInt32LittleEndian(unwindHeader, (uint)ProgramHeaderType.GnuEhFrame);
        BinaryPrimitives.WriteUInt32LittleEndian(unwindHeader[4..], (uint)ProgramHeaderFlags.Read);
        BinaryPrimitives.WriteUInt64LittleEndian(unwindHeader[8..], (ulong)(payloadOffset + ehFrameHeaderOffset));
        BinaryPrimitives.WriteUInt64LittleEndian(unwindHeader[16..], ehFrameHeaderOffset);
        BinaryPrimitives.WriteUInt64LittleEndian(unwindHeader[32..], 12);
        BinaryPrimitives.WriteUInt64LittleEndian(unwindHeader[40..], 12);
        BinaryPrimitives.WriteUInt64LittleEndian(unwindHeader[48..], 4);

        var encoded = elf.AsSpan(payloadOffset + ehFrameHeaderOffset, 12);
        encoded[0] = 1;
        encoded[1] = 0x1B; // DW_EH_PE_pcrel | DW_EH_PE_sdata4
        encoded[2] = 0x03; // DW_EH_PE_udata4
        encoded[3] = 0x3B; // DW_EH_PE_datarel | DW_EH_PE_sdata4
        BinaryPrimitives.WriteInt32LittleEndian(
            encoded[4..],
            ehFrameOffset - (ehFrameHeaderOffset + 4));

        var image = new SelfLoader().Load(elf, new VirtualMemory());

        Assert.Equal(image.ImageBase + ehFrameHeaderOffset, image.EhFrameHeaderAddress);
        Assert.Equal(image.ImageBase + ehFrameOffset, image.EhFrameAddress);
        Assert.Equal((ulong)(ehFrameHeaderOffset - ehFrameOffset), image.EhFrameSize);
    }

    [Fact]
    public void IgnoresMalformedGnuEhFrameMetadata()
    {
        const int loadSize = 0x100;
        const int ehFrameHeaderOffset = 0x80;
        var payloadOffset = ElfHeaderSize + (2 * ProgramHeaderSize);
        var elf = CreateElf(
            programHeaderOffset: ElfHeaderSize,
            programHeaderCount: 2);
        Array.Resize(ref elf, payloadOffset + loadSize);

        var loadHeader = elf.AsSpan(ElfHeaderSize, ProgramHeaderSize);
        BinaryPrimitives.WriteUInt32LittleEndian(loadHeader, (uint)ProgramHeaderType.Load);
        BinaryPrimitives.WriteUInt32LittleEndian(loadHeader[4..], (uint)ProgramHeaderFlags.Read);
        BinaryPrimitives.WriteUInt64LittleEndian(loadHeader[8..], (ulong)payloadOffset);
        BinaryPrimitives.WriteUInt64LittleEndian(loadHeader[32..], loadSize);
        BinaryPrimitives.WriteUInt64LittleEndian(loadHeader[40..], loadSize);
        BinaryPrimitives.WriteUInt64LittleEndian(loadHeader[48..], 8);

        var unwindHeader = elf.AsSpan(ElfHeaderSize + ProgramHeaderSize, ProgramHeaderSize);
        BinaryPrimitives.WriteUInt32LittleEndian(unwindHeader, (uint)ProgramHeaderType.GnuEhFrame);
        BinaryPrimitives.WriteUInt32LittleEndian(unwindHeader[4..], (uint)ProgramHeaderFlags.Read);
        BinaryPrimitives.WriteUInt64LittleEndian(unwindHeader[8..], (ulong)(payloadOffset + ehFrameHeaderOffset));
        BinaryPrimitives.WriteUInt64LittleEndian(unwindHeader[16..], ehFrameHeaderOffset);
        BinaryPrimitives.WriteUInt64LittleEndian(unwindHeader[32..], 8);
        BinaryPrimitives.WriteUInt64LittleEndian(unwindHeader[40..], 8);
        BinaryPrimitives.WriteUInt64LittleEndian(unwindHeader[48..], 4);

        var encoded = elf.AsSpan(payloadOffset + ehFrameHeaderOffset, 8);
        encoded[0] = 2; // Unsupported .eh_frame_hdr version.
        encoded[1] = 0x1B;

        var image = new SelfLoader().Load(elf, new VirtualMemory());

        Assert.Equal(0UL, image.EhFrameHeaderAddress);
        Assert.Equal(0UL, image.EhFrameAddress);
        Assert.Equal(0UL, image.EhFrameSize);
    }

    [Fact]
    public void ModuleManagerOverloadsRejectNullBeforeChangingGuestMemory()
    {
        var memory = new VirtualMemory();
        memory.Map(
            virtualAddress: 0x1000,
            memorySize: 4,
            fileOffset: 0,
            fileData: [1, 2, 3, 4],
            ProgramHeaderFlags.Read | ProgramHeaderFlags.Write);
        var loader = new SelfLoader();
        var elf = CreateElf();

        Assert.Throws<ArgumentNullException>(() =>
            loader.Load(elf, memory, (IModuleManager)null!));
        Assert.Throws<ArgumentNullException>(() =>
            loader.LoadAdditional(
                elf,
                memory,
                null!,
                fs: null,
                mountRoot: null));

        var region = Assert.Single(memory.SnapshotRegions());
        Assert.Equal(0x1000UL, region.VirtualAddress);
        Span<byte> contents = stackalloc byte[4];
        Assert.True(memory.TryRead(0x1000, contents));
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, contents.ToArray());
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

    [Theory]
    [InlineData(6, 1, 0UL, "identification version")]
    [InlineData(18, 2, 0xB7UL, "machine")]
    [InlineData(20, 4, 2UL, "version")]
    [InlineData(52, 2, ElfHeaderSize - 1UL, "header size")]
    public void RejectedUnsupportedElfIdentityDoesNotClearExistingGuestMemory(
        int fieldOffset,
        int fieldSize,
        ulong value,
        string expectedMessage)
    {
        var memory = CreateSentinelMemory();
        var malformed = CreateElf();
        var field = malformed.AsSpan(fieldOffset, fieldSize);
        switch (fieldSize)
        {
            case 1:
                field[0] = checked((byte)value);
                break;
            case 2:
                BinaryPrimitives.WriteUInt16LittleEndian(field, checked((ushort)value));
                break;
            case 4:
                BinaryPrimitives.WriteUInt32LittleEndian(field, checked((uint)value));
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(fieldSize));
        }

        var exception = Assert.Throws<InvalidDataException>(() =>
            new SelfLoader().Load(malformed, memory));

        Assert.Contains(expectedMessage, exception.Message, StringComparison.OrdinalIgnoreCase);
        AssertSentinelMemoryPreserved(memory);
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
    public void RejectedOverflowingSectionHeaderTableDoesNotClearExistingGuestMemory()
    {
        var memory = CreateSentinelMemory();
        var malformed = CreateElf();
        BinaryPrimitives.WriteUInt64LittleEndian(
            malformed.AsSpan(40),
            ulong.MaxValue - 63);
        BinaryPrimitives.WriteUInt16LittleEndian(malformed.AsSpan(58), 64);
        BinaryPrimitives.WriteUInt16LittleEndian(malformed.AsSpan(60), 2);

        var exception = Assert.Throws<InvalidDataException>(() =>
            new SelfLoader().Load(malformed, memory));

        Assert.Contains("section header", exception.Message, StringComparison.OrdinalIgnoreCase);
        AssertSentinelMemoryPreserved(memory);
    }

    [Theory]
    [InlineData(0UL, 64)]
    [InlineData((ulong)ElfHeaderSize, 63)]
    [InlineData((ulong)ElfHeaderSize, 64)]
    public void RejectedInvalidSectionHeaderTablesDoNotClearExistingGuestMemory(
        ulong tableOffset,
        int entrySize)
    {
        var memory = CreateSentinelMemory();
        var malformed = CreateElf();
        BinaryPrimitives.WriteUInt64LittleEndian(malformed.AsSpan(40), tableOffset);
        BinaryPrimitives.WriteUInt16LittleEndian(
            malformed.AsSpan(58),
            checked((ushort)entrySize));
        BinaryPrimitives.WriteUInt16LittleEndian(malformed.AsSpan(60), 1);

        var exception = Assert.Throws<InvalidDataException>(() =>
            new SelfLoader().Load(malformed, memory));

        Assert.Contains("section header", exception.Message, StringComparison.OrdinalIgnoreCase);
        AssertSentinelMemoryPreserved(memory);
    }

    [Fact]
    public void LoadsConsoleExecutableWhenOptionalSectionHeadersAreNotInTheRuntimeImage()
    {
        var payload = new byte[] { 0x48, 0x31, 0xC0, 0xC3 };
        var elf = CreateElfWithLoadSegment(
            fileOffset: ElfHeaderSize + ProgramHeaderSize,
            virtualAddress: 0,
            fileSize: (ulong)payload.Length,
            memorySize: (ulong)payload.Length,
            payload: payload);
        elf[7] = 9;
        BinaryPrimitives.WriteUInt16LittleEndian(elf.AsSpan(16), 0xFE10);
        BinaryPrimitives.WriteUInt64LittleEndian(elf.AsSpan(40), 0x4000_0000);
        BinaryPrimitives.WriteUInt16LittleEndian(elf.AsSpan(58), 64);
        BinaryPrimitives.WriteUInt16LittleEndian(elf.AsSpan(60), 3);

        var memory = new VirtualMemory();
        var image = new SelfLoader().Load(elf, memory);

        Assert.Equal(0x0000_0008_0000_0000UL, image.EntryPoint);
        Span<byte> loadedPayload = stackalloc byte[payload.Length];
        Assert.True(memory.TryRead(0x0000_0008_0000_0000UL, loadedPayload));
        Assert.Equal(payload, loadedPayload.ToArray());
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
    public void RejectedOverflowingEntryPointDoesNotClearExistingGuestMemory()
    {
        var memory = CreateSentinelMemory();
        var malformed = CreateElf();
        BinaryPrimitives.WriteUInt64LittleEndian(malformed.AsSpan(24), ulong.MaxValue);

        var exception = Assert.Throws<InvalidDataException>(() =>
            new SelfLoader().Load(malformed, memory));

        Assert.Contains("entry point", exception.Message, StringComparison.OrdinalIgnoreCase);
        AssertSentinelMemoryPreserved(memory);
    }

    [Fact]
    public void RejectedOverflowingProcParamAddressDoesNotClearExistingGuestMemory()
    {
        var memory = CreateSentinelMemory();
        var malformed = CreateElf(
            programHeaderOffset: ElfHeaderSize,
            programHeaderCount: 1);
        Array.Resize(ref malformed, ElfHeaderSize + ProgramHeaderSize);
        var procParamHeader = malformed.AsSpan(ElfHeaderSize, ProgramHeaderSize);
        BinaryPrimitives.WriteUInt32LittleEndian(
            procParamHeader,
            (uint)ProgramHeaderType.SceProcParam);
        BinaryPrimitives.WriteUInt64LittleEndian(procParamHeader[16..], ulong.MaxValue);

        var exception = Assert.Throws<InvalidDataException>(() =>
            new SelfLoader().Load(malformed, memory));

        Assert.Contains("proc param", exception.Message, StringComparison.OrdinalIgnoreCase);
        AssertSentinelMemoryPreserved(memory);
    }

    [Fact]
    public void RejectedOversizedDynamicMetadataDoesNotClearExistingGuestMemory()
    {
        var memory = new VirtualMemory();
        memory.Map(
            virtualAddress: 0x1000,
            memorySize: 4,
            fileOffset: 0,
            fileData: new byte[] { 1, 2, 3, 4 },
            protection: ProgramHeaderFlags.Read | ProgramHeaderFlags.Write);
        var malformed = CreateElf(
            programHeaderOffset: ElfHeaderSize,
            programHeaderCount: 1);
        Array.Resize(ref malformed, ElfHeaderSize + ProgramHeaderSize);
        var dynamicHeader = malformed.AsSpan(ElfHeaderSize, ProgramHeaderSize);
        BinaryPrimitives.WriteUInt32LittleEndian(
            dynamicHeader,
            (uint)ProgramHeaderType.Dynamic);
        BinaryPrimitives.WriteUInt64LittleEndian(
            dynamicHeader[32..],
            (ulong)int.MaxValue + 1);

        Assert.Throws<NotSupportedException>(() =>
            new SelfLoader().Load(malformed, memory));

        var region = Assert.Single(memory.SnapshotRegions());
        Assert.Equal(0x1000UL, region.VirtualAddress);
        Span<byte> contents = stackalloc byte[4];
        Assert.True(memory.TryRead(0x1000, contents));
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, contents.ToArray());
    }

    [Fact]
    public void RejectedOversizedLoadSegmentDoesNotClearExistingGuestMemory()
    {
        var memory = CreateSentinelMemory();
        var malformed = CreateElfWithLoadSegment(
            fileOffset: ElfHeaderSize + ProgramHeaderSize,
            virtualAddress: 0,
            fileSize: 0,
            memorySize: (ulong)int.MaxValue + 1,
            payload: []);

        var exception = Assert.Throws<NotSupportedException>(() =>
            new SelfLoader().Load(malformed, memory));

        Assert.Contains("2 GB", exception.Message, StringComparison.OrdinalIgnoreCase);
        AssertSentinelMemoryPreserved(memory);
    }

    [Fact]
    public void RejectedOutOfRangeDynamicMetadataDoesNotClearExistingGuestMemory()
    {
        var memory = new VirtualMemory();
        memory.Map(
            virtualAddress: 0x1000,
            memorySize: 4,
            fileOffset: 0,
            fileData: new byte[] { 1, 2, 3, 4 },
            protection: ProgramHeaderFlags.Read | ProgramHeaderFlags.Write);
        var malformed = CreateElf(
            programHeaderOffset: ElfHeaderSize,
            programHeaderCount: 1);
        Array.Resize(ref malformed, ElfHeaderSize + ProgramHeaderSize);
        var dynamicHeader = malformed.AsSpan(ElfHeaderSize, ProgramHeaderSize);
        BinaryPrimitives.WriteUInt32LittleEndian(
            dynamicHeader,
            (uint)ProgramHeaderType.Dynamic);
        BinaryPrimitives.WriteUInt64LittleEndian(dynamicHeader[8..], ulong.MaxValue);
        BinaryPrimitives.WriteUInt64LittleEndian(dynamicHeader[16..], 0x5000);
        BinaryPrimitives.WriteUInt64LittleEndian(dynamicHeader[32..], DynamicEntrySize);

        Assert.Throws<InvalidDataException>(() =>
            new SelfLoader().Load(malformed, memory));

        var region = Assert.Single(memory.SnapshotRegions());
        Assert.Equal(0x1000UL, region.VirtualAddress);
        Span<byte> contents = stackalloc byte[4];
        Assert.True(memory.TryRead(0x1000, contents));
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, contents.ToArray());
    }

    [Fact]
    public void RejectedDynamicMetadataInLoadSegmentBssDoesNotClearExistingGuestMemory()
    {
        var memory = CreateSentinelMemory();
        var payloadOffset = ElfHeaderSize + (2 * ProgramHeaderSize);
        var malformed = CreateElf(
            programHeaderOffset: ElfHeaderSize,
            programHeaderCount: 2);
        Array.Resize(ref malformed, payloadOffset + DynamicEntrySize);

        var loadHeader = malformed.AsSpan(ElfHeaderSize, ProgramHeaderSize);
        BinaryPrimitives.WriteUInt32LittleEndian(loadHeader, (uint)ProgramHeaderType.Load);
        BinaryPrimitives.WriteUInt64LittleEndian(loadHeader[8..], (ulong)payloadOffset);
        BinaryPrimitives.WriteUInt64LittleEndian(loadHeader[16..], 0x3000);
        BinaryPrimitives.WriteUInt64LittleEndian(loadHeader[32..], DynamicEntrySize);
        BinaryPrimitives.WriteUInt64LittleEndian(loadHeader[40..], 0x40);
        BinaryPrimitives.WriteUInt64LittleEndian(loadHeader[48..], 8);

        var dynamicHeader = malformed.AsSpan(
            ElfHeaderSize + ProgramHeaderSize,
            ProgramHeaderSize);
        BinaryPrimitives.WriteUInt32LittleEndian(
            dynamicHeader,
            (uint)ProgramHeaderType.Dynamic);
        BinaryPrimitives.WriteUInt64LittleEndian(dynamicHeader[8..], ulong.MaxValue);
        BinaryPrimitives.WriteUInt64LittleEndian(dynamicHeader[16..], 0x3020);
        BinaryPrimitives.WriteUInt64LittleEndian(dynamicHeader[32..], DynamicEntrySize);
        BinaryPrimitives.WriteUInt64LittleEndian(dynamicHeader[40..], DynamicEntrySize);

        Assert.Throws<InvalidDataException>(() =>
            new SelfLoader().Load(malformed, memory));

        AssertSentinelMemoryPreserved(memory);
    }

    [Fact]
    public void RejectedOverlappingLoadSegmentsDoNotClearExistingGuestMemory()
    {
        var memory = new VirtualMemory();
        memory.Map(
            virtualAddress: 0x1000,
            memorySize: 4,
            fileOffset: 0,
            fileData: new byte[] { 1, 2, 3, 4 },
            protection: ProgramHeaderFlags.Read | ProgramHeaderFlags.Write);
        var malformed = CreateElf(
            programHeaderOffset: ElfHeaderSize,
            programHeaderCount: 2);
        Array.Resize(ref malformed, ElfHeaderSize + (2 * ProgramHeaderSize));
        WriteLoadHeader(malformed.AsSpan(ElfHeaderSize, ProgramHeaderSize), 0, 0x20);
        WriteLoadHeader(
            malformed.AsSpan(ElfHeaderSize + ProgramHeaderSize, ProgramHeaderSize),
            0x10,
            0x20);

        Assert.Throws<InvalidDataException>(() =>
            new SelfLoader().Load(malformed, memory));

        var region = Assert.Single(memory.SnapshotRegions());
        Assert.Equal(0x1000UL, region.VirtualAddress);
        Span<byte> contents = stackalloc byte[4];
        Assert.True(memory.TryRead(0x1000, contents));
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, contents.ToArray());
    }

    [Fact]
    public void MapsAdjacentLoadSegmentsWithoutTreatingBoundaryAsOverlap()
    {
        var elf = CreateElf(
            programHeaderOffset: ElfHeaderSize,
            programHeaderCount: 2);
        Array.Resize(ref elf, ElfHeaderSize + (2 * ProgramHeaderSize));
        WriteLoadHeader(elf.AsSpan(ElfHeaderSize, ProgramHeaderSize), 0, 0x20);
        WriteLoadHeader(
            elf.AsSpan(ElfHeaderSize + ProgramHeaderSize, ProgramHeaderSize),
            0x20,
            0x20);

        var image = new SelfLoader().Load(elf, new VirtualMemory());

        Assert.Collection(
            image.MappedRegions.OrderBy(region => region.VirtualAddress),
            first =>
            {
                Assert.Equal(0x0000_0008_0000_0000UL, first.VirtualAddress);
                Assert.Equal(0x20UL, first.MemorySize);
            },
            second =>
            {
                Assert.Equal(0x0000_0008_0000_0020UL, second.VirtualAddress);
                Assert.Equal(0x20UL, second.MemorySize);
            });
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

    [Fact]
    public void AdditionalImageReportsOnlyRegionsMappedForThatImage()
    {
        var mainElf = CreateElfWithLoadSegment(
            fileOffset: ElfHeaderSize + ProgramHeaderSize,
            virtualAddress: 0x2000,
            fileSize: 1,
            memorySize: 1,
            payload: [0xC3]);
        var moduleElf = CreateElfWithLoadSegment(
            fileOffset: ElfHeaderSize + ProgramHeaderSize,
            virtualAddress: 0x3000,
            fileSize: 1,
            memorySize: 1,
            payload: [0xC3]);
        var memory = new VirtualMemory();
        var loader = new SelfLoader();
        var moduleManager = new ModuleManager();

        var mainImage = loader.Load(
            mainElf,
            memory,
            moduleManager);
        var moduleImage = loader.LoadAdditional(
            moduleElf,
            memory,
            moduleManager,
            fs: null,
            mountRoot: null);

        var mainRegion = Assert.Single(mainImage.MappedRegions);
        var moduleRegion = Assert.Single(moduleImage.MappedRegions);
        Assert.NotEqual(mainRegion.VirtualAddress, moduleRegion.VirtualAddress);
        Assert.Equal(
            moduleImage.ImageBase + 0x3000,
            moduleRegion.VirtualAddress);
    }

    [Fact]
    public void LoadsDynamicMetadataFromProgramHeaderFileOffset()
    {
        var dynamicOffset = ElfHeaderSize + ProgramHeaderSize;
        var elf = CreateElf(
            programHeaderOffset: ElfHeaderSize,
            programHeaderCount: 1);
        Array.Resize(ref elf, dynamicOffset + (2 * DynamicEntrySize));

        var header = elf.AsSpan(ElfHeaderSize, ProgramHeaderSize);
        BinaryPrimitives.WriteUInt32LittleEndian(header, (uint)ProgramHeaderType.Dynamic);
        BinaryPrimitives.WriteUInt32LittleEndian(header[4..], (uint)ProgramHeaderFlags.Read);
        BinaryPrimitives.WriteUInt64LittleEndian(header[8..], (ulong)dynamicOffset);
        // Deliberately points inside the ELF header table. The loader must not
        // reinterpret this virtual address as a raw file offset.
        BinaryPrimitives.WriteUInt64LittleEndian(header[16..], ElfHeaderSize);
        BinaryPrimitives.WriteUInt64LittleEndian(header[32..], 2 * DynamicEntrySize);
        BinaryPrimitives.WriteUInt64LittleEndian(header[40..], 2 * DynamicEntrySize);
        BinaryPrimitives.WriteUInt64LittleEndian(header[48..], 8);

        var dynamicTable = elf.AsSpan(dynamicOffset, 2 * DynamicEntrySize);
        BinaryPrimitives.WriteInt64LittleEndian(dynamicTable, DynamicTagInit);
        BinaryPrimitives.WriteUInt64LittleEndian(dynamicTable[sizeof(long)..], 0x20_000);

        var image = new SelfLoader().Load(elf, new VirtualMemory());

        Assert.Equal(0x20_000UL, image.InitFunctionEntryPoint);
        Assert.Equal(new[] { 0x20_000UL }, image.InitializerFunctions);
    }

    [Fact]
    public void LoadsDynamicMetadataFromMappedSegmentBeforePhysicalFallback()
    {
        var dynamicOffset = ElfHeaderSize + (2 * ProgramHeaderSize);
        var elf = CreateElf(
            programHeaderOffset: ElfHeaderSize,
            programHeaderCount: 2);
        Array.Resize(ref elf, dynamicOffset + (2 * DynamicEntrySize));

        var loadHeader = elf.AsSpan(ElfHeaderSize, ProgramHeaderSize);
        BinaryPrimitives.WriteUInt32LittleEndian(loadHeader, (uint)ProgramHeaderType.Load);
        BinaryPrimitives.WriteUInt32LittleEndian(loadHeader[4..], (uint)ProgramHeaderFlags.Read);
        BinaryPrimitives.WriteUInt64LittleEndian(loadHeader[8..], (ulong)dynamicOffset);
        BinaryPrimitives.WriteUInt64LittleEndian(loadHeader[16..], 0x3000);
        BinaryPrimitives.WriteUInt64LittleEndian(loadHeader[32..], 2 * DynamicEntrySize);
        BinaryPrimitives.WriteUInt64LittleEndian(loadHeader[40..], 2 * DynamicEntrySize);
        BinaryPrimitives.WriteUInt64LittleEndian(loadHeader[48..], 8);

        var dynamicHeader = elf.AsSpan(
            ElfHeaderSize + ProgramHeaderSize,
            ProgramHeaderSize);
        BinaryPrimitives.WriteUInt32LittleEndian(dynamicHeader, (uint)ProgramHeaderType.Dynamic);
        BinaryPrimitives.WriteUInt32LittleEndian(dynamicHeader[4..], (uint)ProgramHeaderFlags.Read);
        BinaryPrimitives.WriteUInt64LittleEndian(dynamicHeader[8..], ulong.MaxValue);
        BinaryPrimitives.WriteUInt64LittleEndian(dynamicHeader[16..], 0x3000);
        BinaryPrimitives.WriteUInt64LittleEndian(dynamicHeader[32..], 2 * DynamicEntrySize);
        BinaryPrimitives.WriteUInt64LittleEndian(dynamicHeader[40..], 2 * DynamicEntrySize);
        BinaryPrimitives.WriteUInt64LittleEndian(dynamicHeader[48..], 8);

        var dynamicTable = elf.AsSpan(dynamicOffset, 2 * DynamicEntrySize);
        BinaryPrimitives.WriteInt64LittleEndian(dynamicTable, DynamicTagInit);
        BinaryPrimitives.WriteUInt64LittleEndian(dynamicTable[sizeof(long)..], 0x20_000);

        var image = new SelfLoader().Load(elf, new VirtualMemory());

        Assert.Equal(0x20_000UL, image.InitFunctionEntryPoint);
        Assert.Equal(new[] { 0x20_000UL }, image.InitializerFunctions);
    }

    [HostX64Fact]
    public void PhysicalMemoryLoadsDynamicTablesFromNonReadableSegmentBacking()
    {
        var dynamicOffset = ElfHeaderSize + (2 * ProgramHeaderSize);
        const int dynamicTableSize = 4 * DynamicEntrySize;
        const int initializerArraySize = sizeof(ulong);
        var elf = CreateElf(
            programHeaderOffset: ElfHeaderSize,
            programHeaderCount: 2);
        Array.Resize(ref elf, dynamicOffset + dynamicTableSize + initializerArraySize);

        var loadHeader = elf.AsSpan(ElfHeaderSize, ProgramHeaderSize);
        BinaryPrimitives.WriteUInt32LittleEndian(loadHeader, (uint)ProgramHeaderType.Load);
        BinaryPrimitives.WriteUInt32LittleEndian(loadHeader[4..], 0);
        BinaryPrimitives.WriteUInt64LittleEndian(loadHeader[8..], (ulong)dynamicOffset);
        BinaryPrimitives.WriteUInt64LittleEndian(loadHeader[16..], 0x3000);
        BinaryPrimitives.WriteUInt64LittleEndian(
            loadHeader[32..],
            dynamicTableSize + initializerArraySize);
        BinaryPrimitives.WriteUInt64LittleEndian(
            loadHeader[40..],
            dynamicTableSize + initializerArraySize);
        BinaryPrimitives.WriteUInt64LittleEndian(loadHeader[48..], 8);

        var dynamicHeader = elf.AsSpan(
            ElfHeaderSize + ProgramHeaderSize,
            ProgramHeaderSize);
        BinaryPrimitives.WriteUInt32LittleEndian(dynamicHeader, (uint)ProgramHeaderType.Dynamic);
        BinaryPrimitives.WriteUInt32LittleEndian(dynamicHeader[4..], (uint)ProgramHeaderFlags.Read);
        BinaryPrimitives.WriteUInt64LittleEndian(dynamicHeader[8..], ulong.MaxValue);
        BinaryPrimitives.WriteUInt64LittleEndian(dynamicHeader[16..], 0x3000);
        BinaryPrimitives.WriteUInt64LittleEndian(dynamicHeader[32..], dynamicTableSize);
        BinaryPrimitives.WriteUInt64LittleEndian(dynamicHeader[40..], dynamicTableSize);
        BinaryPrimitives.WriteUInt64LittleEndian(dynamicHeader[48..], 8);

        var dynamicTable = elf.AsSpan(dynamicOffset, dynamicTableSize);
        BinaryPrimitives.WriteInt64LittleEndian(dynamicTable, DynamicTagInitArray);
        BinaryPrimitives.WriteUInt64LittleEndian(dynamicTable[sizeof(long)..], 0x3040);
        BinaryPrimitives.WriteInt64LittleEndian(
            dynamicTable[DynamicEntrySize..],
            DynamicTagInitArraySize);
        BinaryPrimitives.WriteUInt64LittleEndian(
            dynamicTable[(DynamicEntrySize + sizeof(long))..],
            initializerArraySize);
        BinaryPrimitives.WriteUInt64LittleEndian(
            elf.AsSpan(dynamicOffset + dynamicTableSize, initializerArraySize),
            0x20_000);

        using var memory = new PhysicalVirtualMemory();
        var image = new SelfLoader().Load(elf, memory);

        Assert.Equal(0UL, image.InitFunctionEntryPoint);
        Assert.Equal(new[] { 0x20_000UL }, image.InitializerFunctions);
    }

    [Fact]
    public void UnavailableDynamicTableDoesNotRequestMetadataSizedGuestBuffer()
    {
        const int unavailableTableSize = 1024 * 1024;
        var dynamicOffset = ElfHeaderSize + ProgramHeaderSize;
        var elf = CreateElf(
            programHeaderOffset: ElfHeaderSize,
            programHeaderCount: 1);
        Array.Resize(ref elf, dynamicOffset + (3 * DynamicEntrySize));

        var header = elf.AsSpan(ElfHeaderSize, ProgramHeaderSize);
        BinaryPrimitives.WriteUInt32LittleEndian(header, (uint)ProgramHeaderType.Dynamic);
        BinaryPrimitives.WriteUInt32LittleEndian(header[4..], (uint)ProgramHeaderFlags.Read);
        BinaryPrimitives.WriteUInt64LittleEndian(header[8..], (ulong)dynamicOffset);
        BinaryPrimitives.WriteUInt64LittleEndian(header[16..], 0x5000);
        BinaryPrimitives.WriteUInt64LittleEndian(header[32..], 3 * DynamicEntrySize);
        BinaryPrimitives.WriteUInt64LittleEndian(header[40..], 3 * DynamicEntrySize);

        var dynamicTable = elf.AsSpan(dynamicOffset, 3 * DynamicEntrySize);
        BinaryPrimitives.WriteInt64LittleEndian(dynamicTable, DynamicTagRela);
        BinaryPrimitives.WriteUInt64LittleEndian(dynamicTable[sizeof(long)..], 0x10_0000);
        BinaryPrimitives.WriteInt64LittleEndian(
            dynamicTable[DynamicEntrySize..],
            DynamicTagRelaSize);
        BinaryPrimitives.WriteUInt64LittleEndian(
            dynamicTable[(DynamicEntrySize + sizeof(long))..],
            unavailableTableSize);
        var memory = new RecordingVirtualMemory();

        _ = new SelfLoader().Load(elf, memory);

        Assert.True(
            memory.LargestReadRequest < unavailableTableSize,
            $"Loader requested an unavailable {memory.LargestReadRequest}-byte guest buffer.");
    }

    [Theory]
    [InlineData(0x1234L)]
    [InlineData(-0x20L)]
    public void AppliesDynamicRelativeRelocation(long addend)
    {
        var elf = CreateElfWithDynamicRelocation(RelativeRelocationType, addend);
        var memory = new VirtualMemory();

        var image = new SelfLoader().Load(elf, memory);

        Assert.Equal(
            AddSignedForTest(image.EntryPoint, addend),
            ReadSyntheticRelocationTarget(memory, image));
        Assert.Empty(image.UnsupportedRelocationTypes);
    }

    [Fact]
    public void ReportsUnsupportedDynamicRelocationWithoutPatchingTarget()
    {
        const uint unsupportedRelocationType = 42;
        var elf = CreateElfWithDynamicRelocation(unsupportedRelocationType, addend: 0x1234);
        var memory = new VirtualMemory();

        var image = new SelfLoader().Load(elf, memory);

        Assert.Equal(new uint[] { unsupportedRelocationType }, image.UnsupportedRelocationTypes);
        Assert.Equal(0UL, ReadSyntheticRelocationTarget(memory, image));
    }

    [Fact]
    public void MutatedDynamicMetadataNeverLeaksArithmeticExceptions()
    {
        var baseline = CreateElfWithSymbolRelocation(
            originalTargetValue: 0,
            symbolBinding: 1,
            symbolType: 1,
            symbolValue: 0,
            symbolName: "TESTNID#libSceSynthetic",
            addend: 0);
        var payloadOffset = ElfHeaderSize + (2 * ProgramHeaderSize);
        var random = new Random(0xD1A6);

        for (var iteration = 0; iteration < 256; iteration++)
        {
            var image = (byte[])baseline.Clone();
            var mutationCount = random.Next(1, 9);
            for (var mutation = 0; mutation < mutationCount; mutation++)
            {
                var offset = random.Next(payloadOffset + 0x20, image.Length);
                image[offset] = (byte)random.Next(0, 256);
            }

            var exception = Record.Exception(() =>
                new SelfLoader().Load(image, new VirtualMemory()));

            Assert.False(
                exception is OverflowException or ArgumentOutOfRangeException or IndexOutOfRangeException,
                $"Iteration {iteration} leaked {exception?.GetType().Name}: {exception?.Message}");
        }
    }

    [Fact]
    public void AppliesDistinctTlsModuleIdsAcrossAdditionalImages()
    {
        var elf = CreateElfWithDynamicRelocation(
            TlsModuleIdRelocationType,
            addend: 0,
            includeTlsSegment: true);
        var memory = new VirtualMemory();
        var moduleManager = new ModuleManager();
        var loader = new SelfLoader();

        var mainImage = loader.Load(elf, memory, moduleManager);
        var additionalImage = loader.LoadAdditional(
            elf,
            memory,
            moduleManager,
            fs: null,
            mountRoot: null);

        Assert.Equal(1UL, ReadSyntheticRelocationTarget(memory, mainImage));
        Assert.Equal(2UL, ReadSyntheticRelocationTarget(memory, additionalImage));
        Assert.Equal(new byte[] { 0xA5, 0x5A }, GuestTlsTemplate.InitImage);
    }

    [Fact]
    public void ResetsTlsModuleIdsWhenLoaderStartsNewPrimaryImage()
    {
        var elf = CreateElfWithDynamicRelocation(
            TlsModuleIdRelocationType,
            addend: 0,
            includeTlsSegment: true);
        var memory = new VirtualMemory();
        var moduleManager = new ModuleManager();
        var loader = new SelfLoader();
        _ = loader.Load(elf, memory, moduleManager);
        _ = loader.LoadAdditional(
            elf,
            memory,
            moduleManager,
            fs: null,
            mountRoot: null);

        var replacementMain = loader.Load(elf, memory, moduleManager);
        var replacementAdditional = loader.LoadAdditional(
            elf,
            memory,
            moduleManager,
            fs: null,
            mountRoot: null);

        Assert.Equal(1UL, ReadSyntheticRelocationTarget(memory, replacementMain));
        Assert.Equal(2UL, ReadSyntheticRelocationTarget(memory, replacementAdditional));
    }

    [Fact]
    public void RejectsTlsModuleRelocationWithoutTlsSegment()
    {
        var elf = CreateElfWithDynamicRelocation(TlsModuleIdRelocationType, addend: 0);

        var exception = Assert.Throws<InvalidDataException>(
            () => new SelfLoader().Load(elf, new VirtualMemory()));

        Assert.Contains("without PT_TLS", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(TlsDtpOffsetRelocationType)]
    [InlineData(TlsTpOffsetRelocationType)]
    public void AppliesTlsOffsetRelocationsAgainstRegisteredTemplate(uint relocationType)
    {
        const ulong symbolOffset = 3;
        const long addend = 2;
        var elf = CreateElfWithSymbolRelocation(
            originalTargetValue: 0,
            symbolBinding: 0,
            symbolType: 0,
            symbolValue: symbolOffset,
            symbolName: string.Empty,
            addend: addend,
            relocationType: relocationType,
            includeTlsSegment: true);
        var memory = new VirtualMemory();

        var image = new SelfLoader().Load(elf, memory);

        Assert.True(GuestTlsTemplate.TryGetStaticOffset(1, out var staticOffset));
        var moduleRelativeOffset = symbolOffset + (ulong)addend;
        var expected = relocationType == TlsTpOffsetRelocationType
            ? unchecked(moduleRelativeOffset - staticOffset)
            : moduleRelativeOffset;
        Assert.Equal(expected, ReadSyntheticRelocationTarget(memory, image));
    }

    [Fact]
    public void RegistersTlsTemplateFromFileBackingWhenLoadSegmentIsNotReadable()
    {
        var elf = CreateElfWithDynamicRelocation(
            TlsModuleIdRelocationType,
            addend: 0,
            includeTlsSegment: true,
            loadProtection: ProgramHeaderFlags.Write);

        _ = new SelfLoader().Load(elf, new VirtualMemory());

        Assert.Equal(new byte[] { 0xA5, 0x5A }, GuestTlsTemplate.InitImage);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void LeavesRelocationTargetUnchangedForUnmappedDefinedSymbol(
        byte symbolBinding)
    {
        const ulong originalTargetValue = 0xA5A5_5A5A_DEAD_BEEFUL;
        var elf = CreateElfWithSymbolRelocation(
            originalTargetValue,
            symbolBinding,
            symbolType: 0,
            symbolValue: 0x400,
            symbolName: string.Empty,
            addend: 0);
        var memory = new VirtualMemory();

        var image = new SelfLoader().Load(elf, memory);

        Assert.Equal(originalTargetValue, ReadSyntheticRelocationTarget(memory, image));
    }

    [Theory]
    [InlineData(1, true)]
    [InlineData(2, false)]
    public void ResolvesUndefinedGlobalImportRelocation(
        byte symbolType,
        bool expectedDataImport)
    {
        const string nid = "TESTNID";
        const long addend = 0x20;
        var elf = CreateElfWithSymbolRelocation(
            originalTargetValue: 0,
            symbolBinding: 1,
            symbolType,
            symbolValue: 0,
            symbolName: $"{nid}#libSceSynthetic",
            addend);
        var memory = new VirtualMemory();

        var image = new SelfLoader().Load(elf, memory);

        var importStub = Assert.Single(image.ImportStubs);
        Assert.Equal(nid, importStub.Value);
        Assert.Equal(
            AddSignedForTest(importStub.Key, addend),
            ReadSyntheticRelocationTarget(memory, image));
        var importedRelocation = Assert.Single(image.ImportedRelocations);
        Assert.Equal(
            image.EntryPoint + SyntheticRelocationTargetVirtualAddress,
            importedRelocation.TargetAddress);
        Assert.Equal(addend, importedRelocation.Addend);
        Assert.Equal(nid, importedRelocation.Nid);
        Assert.Equal(expectedDataImport, importedRelocation.IsData);
        Assert.Equal("libSceSynthetic", importedRelocation.LibraryName);
        Assert.Null(importedRelocation.ModuleName);
        Assert.Equal($"{nid}#libSceSynthetic", importedRelocation.SymbolName);
    }

    [Fact]
    public void ResolvesPs5EncodedImportLibraryAndModuleNames()
    {
        const string nid = "kP2L8t3j-aM";
        var elf = CreateElfWithSymbolRelocation(
            originalTargetValue: 0,
            symbolBinding: 1,
            symbolType: 2,
            symbolValue: 0,
            symbolName: $"{nid}#4#y",
            addend: 0,
            importLibraryId: 56,
            importLibraryName: "libSceVideoOutVrrStatus",
            importModuleId: 50,
            importModuleName: "libSceVideoOut");

        var image = new SelfLoader().Load(elf, new VirtualMemory());

        var relocation = Assert.Single(image.ImportedRelocations);
        Assert.Equal(nid, relocation.Nid);
        Assert.Equal("libSceVideoOutVrrStatus", relocation.LibraryName);
        Assert.Equal("libSceVideoOut", relocation.ModuleName);
        Assert.Equal($"{nid}#4#y", relocation.SymbolName);
    }

    [Fact]
    public void EnrichesUnresolvedImportDiagnosticsFromLoaderMetadata()
    {
        const string nid = "kP2L8t3j-aM";
        var elf = CreateElfWithSymbolRelocation(
            originalTargetValue: 0,
            symbolBinding: 1,
            symbolType: 2,
            symbolValue: 0,
            symbolName: $"{nid}#4#y",
            addend: 0,
            importLibraryId: 56,
            importLibraryName: "libSceVideoOutVrrStatus",
            importModuleId: 50,
            importModuleName: "libSceVideoOut");
        var image = new SelfLoader().Load(elf, new VirtualMemory());
        var application = new PreparedApplication(
            image,
            [],
            [],
            [],
            Generation.Gen5,
            image.ImportStubs,
            image.RuntimeSymbols,
            "eboot.bin");
        var unresolved = new CpuNotImplementedInfo(
            CpuNotImplementedSource.NativeBackend,
            Assert.Single(image.ImportStubs).Key,
            nid,
            exportName: null,
            libraryName: null,
            detail: "unresolved import");

        var enriched = SharpEmuRuntime.EnrichNotImplementedInfo(
            unresolved,
            application);

        Assert.Equal("libSceVideoOutVrrStatus", enriched.LibraryName);
        Assert.Equal("libSceVideoOut", enriched.ModuleName);

        var trace = Assert.Single(SharpEmuRuntime.EnrichImportTraceEntries(
            [new CpuImportTraceEntry(
                DispatchIndex: 1,
                Nid: nid,
                LibraryName: null,
                ExportName: null,
                GuestThreadHandle: 2,
                ReturnAddress: 3,
                Arg0: 4,
                Arg1: 5,
                Arg2: 6,
                Arg3: 7,
                Arg4: 8,
                Arg5: 9,
                ReturnValue: 10)],
            application)!);
        Assert.Equal("libSceVideoOutVrrStatus", trace.LibraryName);
        Assert.Equal("libSceVideoOut", trace.ModuleName);
    }

    [Fact]
    public void MapsImportStubWithExecutableTrapReturnMetadata()
    {
        const string nid = "TESTNID";
        var elf = CreateElfWithSymbolRelocation(
            originalTargetValue: 0,
            symbolBinding: 1,
            symbolType: 2,
            symbolValue: 0,
            symbolName: $"{nid}#libSceSynthetic",
            addend: 0);
        var memory = new VirtualMemory();

        var image = new SelfLoader().Load(elf, memory);

        var importStub = Assert.Single(image.ImportStubs);
        Assert.Equal(SyntheticImportStubBaseAddress, importStub.Key);
        Span<byte> slot = stackalloc byte[(int)SyntheticImportStubSlotSize];
        Assert.True(memory.TryRead(importStub.Key, slot));
        Assert.Equal(0xCC, slot[0]);
        Assert.Equal(0xC3, slot[1]);
        Assert.Equal(0x5457_4ED3U, BinaryPrimitives.ReadUInt32LittleEndian(slot[8..]));
        var stubRegion = Assert.Single(
            memory.SnapshotRegions(),
            region => region.VirtualAddress == importStub.Key);
        Assert.Equal(0x1000UL, stubRegion.MemorySize);
        Assert.Equal(
            ProgramHeaderFlags.Read | ProgramHeaderFlags.Execute,
            stubRegion.Protection);
    }

    [Fact]
    public void MovesAdditionalImageImportStubsWhenCanonicalRegionIsOccupied()
    {
        const string nid = "TESTNID";
        var importElf = CreateElfWithSymbolRelocation(
            originalTargetValue: 0,
            symbolBinding: 1,
            symbolType: 2,
            symbolValue: 0,
            symbolName: $"{nid}#libSceSynthetic",
            addend: 0x20);
        var memory = new VirtualMemory();
        var moduleManager = new ModuleManager();
        var loader = new SelfLoader();
        _ = loader.Load(CreateElf(), memory, moduleManager);
        var occupiedPage = Enumerable.Repeat((byte)0xA5, 0x1000).ToArray();
        memory.Map(
            SyntheticImportStubBaseAddress,
            (ulong)occupiedPage.Length,
            fileOffset: 0,
            occupiedPage,
            ProgramHeaderFlags.Read | ProgramHeaderFlags.Write);

        var image = loader.LoadAdditional(
            importElf,
            memory,
            moduleManager,
            fs: null,
            mountRoot: null);

        var importStub = Assert.Single(image.ImportStubs);
        Assert.Equal(
            SyntheticImportStubBaseAddress - SyntheticImportStubAddressStride,
            importStub.Key);
        Assert.Equal(
            importStub.Key + 0x20,
            ReadSyntheticRelocationTarget(memory, image));
        Span<byte> originalPage = stackalloc byte[0x1000];
        Assert.True(memory.TryRead(SyntheticImportStubBaseAddress, originalPage));
        Assert.All(originalPage.ToArray(), value => Assert.Equal(0xA5, value));
    }

    [Fact]
    public void LoadsSyntheticSelfSegmentFromContainerMapping()
    {
        byte[] payload = [0x31, 0xC0, 0xC3];
        var self = CreateSelfWithLoadSegment(payload);
        var memory = new VirtualMemory();

        var image = new SelfLoader().Load(self, memory);

        Assert.True(image.IsSelf);
        var region = Assert.Single(image.MappedRegions);
        Assert.Equal(0x0000_0008_0000_2000UL, region.VirtualAddress);
        Assert.Equal((ulong)payload.Length, region.MemorySize);
        Span<byte> mapped = stackalloc byte[payload.Length];
        Assert.True(memory.TryRead(region.VirtualAddress, mapped));
        Assert.Equal(payload, mapped.ToArray());
    }

    [Fact]
    public void LoadsDynamicHeaderNestedInsideBlockedSelfPayload()
    {
        const ulong expectedInitializer = 0x1234_5000;
        var self = CreateSelfWithNestedDynamicSegment(expectedInitializer);

        var image = new SelfLoader().Load(self, new VirtualMemory());

        Assert.Equal(expectedInitializer, image.InitFunctionEntryPoint);
        Assert.Equal(new[] { expectedInitializer }, image.InitializerFunctions);
    }

    [Fact]
    public void RejectsDynamicHeaderOutsideBlockedSelfPayload()
    {
        var self = CreateSelfWithNestedDynamicSegment(
            initializer: 0,
            dynamicInsidePayload: false);

        var exception = Assert.Throws<NotSupportedException>(() =>
            new SelfLoader().Load(self, new VirtualMemory()));

        Assert.Contains("program header 1 could not be resolved", exception.Message);
    }

    [Fact]
    public void DoesNotFallbackAroundUnavailableContainingSelfPayload()
    {
        var self = CreateSelfWithNestedDynamicSegment(
            initializer: 0,
            payloadFitsInImage: false);

        var exception = Assert.Throws<NotSupportedException>(() =>
            new SelfLoader().Load(self, new VirtualMemory()));

        Assert.Contains("outside the available payload", exception.Message);
    }

    [Theory]
    [InlineData(0x2UL, "encrypted")]
    [InlineData(0x8UL, "compressed")]
    public void DoesNotFallbackAroundProtectedContainingSelfPayload(
        ulong segmentAttribute,
        string protectionName)
    {
        var self = CreateSelfWithNestedDynamicSegment(
            initializer: 0,
            segmentAttributes: segmentAttribute,
            payloadFitsInImage: false);

        var exception = Assert.Throws<NotSupportedException>(() =>
            new SelfLoader().Load(self, new VirtualMemory()));

        Assert.Contains(protectionName, exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("decrypted ELF/FSELF", exception.Message);
    }

    [Theory]
    [InlineData(0x2UL)]
    [InlineData(0x8UL)]
    public void LoadsAvailableDumpedPayloadForProtectedSelfSegment(ulong segmentAttributes)
    {
        byte[] payload = [0xAA, 0xBB, 0xCC, 0xDD];
        var self = CreateSelfWithLoadSegment(payload, segmentAttributes);
        var memory = new VirtualMemory();

        var image = new SelfLoader().Load(self, memory);

        Assert.True(image.IsSelf);
        var region = Assert.Single(image.MappedRegions);
        Span<byte> mapped = stackalloc byte[payload.Length];
        Assert.True(memory.TryRead(region.VirtualAddress, mapped));
        Assert.Equal(payload, mapped.ToArray());
    }

    [Theory]
    [InlineData(0x2UL, "encrypted")]
    [InlineData(0x8UL, "compressed")]
    public void RejectedProtectedSelfWithoutDumpedPayloadDoesNotClearGuestMemory(
        ulong segmentAttributes,
        string protectionName)
    {
        var memory = new VirtualMemory();
        memory.Map(
            virtualAddress: 0x1000,
            memorySize: 4,
            fileOffset: 0,
            fileData: new byte[] { 1, 2, 3, 4 },
            protection: ProgramHeaderFlags.Read | ProgramHeaderFlags.Write);
        var self = CreateSelfWithLoadSegment([0xAA], segmentAttributes);
        Array.Resize(ref self, SelfHeaderSize + SelfSegmentSize + ElfHeaderSize + ProgramHeaderSize);

        var exception = Assert.Throws<NotSupportedException>(() =>
            new SelfLoader().Load(self, memory));

        Assert.Contains(protectionName, exception.Message, StringComparison.OrdinalIgnoreCase);
        var region = Assert.Single(memory.SnapshotRegions());
        Assert.Equal(0x1000UL, region.VirtualAddress);
        Span<byte> contents = stackalloc byte[4];
        Assert.True(memory.TryRead(0x1000, contents));
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, contents.ToArray());
    }

    [Fact]
    public void RejectedOverflowingSelfFallbackOffsetDoesNotClearGuestMemory()
    {
        var self = CreateSelfWithLoadSegment([0xAA]);
        var segment = self.AsSpan(SelfHeaderSize, SelfSegmentSize);
        BinaryPrimitives.WriteUInt64LittleEndian(segment, 0);
        var programHeaderOffset = SelfHeaderSize + SelfSegmentSize + ElfHeaderSize;
        BinaryPrimitives.WriteUInt64LittleEndian(
            self.AsSpan(programHeaderOffset + sizeof(ulong)),
            ulong.MaxValue);
        var memory = CreateSentinelMemory();

        var exception = Assert.Throws<InvalidDataException>(() =>
            new SelfLoader().Load(self, memory));

        Assert.Contains("offset overflows", exception.Message, StringComparison.OrdinalIgnoreCase);
        AssertSentinelMemoryPreserved(memory);
    }

    [HostX64Fact]
    public void ReservesThroughNonzeroFirstLoadSegment()
    {
        const ulong segmentVirtualAddress = 0x2000;
        byte[] payload = [0x11, 0x22, 0x33, 0x44];
        var elf = CreateElfWithLoadSegment(
            fileOffset: ElfHeaderSize + ProgramHeaderSize,
            virtualAddress: segmentVirtualAddress,
            fileSize: (ulong)payload.Length,
            memorySize: (ulong)payload.Length,
            payload: payload);
        using var memory = new PhysicalVirtualMemory();

        var image = new SelfLoader().Load(elf, memory);

        Span<byte> loaded = stackalloc byte[payload.Length];
        Assert.True(memory.TryRead(image.EntryPoint + segmentVirtualAddress, loaded));
        Assert.Equal(payload, loaded.ToArray());
    }

    [HostX64Fact]
    public async Task LoadsAndExecutesMinimalElfImage()
    {
        if (await NativeTestProcess.RunIfNeededAsync(typeof(SelfLoaderTests)))
        {
            return;
        }

        var elf = CreateElfWithLoadSegment(
            fileOffset: ElfHeaderSize + ProgramHeaderSize,
            virtualAddress: 0,
            fileSize: 3,
            memorySize: 3,
            payload:
            [
                0x31, 0xC0, // xor eax, eax
                0xC3,       // ret
            ]);
        using var memory = new PhysicalVirtualMemory();
        var image = new SelfLoader().Load(elf, memory);
        using var dispatcher = new CpuDispatcher(memory, new ModuleManager());

        var result = dispatcher.DispatchModuleInitializer(
            image.EntryPoint,
            Generation.Gen5,
            image.ImportStubs,
            image.RuntimeSymbols,
            moduleName: "synthetic-loader-smoke");

        Assert.True(
            result == OrbisGen2Result.ORBIS_GEN2_OK,
            dispatcher.LastNotImplementedInfo?.Detail ?? $"Unexpected result: {result}");
        Assert.Equal(CpuExitReason.ReturnedToHost, dispatcher.LastSessionSummary.Reason);
    }

    [HostX64Fact]
    public async Task ProcessEntryReceivesExpectedAbiFrame()
    {
        if (await NativeTestProcess.RunIfNeededAsync(typeof(SelfLoaderTests)))
        {
            return;
        }

        var probe = CreateProcessEntryProbe();
        var elf = CreateElfWithLoadSegment(
            fileOffset: ElfHeaderSize + ProgramHeaderSize,
            virtualAddress: 0,
            fileSize: (ulong)probe.Length,
            memorySize: (ulong)probe.Length,
            payload: probe);
        using var memory = new PhysicalVirtualMemory();
        var image = new SelfLoader().Load(elf, memory);
        using var dispatcher = new CpuDispatcher(memory, new ModuleManager());

        var result = dispatcher.DispatchEntry(
            image.EntryPoint,
            Generation.Gen5,
            image.ImportStubs,
            image.RuntimeSymbols,
            processImageName: "smoke.bin");

        Assert.True(
            result == OrbisGen2Result.ORBIS_GEN2_OK,
            dispatcher.LastNotImplementedInfo?.Detail ?? $"Unexpected result: {result}");
        Assert.Equal(CpuExitReason.ReturnedToHost, dispatcher.LastSessionSummary.Reason);
    }

    private static byte[] CreateProcessEntryProbe()
    {
        var code = new List<byte>();
        var failureBranches = new List<(int DisplacementOffset, byte ErrorCode)>();

        void Emit(params byte[] bytes) => code.AddRange(bytes);
        void JumpToFailure(byte conditionOpcode, byte errorCode)
        {
            Emit(0x0F, conditionOpcode, 0, 0, 0, 0);
            failureBranches.Add((code.Count - sizeof(int), errorCode));
        }

        Emit(0x48, 0x85, 0xFF);                   // test rdi, rdi
        JumpToFailure(0x84, 1);                   // jz failure
        Emit(0x83, 0x3F, 0x01);                   // cmp dword ptr [rdi], 1
        JumpToFailure(0x85, 2);                   // jne failure
        Emit(0x83, 0x7F, 0x04, 0x00);             // cmp dword ptr [rdi+4], 0
        JumpToFailure(0x85, 3);                   // jne failure
        Emit(0x48, 0x8B, 0x47, 0x08);             // mov rax, [rdi+8]
        Emit(0x48, 0x85, 0xC0);                   // test rax, rax
        JumpToFailure(0x84, 4);                   // jz failure
        Emit(0x81, 0x38, 0x73, 0x6D, 0x6F, 0x6B); // cmp dword ptr [rax], "smok"
        JumpToFailure(0x85, 5);                   // jne failure
        Emit(0x81, 0x78, 0x04, 0x65, 0x2E, 0x62, 0x69); // cmp [rax+4], "e.bi"
        JumpToFailure(0x85, 6);                   // jne failure
        Emit(0x80, 0x78, 0x08, 0x6E);             // cmp byte ptr [rax+8], 'n'
        JumpToFailure(0x85, 7);                   // jne failure
        Emit(0x80, 0x78, 0x09, 0x00);             // cmp byte ptr [rax+9], 0
        JumpToFailure(0x85, 8);                   // jne failure
        Emit(0x48, 0x83, 0x7F, 0x10, 0x00);       // cmp qword ptr [rdi+0x10], 0
        JumpToFailure(0x85, 9);                   // jne failure
        Emit(0x48, 0x83, 0x7F, 0x18, 0x00);       // cmp qword ptr [rdi+0x18], 0
        JumpToFailure(0x85, 10);                  // jne failure
        Emit(0x48, 0x85, 0xF6);                   // test rsi, rsi
        JumpToFailure(0x84, 11);                  // jz failure
        Emit(0x48, 0x85, 0xD2);                   // test rdx, rdx
        JumpToFailure(0x85, 12);                  // jne failure
        Emit(0x48, 0x85, 0xC9);                   // test rcx, rcx
        JumpToFailure(0x85, 13);                  // jne failure
        Emit(0x4D, 0x85, 0xC0);                   // test r8, r8
        JumpToFailure(0x85, 14);                  // jne failure
        Emit(0x4D, 0x85, 0xC9);                   // test r9, r9
        JumpToFailure(0x85, 15);                  // jne failure
        Emit(0x31, 0xC0, 0xC3);                   // xor eax, eax; ret

        foreach (var (displacementOffset, errorCode) in failureBranches)
        {
            var failureOffset = code.Count;
            Emit(0xB8, errorCode, 0x00, 0x00, 0x00, 0xC3); // mov eax, errorCode; ret
            var displacement = checked(failureOffset - (displacementOffset + sizeof(int)));
            code[displacementOffset] = (byte)displacement;
            code[displacementOffset + 1] = (byte)(displacement >> 8);
            code[displacementOffset + 2] = (byte)(displacement >> 16);
            code[displacementOffset + 3] = (byte)(displacement >> 24);
        }

        return code.ToArray();
    }

    private static VirtualMemory CreateSentinelMemory()
    {
        var memory = new VirtualMemory();
        memory.Map(
            virtualAddress: 0x1000,
            memorySize: 4,
            fileOffset: 0,
            fileData: new byte[] { 1, 2, 3, 4 },
            protection: ProgramHeaderFlags.Read | ProgramHeaderFlags.Write);
        return memory;
    }

    private static void AssertSentinelMemoryPreserved(VirtualMemory memory)
    {
        var region = Assert.Single(memory.SnapshotRegions());
        Assert.Equal(0x1000UL, region.VirtualAddress);
        Span<byte> contents = stackalloc byte[4];
        Assert.True(memory.TryRead(0x1000, contents));
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, contents.ToArray());
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

    private static byte[] CreateSelfWithLoadSegment(
        byte[] payload,
        ulong segmentAttributes = 0)
    {
        var elf = CreateElfWithLoadSegment(
            fileOffset: SyntheticSelfPayloadOffset,
            virtualAddress: 0x2000,
            fileSize: (ulong)payload.Length,
            memorySize: (ulong)payload.Length,
            payload: payload);
        var elfOffset = SelfHeaderSize + SelfSegmentSize;
        var image = new byte[SyntheticSelfPayloadOffset + payload.Length];

        byte[] selfIdentity =
        [
            0x4F, 0x15, 0x3D, 0x1D,
            0x00, 0x01, 0x01, 0x12,
            0x01, 0x01, 0x00, 0x00,
        ];
        selfIdentity.CopyTo(image, 0);
        BinaryPrimitives.WriteUInt64LittleEndian(
            image.AsSpan(16),
            SyntheticSelfPayloadOffset);
        BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(24), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(26), 0x22);

        var segment = image.AsSpan(SelfHeaderSize, SelfSegmentSize);
        var segmentType = 0x800UL | segmentAttributes;
        BinaryPrimitives.WriteUInt64LittleEndian(segment, segmentType);
        BinaryPrimitives.WriteUInt64LittleEndian(segment[8..], SyntheticSelfPayloadOffset);
        BinaryPrimitives.WriteUInt64LittleEndian(segment[16..], (ulong)payload.Length);
        BinaryPrimitives.WriteUInt64LittleEndian(segment[24..], (ulong)payload.Length);

        elf.AsSpan(0, ElfHeaderSize + ProgramHeaderSize).CopyTo(image.AsSpan(elfOffset));
        payload.CopyTo(image.AsSpan(SyntheticSelfPayloadOffset));
        return image;
    }

    private static byte[] CreateSelfWithNestedDynamicSegment(
        ulong initializer,
        bool dynamicInsidePayload = true,
        ulong segmentAttributes = 0,
        bool payloadFitsInImage = true)
    {
        const int imageSize = 0x500;
        const ulong payloadLogicalOffset = 0x200;
        const ulong payloadFileSize = 0x100;
        const ulong mappedPayloadPhysicalOffset = 0x300;
        const ulong truncatedPayloadPhysicalOffset = 0x4C0;
        const ulong dynamicOffsetInPayload = 0x40;
        const ulong payloadVirtualAddress = 0x0900_0000;
        const ulong dynamicFileSize = 0x20;
        const ulong unmappedDynamicOffset = 0x600;

        var image = new byte[imageSize];
        var selfHeader = image.AsSpan(0, SelfHeaderSize);
        BinaryPrimitives.WriteUInt32BigEndian(selfHeader, 0x5414F5EE);
        selfHeader[4] = 0x10;
        selfHeader[5] = 0x01;
        selfHeader[6] = 0x01;
        selfHeader[7] = 0x12;
        BinaryPrimitives.WriteUInt32LittleEndian(selfHeader[8..], 0x1000_0101);
        BinaryPrimitives.WriteUInt16LittleEndian(selfHeader[12..], SelfHeaderSize);
        BinaryPrimitives.WriteUInt64LittleEndian(selfHeader[16..], imageSize);
        BinaryPrimitives.WriteUInt16LittleEndian(selfHeader[24..], 1);
        BinaryPrimitives.WriteUInt16LittleEndian(selfHeader[26..], 0x32);

        var selfSegment = image.AsSpan(SelfHeaderSize, SelfSegmentSize);
        var payloadPhysicalOffset = payloadFitsInImage
            ? mappedPayloadPhysicalOffset
            : truncatedPayloadPhysicalOffset;
        BinaryPrimitives.WriteUInt64LittleEndian(selfSegment, 0x800 | segmentAttributes);
        BinaryPrimitives.WriteUInt64LittleEndian(selfSegment[8..], payloadPhysicalOffset);
        BinaryPrimitives.WriteUInt64LittleEndian(
            selfSegment[16..],
            payloadFitsInImage
                ? payloadFileSize
                : (ulong)(imageSize - (int)truncatedPayloadPhysicalOffset));
        BinaryPrimitives.WriteUInt64LittleEndian(selfSegment[24..], payloadFileSize);

        var elfOffset = SelfHeaderSize + SelfSegmentSize;
        var elf = CreateElf(
            programHeaderOffset: ElfHeaderSize,
            programHeaderCount: 2);
        elf.AsSpan(0, ElfHeaderSize).CopyTo(image.AsSpan(elfOffset));

        var programHeaders = image.AsSpan(elfOffset + ElfHeaderSize);
        WriteNestedSelfProgramHeader(
            programHeaders,
            ProgramHeaderType.SceDynLibData,
            payloadLogicalOffset,
            payloadVirtualAddress,
            payloadFileSize,
            payloadFileSize,
            0x10);
        var dynamicLogicalOffset = dynamicInsidePayload
            ? payloadLogicalOffset + dynamicOffsetInPayload
            : unmappedDynamicOffset;
        var dynamicVirtualAddress = dynamicInsidePayload
            ? payloadVirtualAddress + dynamicOffsetInPayload
            : payloadVirtualAddress + unmappedDynamicOffset;
        WriteNestedSelfProgramHeader(
            programHeaders[ProgramHeaderSize..],
            ProgramHeaderType.Dynamic,
            dynamicLogicalOffset,
            dynamicVirtualAddress,
            dynamicFileSize,
            dynamicFileSize,
            8);

        if (dynamicInsidePayload && payloadFitsInImage)
        {
            var dynamicTable = image.AsSpan(
                checked((int)(payloadPhysicalOffset + dynamicOffsetInPayload)),
                checked((int)dynamicFileSize));
            BinaryPrimitives.WriteInt64LittleEndian(dynamicTable, DynamicTagInit);
            BinaryPrimitives.WriteUInt64LittleEndian(dynamicTable[8..], initializer);
            BinaryPrimitives.WriteInt64LittleEndian(dynamicTable[16..], 0);
            BinaryPrimitives.WriteUInt64LittleEndian(dynamicTable[24..], 0);
        }

        return image;
    }

    private static void WriteNestedSelfProgramHeader(
        Span<byte> header,
        ProgramHeaderType type,
        ulong offset,
        ulong virtualAddress,
        ulong fileSize,
        ulong memorySize,
        ulong alignment)
    {
        header = header[..ProgramHeaderSize];
        header.Clear();
        BinaryPrimitives.WriteUInt32LittleEndian(header, (uint)type);
        BinaryPrimitives.WriteUInt32LittleEndian(header[4..], (uint)ProgramHeaderFlags.Read);
        BinaryPrimitives.WriteUInt64LittleEndian(header[8..], offset);
        BinaryPrimitives.WriteUInt64LittleEndian(header[16..], virtualAddress);
        BinaryPrimitives.WriteUInt64LittleEndian(header[32..], fileSize);
        BinaryPrimitives.WriteUInt64LittleEndian(header[40..], memorySize);
        BinaryPrimitives.WriteUInt64LittleEndian(header[48..], alignment);
    }

    private static void WriteLoadHeader(
        Span<byte> header,
        ulong virtualAddress,
        ulong memorySize)
    {
        BinaryPrimitives.WriteUInt32LittleEndian(header, (uint)ProgramHeaderType.Load);
        BinaryPrimitives.WriteUInt32LittleEndian(
            header[4..],
            (uint)(ProgramHeaderFlags.Read | ProgramHeaderFlags.Write));
        BinaryPrimitives.WriteUInt64LittleEndian(header[16..], virtualAddress);
        BinaryPrimitives.WriteUInt64LittleEndian(header[40..], memorySize);
        BinaryPrimitives.WriteUInt64LittleEndian(header[48..], 1);
    }

    private static ulong AddSignedForTest(ulong value, long addend) =>
        addend >= 0
            ? checked(value + (ulong)addend)
            : checked(value - (ulong)(-(addend + 1)) - 1);

    private static byte[] CreateElfWithDynamicRelocation(
        uint relocationType,
        long addend,
        bool includeTlsSegment = false,
        ProgramHeaderFlags loadProtection = ProgramHeaderFlags.Read | ProgramHeaderFlags.Write)
    {
        const int payloadSize = 0x100;
        const int dynamicVirtualAddress = 0x20;
        const int relocationVirtualAddress = 0x60;
        const int tlsVirtualAddress = 0xC0;
        var programHeaderCount = includeTlsSegment ? 3 : 2;
        var payloadOffset = ElfHeaderSize + (programHeaderCount * ProgramHeaderSize);
        var elf = CreateElf(
            programHeaderOffset: ElfHeaderSize,
            programHeaderCount: checked((ushort)programHeaderCount));
        Array.Resize(ref elf, payloadOffset + payloadSize);

        var loadHeader = elf.AsSpan(ElfHeaderSize, ProgramHeaderSize);
        BinaryPrimitives.WriteUInt32LittleEndian(loadHeader, (uint)ProgramHeaderType.Load);
        BinaryPrimitives.WriteUInt32LittleEndian(
            loadHeader[4..],
            (uint)loadProtection);
        BinaryPrimitives.WriteUInt64LittleEndian(loadHeader[8..], (ulong)payloadOffset);
        BinaryPrimitives.WriteUInt64LittleEndian(loadHeader[32..], payloadSize);
        BinaryPrimitives.WriteUInt64LittleEndian(loadHeader[40..], payloadSize);
        BinaryPrimitives.WriteUInt64LittleEndian(loadHeader[48..], 8);

        var dynamicHeader = elf.AsSpan(
            ElfHeaderSize + ProgramHeaderSize,
            ProgramHeaderSize);
        BinaryPrimitives.WriteUInt32LittleEndian(dynamicHeader, (uint)ProgramHeaderType.Dynamic);
        BinaryPrimitives.WriteUInt32LittleEndian(dynamicHeader[4..], (uint)ProgramHeaderFlags.Read);
        BinaryPrimitives.WriteUInt64LittleEndian(
            dynamicHeader[8..],
            (ulong)(payloadOffset + dynamicVirtualAddress));
        BinaryPrimitives.WriteUInt64LittleEndian(dynamicHeader[16..], dynamicVirtualAddress);
        BinaryPrimitives.WriteUInt64LittleEndian(dynamicHeader[32..], 3 * DynamicEntrySize);
        BinaryPrimitives.WriteUInt64LittleEndian(dynamicHeader[40..], 3 * DynamicEntrySize);
        BinaryPrimitives.WriteUInt64LittleEndian(dynamicHeader[48..], 8);

        if (includeTlsSegment)
        {
            WriteTlsHeader(elf, payloadOffset, tlsVirtualAddress);
        }

        var dynamicTable = elf.AsSpan(
            payloadOffset + dynamicVirtualAddress,
            3 * DynamicEntrySize);
        BinaryPrimitives.WriteInt64LittleEndian(dynamicTable, DynamicTagRela);
        BinaryPrimitives.WriteUInt64LittleEndian(
            dynamicTable[sizeof(long)..],
            relocationVirtualAddress);
        BinaryPrimitives.WriteInt64LittleEndian(
            dynamicTable[DynamicEntrySize..],
            DynamicTagRelaSize);
        BinaryPrimitives.WriteUInt64LittleEndian(
            dynamicTable[(DynamicEntrySize + sizeof(long))..],
            ElfRelocationSize);

        var relocation = elf.AsSpan(
            payloadOffset + relocationVirtualAddress,
            ElfRelocationSize);
        BinaryPrimitives.WriteUInt64LittleEndian(
            relocation,
            SyntheticRelocationTargetVirtualAddress);
        BinaryPrimitives.WriteUInt64LittleEndian(
            relocation[sizeof(ulong)..],
            relocationType);
        BinaryPrimitives.WriteInt64LittleEndian(
            relocation[(2 * sizeof(ulong))..],
            addend);
        return elf;
    }

    private static byte[] CreateElfWithSymbolRelocation(
        ulong originalTargetValue,
        byte symbolBinding,
        byte symbolType,
        ulong symbolValue,
        string symbolName,
        long addend,
        uint relocationType = Absolute64RelocationType,
        bool includeTlsSegment = false,
        ushort? importLibraryId = null,
        string? importLibraryName = null,
        ushort? importModuleId = null,
        string? importModuleName = null)
    {
        const int payloadSize = 0x260;
        const int dynamicVirtualAddress = 0x180;
        const int relocationVirtualAddress = 0xB0;
        const int symbolTableVirtualAddress = 0xD0;
        const int stringTableVirtualAddress = 0x110;
        const int tlsVirtualAddress = 0x240;
        const int dynamicEntryCount = 8;
        var symbolNameBytes = System.Text.Encoding.ASCII.GetBytes(symbolName);
        var libraryNameBytes = System.Text.Encoding.ASCII.GetBytes(importLibraryName ?? string.Empty);
        var moduleNameBytes = System.Text.Encoding.ASCII.GetBytes(importModuleName ?? string.Empty);
        var libraryNameOffset = checked(2 + symbolNameBytes.Length);
        var moduleNameOffset = checked(libraryNameOffset + libraryNameBytes.Length + 1);
        var stringTableSize = checked(moduleNameOffset + moduleNameBytes.Length + 1);
        var programHeaderCount = includeTlsSegment ? 3 : 2;
        var payloadOffset = ElfHeaderSize + (programHeaderCount * ProgramHeaderSize);
        var elf = CreateElf(
            programHeaderOffset: ElfHeaderSize,
            programHeaderCount: checked((ushort)programHeaderCount));
        Array.Resize(ref elf, payloadOffset + payloadSize);

        var loadHeader = elf.AsSpan(ElfHeaderSize, ProgramHeaderSize);
        BinaryPrimitives.WriteUInt32LittleEndian(loadHeader, (uint)ProgramHeaderType.Load);
        BinaryPrimitives.WriteUInt32LittleEndian(
            loadHeader[4..],
            (uint)(ProgramHeaderFlags.Read | ProgramHeaderFlags.Write));
        BinaryPrimitives.WriteUInt64LittleEndian(loadHeader[8..], (ulong)payloadOffset);
        BinaryPrimitives.WriteUInt64LittleEndian(loadHeader[32..], payloadSize);
        BinaryPrimitives.WriteUInt64LittleEndian(loadHeader[40..], payloadSize);
        BinaryPrimitives.WriteUInt64LittleEndian(loadHeader[48..], 8);

        var dynamicHeader = elf.AsSpan(
            ElfHeaderSize + ProgramHeaderSize,
            ProgramHeaderSize);
        BinaryPrimitives.WriteUInt32LittleEndian(dynamicHeader, (uint)ProgramHeaderType.Dynamic);
        BinaryPrimitives.WriteUInt32LittleEndian(dynamicHeader[4..], (uint)ProgramHeaderFlags.Read);
        BinaryPrimitives.WriteUInt64LittleEndian(
            dynamicHeader[8..],
            (ulong)(payloadOffset + dynamicVirtualAddress));
        BinaryPrimitives.WriteUInt64LittleEndian(dynamicHeader[16..], dynamicVirtualAddress);
        BinaryPrimitives.WriteUInt64LittleEndian(
            dynamicHeader[32..],
            dynamicEntryCount * DynamicEntrySize);
        BinaryPrimitives.WriteUInt64LittleEndian(
            dynamicHeader[40..],
            dynamicEntryCount * DynamicEntrySize);
        BinaryPrimitives.WriteUInt64LittleEndian(dynamicHeader[48..], 8);

        if (includeTlsSegment)
        {
            WriteTlsHeader(elf, payloadOffset, tlsVirtualAddress);
        }

        var dynamicTable = elf.AsSpan(
            payloadOffset + dynamicVirtualAddress,
            dynamicEntryCount * DynamicEntrySize);
        WriteDynamicEntry(dynamicTable, 0, DynamicTagRela, relocationVirtualAddress);
        WriteDynamicEntry(dynamicTable, 1, DynamicTagRelaSize, ElfRelocationSize);
        WriteDynamicEntry(dynamicTable, 2, DynamicTagStringTable, stringTableVirtualAddress);
        WriteDynamicEntry(
            dynamicTable,
            3,
            DynamicTagStringTableSize,
            checked((ulong)stringTableSize));
        WriteDynamicEntry(dynamicTable, 4, DynamicTagSymbolTable, symbolTableVirtualAddress);
        if (importLibraryId is { } libraryId && libraryNameBytes.Length != 0)
        {
            WriteDynamicEntry(
                dynamicTable,
                5,
                DynamicTagPs5ImportLibrary,
                ((ulong)libraryId << 48) | checked((uint)libraryNameOffset));
        }

        if (importModuleId is { } moduleId && moduleNameBytes.Length != 0)
        {
            WriteDynamicEntry(
                dynamicTable,
                6,
                DynamicTagPs5NeededModule,
                ((ulong)moduleId << 48) | checked((uint)moduleNameOffset));
        }

        var relocation = elf.AsSpan(
            payloadOffset + relocationVirtualAddress,
            ElfRelocationSize);
        BinaryPrimitives.WriteUInt64LittleEndian(
            relocation,
            SyntheticRelocationTargetVirtualAddress);
        BinaryPrimitives.WriteUInt64LittleEndian(
            relocation[sizeof(ulong)..],
            ((ulong)1 << 32) | relocationType);
        BinaryPrimitives.WriteInt64LittleEndian(
            relocation[(2 * sizeof(ulong))..],
            addend);

        var symbol = elf.AsSpan(
            payloadOffset + symbolTableVirtualAddress + 24,
            24);
        BinaryPrimitives.WriteUInt32LittleEndian(
            symbol,
            symbolNameBytes.Length == 0 ? 0u : 1u);
        symbol[4] = checked((byte)((symbolBinding << 4) | symbolType));
        BinaryPrimitives.WriteUInt16LittleEndian(
            symbol[6..],
            symbolValue == 0 ? (ushort)0 : (ushort)1);
        BinaryPrimitives.WriteUInt64LittleEndian(symbol[8..], symbolValue);
        elf[payloadOffset + stringTableVirtualAddress] = 0;
        symbolNameBytes.CopyTo(
            elf.AsSpan(payloadOffset + stringTableVirtualAddress + 1));
        libraryNameBytes.CopyTo(
            elf.AsSpan(payloadOffset + stringTableVirtualAddress + libraryNameOffset));
        moduleNameBytes.CopyTo(
            elf.AsSpan(payloadOffset + stringTableVirtualAddress + moduleNameOffset));
        BinaryPrimitives.WriteUInt64LittleEndian(
            elf.AsSpan(
                payloadOffset + SyntheticRelocationTargetVirtualAddress,
                sizeof(ulong)),
            originalTargetValue);
        return elf;
    }

    private static void WriteTlsHeader(byte[] elf, int payloadOffset, int tlsVirtualAddress)
    {
        var tlsHeader = elf.AsSpan(
            ElfHeaderSize + (2 * ProgramHeaderSize),
            ProgramHeaderSize);
        BinaryPrimitives.WriteUInt32LittleEndian(tlsHeader, (uint)ProgramHeaderType.Tls);
        BinaryPrimitives.WriteUInt32LittleEndian(tlsHeader[4..], (uint)ProgramHeaderFlags.Read);
        BinaryPrimitives.WriteUInt64LittleEndian(
            tlsHeader[8..],
            checked((ulong)(payloadOffset + tlsVirtualAddress)));
        BinaryPrimitives.WriteUInt64LittleEndian(tlsHeader[16..], checked((ulong)tlsVirtualAddress));
        BinaryPrimitives.WriteUInt64LittleEndian(tlsHeader[32..], 2);
        BinaryPrimitives.WriteUInt64LittleEndian(tlsHeader[40..], 8);
        BinaryPrimitives.WriteUInt64LittleEndian(tlsHeader[48..], 8);
        elf[payloadOffset + tlsVirtualAddress] = 0xA5;
        elf[payloadOffset + tlsVirtualAddress + 1] = 0x5A;
    }

    private static void WriteDynamicEntry(
        Span<byte> dynamicTable,
        int index,
        long tag,
        ulong value)
    {
        var entry = dynamicTable[(index * DynamicEntrySize)..];
        BinaryPrimitives.WriteInt64LittleEndian(entry, tag);
        BinaryPrimitives.WriteUInt64LittleEndian(entry[sizeof(long)..], value);
    }

    private static ulong ReadSyntheticRelocationTarget(
        VirtualMemory memory,
        SelfImage image)
    {
        Span<byte> patchedTarget = stackalloc byte[sizeof(ulong)];
        Assert.True(memory.TryRead(
            image.EntryPoint + SyntheticRelocationTargetVirtualAddress,
            patchedTarget));
        return BinaryPrimitives.ReadUInt64LittleEndian(patchedTarget);
    }

    private sealed class RecordingVirtualMemory : IVirtualMemory
    {
        private readonly VirtualMemory _inner = new();

        public int LargestReadRequest { get; private set; }

        public void Clear() => _inner.Clear();

        public void Map(
            ulong virtualAddress,
            ulong memorySize,
            ulong fileOffset,
            ReadOnlySpan<byte> fileData,
            ProgramHeaderFlags protection) =>
            _inner.Map(
                virtualAddress,
                memorySize,
                fileOffset,
                fileData,
                protection);

        public IReadOnlyList<VirtualMemoryRegion> SnapshotRegions() =>
            _inner.SnapshotRegions();

        public bool TryRead(ulong virtualAddress, Span<byte> destination)
        {
            LargestReadRequest = Math.Max(LargestReadRequest, destination.Length);
            return _inner.TryRead(virtualAddress, destination);
        }

        public bool TryWrite(ulong virtualAddress, ReadOnlySpan<byte> source) =>
            _inner.TryWrite(virtualAddress, source);
    }
}
