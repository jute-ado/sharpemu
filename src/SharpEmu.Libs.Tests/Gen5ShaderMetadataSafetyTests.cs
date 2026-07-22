// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using SharpEmu.Libs.Agc;
using SharpEmu.ShaderCompiler;
using Xunit;

namespace SharpEmu.Libs.Tests;

public sealed class Gen5ShaderMetadataSafetyTests
{
    private const ulong ShaderHeaderAddress = 0x1000;
    private const ulong UserDataAddress = 0x2000;
    private const ulong DirectResourcesAddress = 0x3000;
    private const ulong TextureResourcesAddress = 0x4000;
    private const ulong ReadWriteResourcesAddress = 0x5000;
    private const ulong SamplerResourcesAddress = 0x6000;
    private const ulong ShaderCodeAddress = 0x7000;
    private const int ShaderHeaderSize = 0x10;
    private const int UserDataSize = 0x36;

    [Fact]
    public void TryReadDecodesCompleteMetadata()
    {
        var (memory, context) = CreateMetadataMemory();
        memory.AddRegion(DirectResourcesAddress, new byte[3 * sizeof(ushort)]);
        memory.AddRegion(TextureResourcesAddress, new byte[2 * sizeof(ushort)]);
        memory.AddRegion(ReadWriteResourcesAddress, new byte[sizeof(ushort)]);
        memory.AddRegion(SamplerResourcesAddress, new byte[sizeof(ushort)]);

        WriteUInt64(context, UserDataAddress, DirectResourcesAddress);
        WriteUInt64(context, UserDataAddress + 0x08, TextureResourcesAddress);
        WriteUInt64(context, UserDataAddress + 0x10, ReadWriteResourcesAddress);
        WriteUInt64(context, UserDataAddress + 0x18, SamplerResourcesAddress);
        WriteUInt16(context, UserDataAddress + 0x28, 12);
        WriteUInt16(context, UserDataAddress + 0x2A, 9);
        WriteUInt16(context, UserDataAddress + 0x2C, 3);
        WriteUInt16(context, UserDataAddress + 0x2E, 2);
        WriteUInt16(context, UserDataAddress + 0x30, 1);
        WriteUInt16(context, UserDataAddress + 0x32, 1);

        WriteUInt16(context, DirectResourcesAddress, 5);
        WriteUInt16(context, DirectResourcesAddress + 2, ushort.MaxValue);
        WriteUInt16(context, DirectResourcesAddress + 4, 8);
        WriteUInt16(context, TextureResourcesAddress, 3);
        WriteUInt16(context, TextureResourcesAddress + 2, 0x8004);
        WriteUInt16(context, ReadWriteResourcesAddress, 0x7FFF);
        WriteUInt16(context, SamplerResourcesAddress, 0x8006);

        Assert.True(Gen5ShaderMetadataReader.TryRead(context, ShaderHeaderAddress, out var metadata));
        Assert.Equal(12u, metadata.ExtendedUserDataSizeDwords);
        Assert.Equal(9u, metadata.ShaderResourceTableSizeDwords);
        Assert.Equal(2, metadata.DirectResources.Count);
        Assert.Equal(5u, metadata.DirectResources[0]);
        Assert.Equal(8u, metadata.DirectResources[2]);
        Assert.Collection(
            metadata.Resources,
            resource => Assert.Equal(
                new Gen5ShaderResourceMapping(Gen5ShaderResourceKind.ReadOnlyTexture, 0, 3, false),
                resource),
            resource => Assert.Equal(
                new Gen5ShaderResourceMapping(Gen5ShaderResourceKind.ReadOnlyTexture, 1, 4, true),
                resource),
            resource => Assert.Equal(
                new Gen5ShaderResourceMapping(Gen5ShaderResourceKind.Sampler, 0, 6, true),
                resource));
    }

    [Fact]
    public void TryCreateStateIncludesBothDwordsOfDirectPointerResources()
    {
        const uint userDataRegister = 0x40;
        const int directResourceCount = 11;
        const int vertexBufferPointerType = 8;
        const int vertexAttributePointerType = 10;
        var (memory, context) = CreateMetadataMemory();
        memory.AddRegion(ShaderCodeAddress, BitConverter.GetBytes(0xBF81_0000u));
        memory.AddRegion(
            DirectResourcesAddress,
            new byte[directResourceCount * sizeof(ushort)]);
        WriteUInt64(context, UserDataAddress, DirectResourcesAddress);
        WriteUInt16(context, UserDataAddress + 0x2C, directResourceCount);
        for (var type = 0; type < directResourceCount; type++)
        {
            WriteUInt16(
                context,
                DirectResourcesAddress + (ulong)(type * sizeof(ushort)),
                ushort.MaxValue);
        }

        WriteUInt16(
            context,
            DirectResourcesAddress + vertexBufferPointerType * sizeof(ushort),
            16);
        WriteUInt16(
            context,
            DirectResourcesAddress + vertexAttributePointerType * sizeof(ushort),
            18);
        var registers = new Dictionary<uint, uint>
        {
            [userDataRegister + 16] = 0x1234_5000u,
            [userDataRegister + 17] = 0x0000_0006u,
            [userDataRegister + 18] = 0x5678_9000u,
            [userDataRegister + 19] = 0x0000_0006u,
        };

        Assert.True(
            Gen5ShaderTranslator.TryCreateState(
                context,
                ShaderCodeAddress,
                ShaderHeaderAddress,
                registers,
                userDataRegister,
                out var state,
                out var error),
            error);
        Assert.Equal(20, state.UserData.Count);
        Assert.Equal(0x0000_0006u, state.UserData[17]);
        Assert.Equal(0x0000_0006u, state.UserData[19]);
    }

    [Fact]
    public void TryReadRejectsWrappedShaderHeader()
    {
        const ulong wrappedHeaderAddress = ulong.MaxValue - 3;
        var memory = new FakeGuestMemory();
        var context = new CpuContext(memory, Generation.Gen5);
        memory.AddRegion(0, new byte[12]);
        memory.AddRegion(UserDataAddress, new byte[UserDataSize]);
        WriteUInt64(context, 4, UserDataAddress);

        Assert.False(Gen5ShaderMetadataReader.TryRead(context, wrappedHeaderAddress, out _));
    }

    [Fact]
    public void TryReadRejectsWrappedUserDataStructure()
    {
        const ulong wrappedUserDataAddress = ulong.MaxValue - 0x0F;
        var memory = new FakeGuestMemory();
        var context = new CpuContext(memory, Generation.Gen5);
        memory.AddRegion(ShaderHeaderAddress, new byte[ShaderHeaderSize]);
        memory.AddRegion(wrappedUserDataAddress, new byte[0x10]);
        memory.AddRegion(0, new byte[0x26]);
        WriteUInt64(context, ShaderHeaderAddress + 0x08, wrappedUserDataAddress);

        Assert.False(Gen5ShaderMetadataReader.TryRead(context, ShaderHeaderAddress, out _));
    }

    [Fact]
    public void TryReadRejectsWrappedDirectResourceArray()
    {
        const ulong wrappedResourcesAddress = ulong.MaxValue - 1;
        var (memory, context) = CreateMetadataMemory();
        memory.AddRegion(wrappedResourcesAddress, new byte[sizeof(ushort)]);
        memory.AddRegion(0, new byte[sizeof(ushort)]);
        WriteUInt64(context, UserDataAddress, wrappedResourcesAddress);
        WriteUInt16(context, UserDataAddress + 0x2C, 2);
        WriteUInt16(context, wrappedResourcesAddress, 3);
        WriteUInt16(context, 0, 5);

        Assert.False(Gen5ShaderMetadataReader.TryRead(context, ShaderHeaderAddress, out _));
    }

    [Fact]
    public void TryReadRejectsWrappedClassResourceArray()
    {
        const ulong wrappedResourcesAddress = ulong.MaxValue - 1;
        var (memory, context) = CreateMetadataMemory();
        memory.AddRegion(wrappedResourcesAddress, new byte[sizeof(ushort)]);
        memory.AddRegion(0, new byte[sizeof(ushort)]);
        WriteUInt64(context, UserDataAddress + 0x08, wrappedResourcesAddress);
        WriteUInt16(context, UserDataAddress + 0x2E, 2);
        WriteUInt16(context, wrappedResourcesAddress, 3);
        WriteUInt16(context, 0, 5);

        Assert.False(Gen5ShaderMetadataReader.TryRead(context, ShaderHeaderAddress, out _));
    }

    private static (FakeGuestMemory Memory, CpuContext Context) CreateMetadataMemory()
    {
        var memory = new FakeGuestMemory();
        var context = new CpuContext(memory, Generation.Gen5);
        memory.AddRegion(ShaderHeaderAddress, new byte[ShaderHeaderSize]);
        memory.AddRegion(UserDataAddress, new byte[UserDataSize]);
        WriteUInt64(context, ShaderHeaderAddress + 0x08, UserDataAddress);
        return (memory, context);
    }

    private static void WriteUInt16(CpuContext context, ulong address, ushort value)
        => Assert.True(context.TryWriteUInt16(address, value));

    private static void WriteUInt64(CpuContext context, ulong address, ulong value)
        => Assert.True(context.TryWriteUInt64(address, value));
}
