// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using SharpEmu.HLE;
using SharpEmu.Libs.Agc;
using Xunit;

namespace SharpEmu.Libs.Tests;

public sealed class AgcShaderFusionTests
{
    private const ulong SizeOutputAddress = 0x1000;
    private const ulong DestinationAddress = 0x2000;
    private const ulong GeometryHalfAddress = 0x3000;
    private const ulong ExportHalfAddress = 0x4000;
    private const ulong WorkspaceAddress = 0x5000;
    private const ulong GeometryCxAddress = 0x6000;
    private const ulong GeometryShAddress = 0x6100;
    private const ulong ExportCxAddress = 0x6200;
    private const ulong ExportShAddress = 0x6300;
    private const ulong GeometryInputSemanticsAddress = 0x6400;
    private const ulong ExportOutputSemanticsAddress = 0x6500;
    private const int ShaderHeaderSize = 0x60;
    private const uint ShaderFileHeader = 0x34333231;
    private const uint ShaderVersion = 0x18;

    [Fact]
    public void GetFusedShaderSizeReportsAlignedCombinedRegisterStorage()
    {
        var fixture = CreateFixture();
        fixture.Context[CpuRegister.Rdi] = SizeOutputAddress;
        fixture.Context[CpuRegister.Rsi] = GeometryHalfAddress;
        fixture.Context[CpuRegister.Rdx] = ExportHalfAddress;

        Assert.Equal(0, AgcExports.GetFusedShaderSize(fixture.Context));
        Assert.True(fixture.Context.TryReadUInt64(SizeOutputAddress, out var size));
        Assert.True(fixture.Context.TryReadUInt64(SizeOutputAddress + 8, out var alignment));
        Assert.Equal(0x40UL, size);
        Assert.Equal(0x20UL, alignment);
    }

    [Fact]
    public void FuseShaderHalvesBuildsTypeTwoHeaderAndConcatenatesRegisterTables()
    {
        var fixture = CreateFixture();
        fixture.Context[CpuRegister.Rdi] = DestinationAddress;
        fixture.Context[CpuRegister.Rsi] = GeometryHalfAddress;
        fixture.Context[CpuRegister.Rdx] = ExportHalfAddress;
        fixture.Context[CpuRegister.Rcx] = WorkspaceAddress;

        Assert.Equal(0, AgcExports.FuseShaderHalves(fixture.Context));

        var header = Read(fixture.Memory, DestinationAddress, ShaderHeaderSize);
        Assert.Equal(ShaderFileHeader, BinaryPrimitives.ReadUInt32LittleEndian(header));
        Assert.Equal(ShaderVersion, BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(4)));
        Assert.Equal(WorkspaceAddress, BinaryPrimitives.ReadUInt64LittleEndian(header.AsSpan(0x18)));
        Assert.Equal(WorkspaceAddress + 0x18, BinaryPrimitives.ReadUInt64LittleEndian(header.AsSpan(0x20)));
        Assert.Equal(GeometryInputSemanticsAddress, BinaryPrimitives.ReadUInt64LittleEndian(header.AsSpan(0x30)));
        Assert.Equal(ExportOutputSemanticsAddress, BinaryPrimitives.ReadUInt64LittleEndian(header.AsSpan(0x38)));
        Assert.Equal(11, BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(0x54)));
        Assert.Equal(2, header[0x5A]);
        Assert.Equal(3, header[0x5B]);
        Assert.Equal(3, header[0x5C]);

        Assert.Equal(
            new ulong[] { 0xA1, 0xB1, 0xB2, 0xC1, 0xC2, 0xD1 },
            ReadUInt64s(fixture.Memory, WorkspaceAddress, 6));
    }

    [Fact]
    public void FuseShaderHalvesRejectsIncompatibleTypesWithoutMutatingDestination()
    {
        var fixture = CreateFixture(exportType: 1);
        fixture.Context[CpuRegister.Rdi] = DestinationAddress;
        fixture.Context[CpuRegister.Rsi] = GeometryHalfAddress;
        fixture.Context[CpuRegister.Rdx] = ExportHalfAddress;
        fixture.Context[CpuRegister.Rcx] = WorkspaceAddress;
        var before = Read(fixture.Memory, DestinationAddress, ShaderHeaderSize);

        Assert.Equal(unchecked((int)0x8A6C0008), AgcExports.FuseShaderHalves(fixture.Context));
        Assert.Equal(before, Read(fixture.Memory, DestinationAddress, ShaderHeaderSize));
    }

    [Fact]
    public void FuseShaderHalvesDoesNotCommitHeaderWhenARegisterTableCannotBeRead()
    {
        var fixture = CreateFixture(includeExportShRegisters: false);
        fixture.Context[CpuRegister.Rdi] = DestinationAddress;
        fixture.Context[CpuRegister.Rsi] = GeometryHalfAddress;
        fixture.Context[CpuRegister.Rdx] = ExportHalfAddress;
        fixture.Context[CpuRegister.Rcx] = WorkspaceAddress;
        var before = Read(fixture.Memory, DestinationAddress, ShaderHeaderSize);

        Assert.Equal(
            (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT,
            AgcExports.FuseShaderHalves(fixture.Context));
        Assert.Equal(before, Read(fixture.Memory, DestinationAddress, ShaderHeaderSize));
    }

    private static (FakeGuestMemory Memory, CpuContext Context) CreateFixture(
        byte exportType = 6,
        bool includeExportShRegisters = true)
    {
        var memory = new FakeGuestMemory();
        memory.AddRegion(SizeOutputAddress, new byte[16]);
        memory.AddRegion(DestinationAddress, Enumerable.Repeat((byte)0xCC, ShaderHeaderSize).ToArray());
        memory.AddRegion(GeometryHalfAddress, CreateHeader(
            type: 4,
            cxAddress: GeometryCxAddress,
            cxCount: 1,
            shAddress: GeometryShAddress,
            shCount: 2,
            inputSemanticsAddress: GeometryInputSemanticsAddress,
            outputSemanticsAddress: 0,
            scratchUsage: 7));
        memory.AddRegion(ExportHalfAddress, CreateHeader(
            type: exportType,
            cxAddress: ExportCxAddress,
            cxCount: 2,
            shAddress: ExportShAddress,
            shCount: 1,
            inputSemanticsAddress: 0,
            outputSemanticsAddress: ExportOutputSemanticsAddress,
            scratchUsage: 11));
        memory.AddRegion(WorkspaceAddress, new byte[0x40]);
        memory.AddRegion(GeometryCxAddress, ToBytes(0xA1));
        memory.AddRegion(GeometryShAddress, ToBytes(0xC1, 0xC2));
        memory.AddRegion(ExportCxAddress, ToBytes(0xB1, 0xB2));
        if (includeExportShRegisters)
        {
            memory.AddRegion(ExportShAddress, ToBytes(0xD1));
        }

        return (memory, new CpuContext(memory, Generation.Gen5));
    }

    private static byte[] CreateHeader(
        byte type,
        ulong cxAddress,
        byte cxCount,
        ulong shAddress,
        byte shCount,
        ulong inputSemanticsAddress,
        ulong outputSemanticsAddress,
        ushort scratchUsage)
    {
        var header = new byte[ShaderHeaderSize];
        BinaryPrimitives.WriteUInt32LittleEndian(header, ShaderFileHeader);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(4), ShaderVersion);
        BinaryPrimitives.WriteUInt64LittleEndian(header.AsSpan(0x18), cxAddress);
        BinaryPrimitives.WriteUInt64LittleEndian(header.AsSpan(0x20), shAddress);
        BinaryPrimitives.WriteUInt64LittleEndian(header.AsSpan(0x30), inputSemanticsAddress);
        BinaryPrimitives.WriteUInt64LittleEndian(header.AsSpan(0x38), outputSemanticsAddress);
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(0x54), scratchUsage);
        header[0x5A] = type;
        header[0x5B] = cxCount;
        header[0x5C] = shCount;
        return header;
    }

    private static byte[] ToBytes(params ulong[] values)
    {
        var bytes = new byte[values.Length * sizeof(ulong)];
        for (var index = 0; index < values.Length; index++)
        {
            BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(index * sizeof(ulong)), values[index]);
        }

        return bytes;
    }

    private static byte[] Read(FakeGuestMemory memory, ulong address, int length)
    {
        var bytes = new byte[length];
        Assert.True(memory.TryRead(address, bytes));
        return bytes;
    }

    private static ulong[] ReadUInt64s(FakeGuestMemory memory, ulong address, int count)
    {
        var bytes = Read(memory, address, count * sizeof(ulong));
        var values = new ulong[count];
        for (var index = 0; index < count; index++)
        {
            values[index] = BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(index * sizeof(ulong)));
        }

        return values;
    }
}
