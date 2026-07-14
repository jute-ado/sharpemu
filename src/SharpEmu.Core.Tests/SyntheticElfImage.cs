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
}
