// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Core.Cpu.Native;
using System.Buffers.Binary;
using Xunit;

namespace SharpEmu.Core.Tests;

public sealed class JitStubsTests
{
    [Fact]
    public unsafe void RelativeBranchEncodersAcceptSignedBoundaryTargets()
    {
        var jumpCode = new byte[JitStubs.JmpWithIndex.Size];
        var callCode = new byte[JitStubs.Call9.Size];

        fixed (byte* jumpStart = jumpCode)
        fixed (byte* callStart = callCode)
        {
            JitStubs.CreateJmpWithIndex(
                jumpStart,
                7,
                (void*)((nint)(jumpStart + 10) + int.MaxValue));
            JitStubs.CreateCall9(
                callStart,
                (void*)((nint)(callStart + 5) + int.MinValue));
        }

        Assert.Equal(0x68, jumpCode[0]);
        Assert.Equal(7u, BinaryPrimitives.ReadUInt32LittleEndian(jumpCode.AsSpan(1, 4)));
        Assert.Equal(0xE9, jumpCode[5]);
        Assert.Equal(int.MaxValue, BinaryPrimitives.ReadInt32LittleEndian(jumpCode.AsSpan(6, 4)));
        Assert.Equal(0xE8, callCode[0]);
        Assert.Equal(int.MinValue, BinaryPrimitives.ReadInt32LittleEndian(callCode.AsSpan(1, 4)));
    }

    [Fact]
    public unsafe void RelativeJumpRejectsOutOfRangeHandlerWithoutMutation()
    {
        var code = Enumerable.Repeat((byte)0xCC, JitStubs.JmpWithIndex.Size).ToArray();

        fixed (byte* start = code)
        {
            var nextInstruction = (nint)(start + 10);
            var outOfRangeHandler = (void*)(nextInstruction + (long)int.MaxValue + 1);
            var rejected = false;

            try
            {
                JitStubs.CreateJmpWithIndex(start, 7, outOfRangeHandler);
            }
            catch (ArgumentOutOfRangeException)
            {
                rejected = true;
            }

            Assert.True(rejected);
            Assert.All(code, value => Assert.Equal(0xCC, value));
        }
    }

    [Fact]
    public unsafe void RelativeCallRejectsOutOfRangeTargetWithoutMutation()
    {
        var code = Enumerable.Repeat((byte)0xCC, JitStubs.Call9.Size).ToArray();

        fixed (byte* start = code)
        {
            var nextInstruction = (nint)(start + 5);
            var outOfRangeTarget = (void*)(nextInstruction + (long)int.MinValue - 1);
            var rejected = false;

            try
            {
                JitStubs.CreateCall9(start, outOfRangeTarget);
            }
            catch (ArgumentOutOfRangeException)
            {
                rejected = true;
            }

            Assert.True(rejected);
            Assert.All(code, value => Assert.Equal(0xCC, value));
        }
    }

    [Fact]
    public unsafe void TlsPatternScannerFindsPatternThatFillsBuffer()
    {
        var code = JitStubs.TlsAccessPattern.ToArray();

        fixed (byte* start = code)
        {
            var matches = JitStubs.FindTlsAccessPatterns(start, code.Length);

            Assert.Equal([(nint)start], matches);
        }
    }

    [Fact]
    public unsafe void TlsPatternScannerFindsPatternAtFinalValidOffset()
    {
        var pattern = JitStubs.TlsAccessPattern;
        var code = new byte[pattern.Length + 4];
        pattern.CopyTo(code.AsSpan(4));

        fixed (byte* start = code)
        {
            var matches = JitStubs.FindTlsAccessPatterns(start, code.Length);

            Assert.Equal([(nint)(start + 4)], matches);
        }
    }

    [Fact]
    public unsafe void TlsPatternScannerRejectsBuffersShorterThanPattern()
    {
        var code = new byte[JitStubs.TlsAccessPattern.Length - 1];

        fixed (byte* start = code)
        {
            Assert.Empty(JitStubs.FindTlsAccessPatterns(start, code.Length));
        }
    }
}
