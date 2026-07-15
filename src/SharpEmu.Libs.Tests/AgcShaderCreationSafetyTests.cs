// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using SharpEmu.HLE;
using SharpEmu.Libs.Agc;
using Xunit;

namespace SharpEmu.Libs.Tests;

public sealed class AgcShaderCreationSafetyTests
{
    private const ulong ShaderHeaderAddress = 0x1000;
    private const ulong ShaderRegistersAddress = 0x2000;
    private const ulong DestinationAddress = 0x3000;
    private const ulong ShaderCodeAddress = 0x0000_1234_5678_9A00;
    private const int ShaderHeaderSize = 0x60;
    private const int ShaderCxRegistersOffset = 0x18;
    private const int ShaderShRegistersOffset = 0x20;
    private const int ShaderOutputSemanticsOffset = 0x38;
    private const int ShaderCodeOffset = 0x10;
    private const uint ShaderFileHeader = 0x34333231;
    private const uint ShaderVersion = 0x18;
    private const uint ComputePgmLo = 0x20C;
    private const uint ComputePgmHi = 0x20D;

    [Fact]
    public void CreateShaderRelocatesPointersAndPatchesProgramRegisters()
    {
        var (memory, context) = CreateFixture();
        context[CpuRegister.Rdi] = DestinationAddress;

        Assert.Equal(0, AgcExports.CreateShader(context));
        Assert.True(context.TryReadUInt64(DestinationAddress, out var shaderAddress));
        Assert.Equal(ShaderHeaderAddress, shaderAddress);
        Assert.True(context.TryReadUInt64(ShaderHeaderAddress + ShaderCodeOffset, out var codeAddress));
        Assert.Equal(ShaderCodeAddress, codeAddress);
        Assert.True(context.TryReadUInt64(
            ShaderHeaderAddress + ShaderShRegistersOffset,
            out var registersAddress));
        Assert.Equal(ShaderRegistersAddress, registersAddress);
        Assert.True(context.TryReadUInt32(ShaderRegistersAddress + 4, out var programLo));
        Assert.Equal((uint)((ShaderCodeAddress >> 8) & uint.MaxValue), programLo);
        Assert.True(context.TryReadUInt32(ShaderRegistersAddress + 12, out var programHi));
        Assert.Equal((uint)((ShaderCodeAddress >> 40) & 0xFF), programHi);
        Assert.True(memory.ReadCount > 0);
    }

    [Fact]
    public void CreateShaderRejectsWrappedRelativePointerWithoutMutatingIt()
    {
        const ulong finalHeaderAddress = ulong.MaxValue - (ShaderHeaderSize - 1);
        const ulong wrappedRelativePointer = 0x100;
        var cxFieldAddress = finalHeaderAddress + ShaderCxRegistersOffset;
        var (_, context) = CreateFixture(finalHeaderAddress, wrappedRelativePointer);

        Assert.Equal(
            (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT,
            AgcExports.CreateShader(context));
        Assert.True(context.TryReadUInt64(cxFieldAddress, out var storedPointer));
        Assert.Equal(wrappedRelativePointer, storedPointer);
    }

    [Fact]
    public void CreateShaderDoesNotCommitEarlierRelocationsWhenLaterPointerWraps()
    {
        var outputFieldAddress = ShaderHeaderAddress + ShaderOutputSemanticsOffset;
        var wrappedRelativePointer = ulong.MaxValue - outputFieldAddress + 2;
        var shFieldAddress = ShaderHeaderAddress + ShaderShRegistersOffset;
        var originalShRelativePointer = unchecked(ShaderRegistersAddress - shFieldAddress);
        var (_, context) = CreateFixture(
            outputSemanticsRelativePointer: wrappedRelativePointer);

        Assert.Equal(
            (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT,
            AgcExports.CreateShader(context));
        Assert.True(context.TryReadUInt64(shFieldAddress, out var storedShPointer));
        Assert.Equal(originalShRelativePointer, storedShPointer);
        Assert.True(context.TryReadUInt64(
            outputFieldAddress,
            out var storedOutputPointer));
        Assert.Equal(wrappedRelativePointer, storedOutputPointer);
    }

    private static (FakeGuestMemory Memory, CpuContext Context) CreateFixture(
        ulong headerAddress = ShaderHeaderAddress,
        ulong cxRegistersRelativePointer = 0,
        ulong outputSemanticsRelativePointer = 0)
    {
        var header = new byte[ShaderHeaderSize];
        BinaryPrimitives.WriteUInt32LittleEndian(header, ShaderFileHeader);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(sizeof(uint)), ShaderVersion);
        BinaryPrimitives.WriteUInt64LittleEndian(
            header.AsSpan(ShaderCxRegistersOffset),
            cxRegistersRelativePointer);
        BinaryPrimitives.WriteUInt64LittleEndian(
            header.AsSpan(ShaderShRegistersOffset),
            unchecked(ShaderRegistersAddress - (headerAddress + ShaderShRegistersOffset)));
        BinaryPrimitives.WriteUInt64LittleEndian(
            header.AsSpan(ShaderOutputSemanticsOffset),
            outputSemanticsRelativePointer);
        header[0x5A] = 0;
        header[0x5C] = 2;

        var registers = new byte[2 * sizeof(ulong)];
        BinaryPrimitives.WriteUInt32LittleEndian(registers, ComputePgmLo);
        BinaryPrimitives.WriteUInt32LittleEndian(registers.AsSpan(sizeof(ulong)), ComputePgmHi);

        var memory = new FakeGuestMemory();
        memory.AddRegion(headerAddress, header);
        memory.AddRegion(ShaderRegistersAddress, registers);
        memory.AddRegion(DestinationAddress, new byte[sizeof(ulong)]);
        var context = new CpuContext(memory, Generation.Gen5);
        context[CpuRegister.Rsi] = headerAddress;
        context[CpuRegister.Rdx] = ShaderCodeAddress;
        return (memory, context);
    }
}
