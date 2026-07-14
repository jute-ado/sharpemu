// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using SharpEmu.Core.Cpu;
using SharpEmu.Core.Loader;
using SharpEmu.Core.Memory;
using SharpEmu.HLE;
using Xunit;

namespace SharpEmu.Core.Tests;

public sealed class SelfLoaderTests
{
    private const int ElfHeaderSize = 64;
    private const int ProgramHeaderSize = 56;
    private const int SelfHeaderSize = 32;
    private const int SelfSegmentSize = 32;
    private const int SyntheticSelfPayloadOffset = 0x200;
    private const int DynamicEntrySize = 16;
    private const long DynamicTagInit = 0x0C;
    private const long DynamicTagRela = 0x07;
    private const long DynamicTagRelaSize = 0x08;

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

    [WindowsX64Fact]
    public void LoadsAndExecutesMinimalElfImage()
    {
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

    [WindowsX64Fact]
    public void ProcessEntryReceivesExpectedAbiFrame()
    {
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
