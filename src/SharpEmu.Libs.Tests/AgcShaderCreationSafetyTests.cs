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
    private const int ShaderUserDataOffset = 0x08;
    private const int ShaderCxRegistersOffset = 0x18;
    private const int ShaderShRegistersOffset = 0x20;
    private const int ShaderOutputSemanticsOffset = 0x38;
    private const int ShaderCodeOffset = 0x10;
    private const uint ShaderFileHeader = 0x34333231;
    private const uint ShaderVersion = 0x18;
    private const uint ComputePgmLo = 0x20C;
    private const uint ComputePgmHi = 0x20D;
    private const uint SpiShaderPgmLoVs = 0x48;
    private const uint SpiShaderPgmHiVs = 0x49;
    private const uint SpiShaderPgmLoHs = 0x108;
    private const uint SpiShaderPgmHiHs = 0x109;
    private const uint SpiShaderPgmRsrc1Hs = 0x10A;
    private const uint SpiShaderPgmRsrc2Hs = 0x10B;

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

    [Fact]
    public void CreateShaderRejectsHeaderThatWrapsGuestAddressSpace()
    {
        const ulong wrappedHeaderAddress = ulong.MaxValue - 0x1F;
        var header = CreateHeader(wrappedHeaderAddress);
        var memory = new FakeGuestMemory();
        memory.AddRegion(wrappedHeaderAddress, header[..0x20]);
        memory.AddRegion(0, header[0x20..]);
        memory.AddRegion(ShaderRegistersAddress, CreateRegisters());
        var context = CreateContext(memory, wrappedHeaderAddress);

        Assert.Equal(
            (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT,
            AgcExports.CreateShader(context));
    }

    [Fact]
    public void CreateShaderRejectsUserDataBlockThatWrapsGuestAddressSpace()
    {
        const ulong wrappedUserDataAddress = ulong.MaxValue - 0x0F;
        var (memory, context) = CreateFixture();
        var userDataFieldAddress = ShaderHeaderAddress + ShaderUserDataOffset;
        Assert.True(context.TryWriteUInt64(
            userDataFieldAddress,
            wrappedUserDataAddress - userDataFieldAddress));
        memory.AddRegion(wrappedUserDataAddress, new byte[0x10]);
        memory.AddRegion(0, new byte[0x18]);

        Assert.Equal(
            (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT,
            AgcExports.CreateShader(context));
    }

    [Fact]
    public void CreateShaderRejectsProgramRegistersThatWrapGuestAddressSpace()
    {
        const ulong wrappedRegistersAddress = ulong.MaxValue - 7;
        var (memory, context) = CreateFixture();
        var shFieldAddress = ShaderHeaderAddress + ShaderShRegistersOffset;
        Assert.True(context.TryWriteUInt64(
            shFieldAddress,
            wrappedRegistersAddress - shFieldAddress));
        var registers = CreateRegisters();
        memory.AddRegion(wrappedRegistersAddress, registers[..sizeof(ulong)]);
        memory.AddRegion(0, registers[sizeof(ulong)..]);

        Assert.Equal(
            (int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT,
            AgcExports.CreateShader(context));
    }

    [Fact]
    public void CreateShaderFindsHullProgramRegistersAfterResourceEntries()
    {
        var registers = CreateRegisterTable(
            (SpiShaderPgmRsrc1Hs, 0x1111_1111),
            (SpiShaderPgmRsrc2Hs, 0x2222_2222),
            (SpiShaderPgmLoHs, 0),
            (SpiShaderPgmHiHs, 0));
        var (_, context) = CreateFixture(
            shaderType: 5,
            registers: registers);
        context[CpuRegister.Rdi] = DestinationAddress;

        Assert.Equal(0, AgcExports.CreateShader(context));
        Assert.True(context.TryReadUInt32(ShaderRegistersAddress + 4, out var resource1));
        Assert.Equal(0x1111_1111U, resource1);
        Assert.True(context.TryReadUInt32(ShaderRegistersAddress + 12, out var resource2));
        Assert.Equal(0x2222_2222U, resource2);
        Assert.True(context.TryReadUInt32(ShaderRegistersAddress + 20, out var programLo));
        Assert.Equal((uint)((ShaderCodeAddress >> 8) & uint.MaxValue), programLo);
        Assert.True(context.TryReadUInt32(ShaderRegistersAddress + 28, out var programHi));
        Assert.Equal((uint)((ShaderCodeAddress >> 40) & 0xFF), programHi);
    }

    [Fact]
    public void CreateShaderPatchesVertexProgramRegisters()
    {
        var registers = CreateRegisterTable(
            (SpiShaderPgmLoVs, 0),
            (SpiShaderPgmHiVs, 0));
        var (_, context) = CreateFixture(
            shaderType: 3,
            registers: registers);

        Assert.Equal(0, AgcExports.CreateShader(context));
        Assert.True(context.TryReadUInt32(ShaderRegistersAddress + 4, out var programLo));
        Assert.Equal((uint)((ShaderCodeAddress >> 8) & uint.MaxValue), programLo);
        Assert.True(context.TryReadUInt32(ShaderRegistersAddress + 12, out var programHi));
        Assert.Equal((uint)((ShaderCodeAddress >> 40) & 0xFF), programHi);
    }

    [Fact]
    public void CreateShaderAcceptsHullResourceTableWithoutProgramRegisters()
    {
        var registers = CreateRegisterTable(
            (SpiShaderPgmRsrc1Hs, 0x1111_1111),
            (SpiShaderPgmRsrc2Hs, 0x2222_2222));
        var (_, context) = CreateFixture(
            shaderType: 5,
            registers: registers);
        context[CpuRegister.Rdi] = DestinationAddress;

        Assert.Equal(0, AgcExports.CreateShader(context));
        Assert.True(context.TryReadUInt64(DestinationAddress, out var shaderAddress));
        Assert.Equal(ShaderHeaderAddress, shaderAddress);
        Assert.True(context.TryReadUInt64(
            ShaderHeaderAddress + ShaderCodeOffset,
            out var codeAddress));
        Assert.Equal(ShaderCodeAddress, codeAddress);
        Assert.True(context.TryReadUInt32(ShaderRegistersAddress + 4, out var resource1));
        Assert.Equal(0x1111_1111U, resource1);
        Assert.True(context.TryReadUInt32(ShaderRegistersAddress + 12, out var resource2));
        Assert.Equal(0x2222_2222U, resource2);
    }

    [Fact]
    public void CreateShaderRejectsTruncatedDeclaredRegisterTable()
    {
        var registers = CreateRegisters();
        var (_, context) = CreateFixture(
            registers: registers,
            registerCount: 3);

        Assert.Equal(
            (int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT,
            AgcExports.CreateShader(context));
        Assert.True(context.TryReadUInt32(ShaderRegistersAddress + 4, out var programLo));
        Assert.Equal(0U, programLo);
        Assert.True(context.TryReadUInt32(ShaderRegistersAddress + 12, out var programHi));
        Assert.Equal(0U, programHi);
    }

    private static (FakeGuestMemory Memory, CpuContext Context) CreateFixture(
        ulong headerAddress = ShaderHeaderAddress,
        ulong cxRegistersRelativePointer = 0,
        ulong outputSemanticsRelativePointer = 0,
        byte shaderType = 0,
        byte[]? registers = null,
        byte? registerCount = null)
    {
        registers ??= CreateRegisters();
        var header = CreateHeader(
            headerAddress,
            cxRegistersRelativePointer,
            outputSemanticsRelativePointer,
            shaderType,
            registerCount ?? checked((byte)(registers.Length / sizeof(ulong))));
        var memory = new FakeGuestMemory();
        memory.AddRegion(headerAddress, header);
        memory.AddRegion(ShaderRegistersAddress, registers);
        memory.AddRegion(DestinationAddress, new byte[sizeof(ulong)]);
        return (memory, CreateContext(memory, headerAddress));
    }

    private static byte[] CreateHeader(
        ulong headerAddress,
        ulong cxRegistersRelativePointer = 0,
        ulong outputSemanticsRelativePointer = 0,
        byte shaderType = 0,
        byte registerCount = 2)
    {
        var header = new byte[ShaderHeaderSize];
        BinaryPrimitives.WriteUInt32LittleEndian(header, ShaderFileHeader);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(sizeof(uint)), ShaderVersion);
        BinaryPrimitives.WriteUInt64LittleEndian(
            header.AsSpan(ShaderCxRegistersOffset),
            cxRegistersRelativePointer);
        BinaryPrimitives.WriteUInt64LittleEndian(
            header.AsSpan(ShaderShRegistersOffset),
            unchecked(
                ShaderRegistersAddress -
                unchecked(headerAddress + ShaderShRegistersOffset)));
        BinaryPrimitives.WriteUInt64LittleEndian(
            header.AsSpan(ShaderOutputSemanticsOffset),
            outputSemanticsRelativePointer);
        header[0x5A] = shaderType;
        header[0x5C] = registerCount;
        return header;
    }

    private static byte[] CreateRegisters() =>
        CreateRegisterTable(
            (ComputePgmLo, 0),
            (ComputePgmHi, 0));

    private static byte[] CreateRegisterTable(
        params (uint Offset, uint Value)[] entries)
    {
        var registers = new byte[entries.Length * sizeof(ulong)];
        for (var index = 0; index < entries.Length; index++)
        {
            var entry = registers.AsSpan(index * sizeof(ulong), sizeof(ulong));
            BinaryPrimitives.WriteUInt32LittleEndian(entry, entries[index].Offset);
            BinaryPrimitives.WriteUInt32LittleEndian(entry[sizeof(uint)..], entries[index].Value);
        }

        return registers;
    }

    private static CpuContext CreateContext(FakeGuestMemory memory, ulong headerAddress)
    {
        var context = new CpuContext(memory, Generation.Gen5);
        context[CpuRegister.Rsi] = headerAddress;
        context[CpuRegister.Rdx] = ShaderCodeAddress;
        return context;
    }
}
