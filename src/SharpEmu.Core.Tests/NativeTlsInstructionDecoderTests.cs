// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Core.Cpu.Native;
using Xunit;

namespace SharpEmu.Core.Tests;

public sealed class NativeTlsInstructionDecoderTests
{
    public static TheoryData<byte[], int[]> SupportedInstructions => new()
    {
        {
            [0x64, 0x48, 0x8B, 0x04, 0x25, 0x28, 0x00, 0x00, 0x00],
            [(int)NativeTlsInstructionKind.Load, 9, 0, 0x28, 0, 1]
        },
        {
            [0x64, 0x44, 0x8B, 0x04, 0x25, 0xF0, 0xFF, 0xFF, 0xFF],
            [(int)NativeTlsInstructionKind.Load, 9, 8, -16, 0, 0]
        },
        {
            [0x66, 0x66, 0x64, 0x4C, 0x8B, 0x3C, 0x25, 0x40, 0x00, 0x00, 0x00],
            [(int)NativeTlsInstructionKind.Load, 11, 15, 0x40, 0, 1]
        },
        {
            [0x64, 0x89, 0x04, 0x25, 0x50, 0x00, 0x00, 0x00],
            [(int)NativeTlsInstructionKind.RegisterStore, 8, 0, 0x50, 0, 0]
        },
        {
            [0x64, 0x4C, 0x89, 0x3C, 0x25, 0x58, 0x00, 0x00, 0x00],
            [(int)NativeTlsInstructionKind.RegisterStore, 9, 15, 0x58, 0, 1]
        },
        {
            [
                0x64, 0xC7, 0x04, 0x25,
                0x60, 0x00, 0x00, 0x00,
                0x78, 0x56, 0x34, 0x12,
            ],
            [(int)NativeTlsInstructionKind.ImmediateStore, 12, 0, 0x60, 0x12345678, 0]
        },
        {
            [
                0x64, 0x48, 0xC7, 0x04, 0x25,
                0x68, 0x00, 0x00, 0x00,
                0x80, 0xFF, 0xFF, 0xFF,
            ],
            [(int)NativeTlsInstructionKind.ImmediateStore, 13, 0, 0x68, -128, 1]
        },
        {
            [0x64, 0x4C, 0x33, 0x3C, 0x25, 0x28, 0x00, 0x00, 0x00],
            [(int)NativeTlsInstructionKind.StackCanaryXor, 9, 15, 0x28, 0, 1]
        },
    };

    public static TheoryData<byte[]> UnsupportedInstructions => new()
    {
        { Array.Empty<byte>() },
        { [0x64, 0x48, 0x8B] },                         // Truncated instruction.
        { [0x65, 0x48, 0x8B, 0x04, 0x25, 0, 0, 0, 0] }, // GS, not guest FS.
        { [0x64, 0x48, 0x8B, 0x00] },                   // Base-register addressing.
        { [0x64, 0x48, 0x8B, 0x04, 0x05, 0, 0, 0, 0] }, // Indexed addressing.
        { [0x67, 0x64, 0x48, 0x8B, 0x05, 0, 0, 0, 0] }, // 32-bit address size.
        { [0x66, 0x64, 0x8B, 0x04, 0x25, 0, 0, 0, 0] }, // Unsupported 16-bit load.
        { [0x64, 0x48, 0x33, 0x04, 0x25, 0x20, 0, 0, 0] }, // Non-canary XOR.
    };

    [Theory]
    [MemberData(nameof(SupportedInstructions))]
    public void DecodesSupportedAbsoluteFsInstruction(
        byte[] bytes,
        int[] expected)
    {
        Assert.True(NativeTlsInstructionDecoder.TryDecode(bytes, out var actual));
        Assert.Equal((NativeTlsInstructionKind)expected[0], actual.Kind);
        Assert.Equal(expected[1], actual.Length);
        Assert.Equal(expected[2], actual.Register);
        Assert.Equal(expected[3], actual.Displacement);
        Assert.Equal(expected[4], actual.ImmediateValue);
        Assert.Equal(expected[5] != 0, actual.Is64Bit);
    }

    [Theory]
    [MemberData(nameof(UnsupportedInstructions))]
    public void RejectsUnsupportedOrIncompleteInstruction(byte[] bytes)
    {
        Assert.False(NativeTlsInstructionDecoder.TryDecode(bytes, out _));
    }

    [Theory]
    [MemberData(nameof(SupportedInstructions))]
    public void RejectsEveryTruncatedSupportedInstruction(byte[] bytes, int[] expected)
    {
        _ = expected;
        for (var length = 0; length < bytes.Length; length++)
        {
            Assert.False(NativeTlsInstructionDecoder.TryDecode(bytes.AsSpan(0, length), out _));
        }
    }
}
