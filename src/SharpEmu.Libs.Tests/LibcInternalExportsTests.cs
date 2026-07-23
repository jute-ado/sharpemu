// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;

using SharpEmu.HLE;
using SharpEmu.Libs.LibcInternal;

using Xunit;

namespace SharpEmu.Libs.Tests;

public sealed class LibcInternalExportsTests
{
    private const ulong Base = 0x3_0000_0000;
    private const ulong InfoAddress = Base + 0x100;
    private const ulong JumpBufferAddress = Base + 0x200;
    private const ulong StackPointer = Base + 0x800;
    private const ulong ExpectedInfoSize = 32;

    [Fact]
    public void SetJmp_RegistersForGen4AndGen5()
    {
        var gen4 = new ModuleManager();
        gen4.RegisterExports(
            SharpEmu.Generated.SysAbiExportRegistry.CreateExports(Generation.Gen4));
        var gen5 = new ModuleManager();
        gen5.RegisterExports(
            SharpEmu.Generated.SysAbiExportRegistry.CreateExports(Generation.Gen5));

        Assert.True(gen4.TryGetExport("gNQ1V2vfXDE", out var gen4Export));
        Assert.Equal("setjmp", gen4Export.Name);
        Assert.Equal("libSceLibcInternal", gen4Export.LibraryName);
        Assert.True(gen5.TryGetExport("gNQ1V2vfXDE", out var gen5Export));
        Assert.Equal("setjmp", gen5Export.Name);
        Assert.Equal("libSceLibcInternal", gen5Export.LibraryName);
    }

    [Fact]
    public void SetJmp_CapturesCalleeSavedContextAndPostReturnStack()
    {
        var memory = new FakeCpuMemory(Base, 0x1000);
        var context = new CpuContext(memory, Generation.Gen5);
        const ulong returnRip = 0x8_0012_3456;
        Assert.True(context.TryWriteUInt64(StackPointer, returnRip));

        context[CpuRegister.Rdi] = JumpBufferAddress;
        context[CpuRegister.Rsp] = StackPointer;
        context[CpuRegister.Rbx] = 0x1111;
        context[CpuRegister.Rbp] = 0x2222;
        context[CpuRegister.R12] = 0x3333;
        context[CpuRegister.R13] = 0x4444;
        context[CpuRegister.R14] = 0x5555;
        context[CpuRegister.R15] = 0x6666;
        context.FpuControlWord = 0x037F;
        context.Mxcsr = 0x1F80;

        Assert.Equal(0, LibcInternalExports.SetJmpInitialReturnCompat(context));
        Assert.Equal(0UL, context[CpuRegister.Rax]);

        Span<byte> saved = stackalloc byte[0x58];
        Assert.True(memory.TryRead(JumpBufferAddress, saved));
        Assert.Equal(returnRip, BinaryPrimitives.ReadUInt64LittleEndian(saved[0x00..]));
        Assert.Equal(0x1111UL, BinaryPrimitives.ReadUInt64LittleEndian(saved[0x08..]));
        Assert.Equal(StackPointer + sizeof(ulong), BinaryPrimitives.ReadUInt64LittleEndian(saved[0x10..]));
        Assert.Equal(0x2222UL, BinaryPrimitives.ReadUInt64LittleEndian(saved[0x18..]));
        Assert.Equal(0x3333UL, BinaryPrimitives.ReadUInt64LittleEndian(saved[0x20..]));
        Assert.Equal(0x4444UL, BinaryPrimitives.ReadUInt64LittleEndian(saved[0x28..]));
        Assert.Equal(0x5555UL, BinaryPrimitives.ReadUInt64LittleEndian(saved[0x30..]));
        Assert.Equal(0x6666UL, BinaryPrimitives.ReadUInt64LittleEndian(saved[0x38..]));
        Assert.Equal((ushort)0x037F, BinaryPrimitives.ReadUInt16LittleEndian(saved[0x40..]));
        Assert.Equal(0x1F80U, BinaryPrimitives.ReadUInt32LittleEndian(saved[0x44..]));
        Assert.All(saved[0x48..].ToArray(), value => Assert.Equal(0, value));
    }

    [Fact]
    public void SetJmp_UnreadableReturnSlotFailsWithoutMutatingBuffer()
    {
        var memory = new FakeCpuMemory(Base, 0x1000);
        var context = new CpuContext(memory, Generation.Gen5);
        var original = Enumerable.Repeat((byte)0xA5, 0x58).ToArray();
        Assert.True(memory.TryWrite(JumpBufferAddress, original));
        context[CpuRegister.Rdi] = JumpBufferAddress;
        context[CpuRegister.Rsp] = Base + 0x2000;

        Assert.Equal(
            (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT,
            LibcInternalExports.SetJmpInitialReturnCompat(context));

        var actual = new byte[0x58];
        Assert.True(memory.TryRead(JumpBufferAddress, actual));
        Assert.Equal(original, actual);
    }

    [Fact]
    public void SetJmp_TruncatedOutputFailsWithoutPartialWrite()
    {
        const ulong truncatedBufferAddress = Base + 0xFC0;
        var memory = new FakeCpuMemory(Base, 0x1000);
        var context = new CpuContext(memory, Generation.Gen5);
        var original = Enumerable.Repeat((byte)0xA5, 0x40).ToArray();
        Assert.True(memory.TryWrite(truncatedBufferAddress, original));
        Assert.True(context.TryWriteUInt64(StackPointer, 0x8_0012_3456));
        context[CpuRegister.Rdi] = truncatedBufferAddress;
        context[CpuRegister.Rsp] = StackPointer;

        Assert.Equal(
            (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT,
            LibcInternalExports.SetJmpInitialReturnCompat(context));

        var actual = new byte[0x40];
        Assert.True(memory.TryRead(truncatedBufferAddress, actual));
        Assert.Equal(original, actual);
    }

    [Fact]
    public void HeapGetTraceInfo_NullPointer_ReturnsInvalidArgument()
    {
        var memory = new FakeCpuMemory(Base, 0x1000);
        var context = new CpuContext(memory, Generation.Gen5);

        context[CpuRegister.Rdi] = 0;

        var result = LibcInternalExports.LibcHeapGetTraceInfo(context);

        Assert.Equal(
            (int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT,
            result);
        Assert.Equal(0UL, context[CpuRegister.Rax]);
    }

    [Fact]
    public void HeapGetTraceInfo_WrongSize_ReturnsInvalidArgument()
    {
        var memory = new FakeCpuMemory(Base, 0x1000);
        var context = new CpuContext(memory, Generation.Gen5);

        Span<byte> sizeBytes = stackalloc byte[sizeof(ulong)];
        BinaryPrimitives.WriteUInt64LittleEndian(
            sizeBytes,
            ExpectedInfoSize - 1);

        Assert.True(memory.TryWrite(InfoAddress, sizeBytes));

        context[CpuRegister.Rdi] = InfoAddress;

        var result = LibcInternalExports.LibcHeapGetTraceInfo(context);

        Assert.Equal(
            (int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT,
            result);
        Assert.Equal(0UL, context[CpuRegister.Rax]);
    }

    [Fact]
    public void HeapGetTraceInfo_ValidBuffer_WritesStablePointers()
    {
        var memory = new FakeCpuMemory(Base, 0x1000);
        var context = new CpuContext(memory, Generation.Gen5);

        Span<byte> sizeBytes = stackalloc byte[sizeof(ulong)];
        BinaryPrimitives.WriteUInt64LittleEndian(
            sizeBytes,
            ExpectedInfoSize);

        Assert.True(memory.TryWrite(InfoAddress, sizeBytes));

        context[CpuRegister.Rdi] = InfoAddress;

        var firstResult =
            LibcInternalExports.LibcHeapGetTraceInfo(context);

        Assert.Equal(
            (int)OrbisGen2Result.ORBIS_GEN2_OK,
            firstResult);
        Assert.Equal(0UL, context[CpuRegister.Rax]);

        Assert.True(
            context.TryReadUInt64(
                InfoAddress + 16,
                out var firstMaskAddress));

        Assert.True(
            context.TryReadUInt64(
                InfoAddress + 24,
                out var firstTableAddress));

        Assert.NotEqual(0UL, firstMaskAddress);
        Assert.Equal(firstMaskAddress + 8UL, firstTableAddress);

        var secondResult =
            LibcInternalExports.LibcHeapGetTraceInfo(context);

        Assert.Equal(
            (int)OrbisGen2Result.ORBIS_GEN2_OK,
            secondResult);

        Assert.True(
            context.TryReadUInt64(
                InfoAddress + 16,
                out var secondMaskAddress));

        Assert.True(
            context.TryReadUInt64(
                InfoAddress + 24,
                out var secondTableAddress));

        Assert.Equal(firstMaskAddress, secondMaskAddress);
        Assert.Equal(firstTableAddress, secondTableAddress);
    }

    [Fact]
    public void HeapGetTraceInfo_TruncatedOutput_ReturnsMemoryFault()
    {
        var memory = new FakeCpuMemory(Base, 31);
        var context = new CpuContext(memory, Generation.Gen5);

        Span<byte> sizeBytes = stackalloc byte[sizeof(ulong)];
        BinaryPrimitives.WriteUInt64LittleEndian(
            sizeBytes,
            ExpectedInfoSize);

        Assert.True(memory.TryWrite(Base, sizeBytes));

        context[CpuRegister.Rdi] = Base;

        var result = LibcInternalExports.LibcHeapGetTraceInfo(context);

        Assert.Equal(
            (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT,
            result);
        Assert.Equal(0UL, context[CpuRegister.Rax]);
    }
}
