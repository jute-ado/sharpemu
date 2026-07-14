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
}
