// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using SharpEmu.Core.Loader;

namespace SharpEmu.Core.Tests;

internal static class SyntheticElfImage
{
    private const int ElfHeaderSize = 64;
    private const int ProgramHeaderSize = 56;

    public static byte[] CreateExecutable(
        ReadOnlySpan<byte> code,
        byte abiVersion = 2,
        ulong virtualAddress = 0,
        ulong entryPoint = 0)
    {
        if (code.IsEmpty)
        {
            throw new ArgumentException("Synthetic executable code cannot be empty.", nameof(code));
        }

        var payloadOffset = ElfHeaderSize + ProgramHeaderSize;
        var image = new byte[checked(payloadOffset + code.Length)];
        var elfHeader = image.AsSpan(0, ElfHeaderSize);
        elfHeader[0] = 0x7F;
        elfHeader[1] = (byte)'E';
        elfHeader[2] = (byte)'L';
        elfHeader[3] = (byte)'F';
        elfHeader[4] = 2; // ELFCLASS64
        elfHeader[5] = 1; // little endian
        elfHeader[6] = 1; // current ELF version
        elfHeader[8] = abiVersion;
        BinaryPrimitives.WriteUInt16LittleEndian(elfHeader[16..], 2); // ET_EXEC
        BinaryPrimitives.WriteUInt16LittleEndian(elfHeader[18..], 0x3E); // x86-64
        BinaryPrimitives.WriteUInt32LittleEndian(elfHeader[20..], 1);
        BinaryPrimitives.WriteUInt64LittleEndian(elfHeader[24..], entryPoint);
        BinaryPrimitives.WriteUInt64LittleEndian(elfHeader[32..], ElfHeaderSize);
        BinaryPrimitives.WriteUInt16LittleEndian(elfHeader[52..], ElfHeaderSize);
        BinaryPrimitives.WriteUInt16LittleEndian(elfHeader[54..], ProgramHeaderSize);
        BinaryPrimitives.WriteUInt16LittleEndian(elfHeader[56..], 1);

        var programHeader = image.AsSpan(ElfHeaderSize, ProgramHeaderSize);
        BinaryPrimitives.WriteUInt32LittleEndian(programHeader, (uint)ProgramHeaderType.Load);
        BinaryPrimitives.WriteUInt32LittleEndian(
            programHeader[4..],
            (uint)(ProgramHeaderFlags.Read | ProgramHeaderFlags.Execute));
        BinaryPrimitives.WriteUInt64LittleEndian(programHeader[8..], (ulong)payloadOffset);
        BinaryPrimitives.WriteUInt64LittleEndian(programHeader[16..], virtualAddress);
        BinaryPrimitives.WriteUInt64LittleEndian(programHeader[32..], (ulong)code.Length);
        BinaryPrimitives.WriteUInt64LittleEndian(programHeader[40..], (ulong)code.Length);
        BinaryPrimitives.WriteUInt64LittleEndian(programHeader[48..], 1);
        code.CopyTo(image.AsSpan(payloadOffset));
        return image;
    }

    public static byte[] CreateModuleWithInitializer(
        ReadOnlySpan<byte> initializerCode,
        byte abiVersion = 2)
    {
        return CreateModuleWithInitializers(
            initializerCode,
            ReadOnlySpan<byte>.Empty,
            abiVersion);
    }

    public static byte[] CreateModuleWithInitializerArray(
        ReadOnlySpan<byte> initializerCode,
        ReadOnlySpan<byte> arrayInitializerCode,
        byte abiVersion = 2)
    {
        if (arrayInitializerCode.IsEmpty)
        {
            throw new ArgumentException(
                "Synthetic array initializer code cannot be empty.",
                nameof(arrayInitializerCode));
        }

        return CreateModuleWithInitializers(
            initializerCode,
            arrayInitializerCode,
            abiVersion);
    }

    private static byte[] CreateModuleWithInitializers(
        ReadOnlySpan<byte> initializerCode,
        ReadOnlySpan<byte> arrayInitializerCode,
        byte abiVersion)
    {
        if (initializerCode.IsEmpty)
        {
            throw new ArgumentException("Synthetic initializer code cannot be empty.", nameof(initializerCode));
        }

        const int dynamicEntrySize = 16;
        const int dynamicVirtualAddress = 0x20;
        const int initArrayVirtualAddress = 0x80;
        const int initializerVirtualAddress = 0x100;
        const int arrayInitializerVirtualAddress = 0x140;
        const int payloadSize = 0x200;
        const long dynamicTagInit = 0x0C;
        const long dynamicTagInitArray = 0x19;
        const long dynamicTagInitArraySize = 0x1B;
        var hasInitArray = !arrayInitializerCode.IsEmpty;
        var dynamicEntryCount = hasInitArray ? 4 : 2;
        var initializerCapacity = hasInitArray
            ? arrayInitializerVirtualAddress - initializerVirtualAddress
            : payloadSize - initializerVirtualAddress;
        if (initializerCode.Length > initializerCapacity)
        {
            throw new ArgumentOutOfRangeException(nameof(initializerCode));
        }
        if (arrayInitializerCode.Length > payloadSize - arrayInitializerVirtualAddress)
        {
            throw new ArgumentOutOfRangeException(nameof(arrayInitializerCode));
        }

        var payloadOffset = ElfHeaderSize + (2 * ProgramHeaderSize);
        var image = new byte[payloadOffset + payloadSize];
        var elfHeader = image.AsSpan(0, ElfHeaderSize);
        elfHeader[0] = 0x7F;
        elfHeader[1] = (byte)'E';
        elfHeader[2] = (byte)'L';
        elfHeader[3] = (byte)'F';
        elfHeader[4] = 2;
        elfHeader[5] = 1;
        elfHeader[6] = 1;
        elfHeader[8] = abiVersion;
        BinaryPrimitives.WriteUInt16LittleEndian(elfHeader[16..], 2);
        BinaryPrimitives.WriteUInt16LittleEndian(elfHeader[18..], 0x3E);
        BinaryPrimitives.WriteUInt32LittleEndian(elfHeader[20..], 1);
        BinaryPrimitives.WriteUInt64LittleEndian(elfHeader[32..], ElfHeaderSize);
        BinaryPrimitives.WriteUInt16LittleEndian(elfHeader[52..], ElfHeaderSize);
        BinaryPrimitives.WriteUInt16LittleEndian(elfHeader[54..], ProgramHeaderSize);
        BinaryPrimitives.WriteUInt16LittleEndian(elfHeader[56..], 2);

        var loadHeader = image.AsSpan(ElfHeaderSize, ProgramHeaderSize);
        BinaryPrimitives.WriteUInt32LittleEndian(loadHeader, (uint)ProgramHeaderType.Load);
        BinaryPrimitives.WriteUInt32LittleEndian(
            loadHeader[4..],
            (uint)(ProgramHeaderFlags.Read | ProgramHeaderFlags.Execute));
        BinaryPrimitives.WriteUInt64LittleEndian(loadHeader[8..], (ulong)payloadOffset);
        BinaryPrimitives.WriteUInt64LittleEndian(loadHeader[32..], payloadSize);
        BinaryPrimitives.WriteUInt64LittleEndian(loadHeader[40..], payloadSize);
        BinaryPrimitives.WriteUInt64LittleEndian(loadHeader[48..], 0x10);

        var dynamicHeader = image.AsSpan(
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
            (ulong)(dynamicEntryCount * dynamicEntrySize));
        BinaryPrimitives.WriteUInt64LittleEndian(
            dynamicHeader[40..],
            (ulong)(dynamicEntryCount * dynamicEntrySize));
        BinaryPrimitives.WriteUInt64LittleEndian(dynamicHeader[48..], 8);

        var dynamicTable = image.AsSpan(
            payloadOffset + dynamicVirtualAddress,
            dynamicEntryCount * dynamicEntrySize);
        BinaryPrimitives.WriteInt64LittleEndian(dynamicTable, dynamicTagInit);
        BinaryPrimitives.WriteUInt64LittleEndian(
            dynamicTable[sizeof(long)..],
            initializerVirtualAddress);
        if (hasInitArray)
        {
            var initArrayEntry = dynamicTable[dynamicEntrySize..];
            BinaryPrimitives.WriteInt64LittleEndian(initArrayEntry, dynamicTagInitArray);
            BinaryPrimitives.WriteUInt64LittleEndian(
                initArrayEntry[sizeof(long)..],
                initArrayVirtualAddress);

            var initArraySizeEntry = dynamicTable[(2 * dynamicEntrySize)..];
            BinaryPrimitives.WriteInt64LittleEndian(initArraySizeEntry, dynamicTagInitArraySize);
            BinaryPrimitives.WriteUInt64LittleEndian(
                initArraySizeEntry[sizeof(long)..],
                sizeof(ulong));

            BinaryPrimitives.WriteUInt64LittleEndian(
                image.AsSpan(payloadOffset + initArrayVirtualAddress),
                arrayInitializerVirtualAddress);
            arrayInitializerCode.CopyTo(
                image.AsSpan(payloadOffset + arrayInitializerVirtualAddress));
        }
        initializerCode.CopyTo(image.AsSpan(payloadOffset + initializerVirtualAddress));
        return image;
    }
}
