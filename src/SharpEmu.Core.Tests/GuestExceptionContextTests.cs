// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using SharpEmu.Core.Cpu.Native;
using SharpEmu.HLE;
using Xunit;

namespace SharpEmu.Core.Tests;

public sealed class GuestExceptionContextTests
{
    private const ulong ContextAddress = 0x1000;
    private const int MachineContextOffset = 0x40;

    [Fact]
    public void LiveImportContextUsesCurrentRegistersWhenNoContinuationExists()
    {
        var memory = new RecordingMemory();
        var context = new CpuContext(memory, Generation.Gen5)
        {
            Rip = 0x1234_5678,
            Rflags = 0x246,
            FsBase = 0x7000,
            GsBase = 0x8000,
        };
        context[CpuRegister.Rdi] = 0x11;
        context[CpuRegister.Rsp] = 0x9000;

        Assert.True(
            DirectExecutionBackend.TryWriteGuestExceptionContext(
                context,
                ContextAddress,
                default,
                0x500));

        Assert.Equal(
            0x11UL,
            Read64(memory.Bytes, MachineContextOffset + 0x08));
        Assert.Equal(
            context.Rip,
            Read64(memory.Bytes, MachineContextOffset + 0xA0));
        Assert.Equal(
            0x9000UL,
            Read64(memory.Bytes, MachineContextOffset + 0xB8));
        Assert.Equal(
            0x246UL,
            Read64(memory.Bytes, MachineContextOffset + 0xB0));
        Assert.Equal(
            0x7000UL,
            Read64(memory.Bytes, MachineContextOffset + 0x440));
        Assert.Equal(
            0x8000UL,
            Read64(memory.Bytes, MachineContextOffset + 0x448));
        Assert.Equal(
            0x480UL,
            Read64(memory.Bytes, MachineContextOffset + 0xC8));
    }

    [Fact]
    public void ImportBoundaryContinuationOverridesLiveRegisters()
    {
        var memory = new RecordingMemory();
        var context = new CpuContext(memory, Generation.Gen5)
        {
            Rip = 0x1111_1111,
            FsBase = 0x2222,
            GsBase = 0x3333,
        };
        context[CpuRegister.Rdi] = 0x44;
        context[CpuRegister.Rsp] = 0x5555;
        var continuation = CreateContinuation(
            rip: 0x1234_5678,
            rsp: 0x8765_4321,
            rdi: 0xAABB,
            fsBase: 0xCCDD,
            gsBase: 0xEEFF);

        Assert.True(
            DirectExecutionBackend.TryWriteGuestExceptionContext(
                context,
                ContextAddress,
                continuation,
                0x500));

        Assert.Equal(
            0xAABBUL,
            Read64(memory.Bytes, MachineContextOffset + 0x08));
        Assert.Equal(
            0x1234_5678UL,
            Read64(memory.Bytes, MachineContextOffset + 0xA0));
        Assert.Equal(
            0x8765_4321UL,
            Read64(memory.Bytes, MachineContextOffset + 0xB8));
        Assert.Equal(
            0xCCDDUL,
            Read64(memory.Bytes, MachineContextOffset + 0x440));
        Assert.Equal(
            0xEEFFUL,
            Read64(memory.Bytes, MachineContextOffset + 0x448));
    }

    private static GuestCpuContinuation CreateContinuation(
        ulong rip,
        ulong rsp,
        ulong rdi,
        ulong fsBase,
        ulong gsBase) =>
        new(
            Rip: rip,
            Rsp: rsp,
            ReturnSlotAddress: 0x100,
            Rflags: 0x202,
            FsBase: fsBase,
            GsBase: gsBase,
            Rax: 1,
            Rcx: 2,
            Rdx: 3,
            Rbx: 4,
            Rbp: 5,
            Rsi: 6,
            Rdi: rdi,
            R8: 8,
            R9: 9,
            R10: 10,
            R11: 11,
            R12: 12,
            R13: 13,
            R14: 14,
            R15: 15,
            FpuControlWord: 0x037F,
            Mxcsr: 0x1F80,
            RestoreFullFpuState: false);

    private static ulong Read64(byte[] bytes, int offset) =>
        BinaryPrimitives.ReadUInt64LittleEndian(
            bytes.AsSpan(offset, sizeof(ulong)));

    private sealed class RecordingMemory : ICpuMemory
    {
        public byte[] Bytes { get; private set; } = [];

        public bool TryRead(
            ulong virtualAddress,
            Span<byte> destination) =>
            false;

        public bool TryWrite(
            ulong virtualAddress,
            ReadOnlySpan<byte> source)
        {
            if (virtualAddress != ContextAddress)
            {
                return false;
            }

            Bytes = source.ToArray();
            return true;
        }
    }
}
