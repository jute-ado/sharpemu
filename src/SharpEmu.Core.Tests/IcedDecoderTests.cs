// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using Iced.Intel;
using SharpEmu.Core.Cpu.Disasm;
using Xunit;

namespace SharpEmu.Core.Tests;

public sealed class IcedDecoderTests
{
    [Fact]
    public void RejectsEmptyAndTruncatedInstructions()
    {
        Assert.False(IcedDecoder.TryDecode(0x1000, [], out _));
        Assert.False(IcedDecoder.TryDecode(0x1000, [0xE8], out _));
    }

    [Fact]
    public void DecodesBasicInstructionMetadataAndLimitsCapturedBytes()
    {
        byte[] bytes = [0x90, 0xCC, 0xCC, 0xCC, 0xCC, 0xCC, 0xCC, 0xCC,
                        0xCC, 0xCC, 0xCC, 0xCC, 0xCC, 0xCC, 0xCC, 0xCC];

        Assert.True(IcedDecoder.TryDecode(0x1234, bytes, out var instruction));

        Assert.Equal(0x1234UL, instruction.Rip);
        Assert.Equal(1, instruction.Length);
        Assert.Equal("Nop", instruction.Mnemonic);
        Assert.Equal("nop", instruction.Text);
        Assert.Equal(FlowControl.Next, instruction.FlowControl);
        Assert.Null(instruction.NearBranchTarget);
        Assert.Null(instruction.MemoryAddress);
        Assert.Equal(new byte[] { 0x90 }, instruction.Bytes);
    }

    [Fact]
    public void ResolvesRelativeCallAndBackwardJumpTargets()
    {
        Assert.True(IcedDecoder.TryDecode(
            0x1000,
            [0xE8, 0x78, 0x56, 0x34, 0x12],
            out var call));
        Assert.Equal("Call", call.Mnemonic);
        Assert.Equal(FlowControl.Call, call.FlowControl);
        Assert.Equal(0x1234_667DUL, call.NearBranchTarget);

        Assert.True(IcedDecoder.TryDecode(0x2000, [0xEB, 0xFE], out var jump));
        Assert.Equal("Jmp", jump.Mnemonic);
        Assert.Equal(FlowControl.UnconditionalBranch, jump.FlowControl);
        Assert.Equal(0x2000UL, jump.NearBranchTarget);
    }

    [Fact]
    public void ResolvesRipRelativeAndAbsoluteMemoryAddresses()
    {
        Assert.True(IcedDecoder.TryDecode(
            0x1000,
            [0x48, 0x8B, 0x05, 0x78, 0x56, 0x34, 0x12],
            out var ripRelative));
        Assert.Equal(0x1234_667FUL, ripRelative.MemoryAddress);

        Assert.True(IcedDecoder.TryDecode(
            0x2000,
            [0x48, 0xA1, 0x88, 0x77, 0x66, 0x55, 0x44, 0x33, 0x22, 0x11],
            out var absolute));
        Assert.Equal(0x1122_3344_5566_7788UL, absolute.MemoryAddress);
    }

    [Fact]
    public void ReportsAbsoluteMemoryAccessAtZero()
    {
        Assert.True(IcedDecoder.TryDecode(
            0x3000,
            [0x48, 0xA1, 0, 0, 0, 0, 0, 0, 0, 0],
            out var instruction));

        Assert.Equal(0UL, instruction.MemoryAddress);
    }

    [Fact]
    public void LeavesRegisterBasedMemoryAddressUnresolved()
    {
        Assert.True(IcedDecoder.TryDecode(0x4000, [0x48, 0x8B, 0x00], out var instruction));

        Assert.Null(instruction.MemoryAddress);
    }

    [Fact]
    public void FormatsDiagnosticByteSequences()
    {
        Assert.Equal("??", IcedDecoder.FormatBytes([]));
        Assert.Equal("00 7F A5 FF", IcedDecoder.FormatBytes([0x00, 0x7F, 0xA5, 0xFF]));
    }
}
