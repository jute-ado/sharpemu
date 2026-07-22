// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using SharpEmu.HLE;
using SharpEmu.Libs.GameUpdate;
using Xunit;

namespace SharpEmu.Libs.Tests;

[Collection("GameUpdate state")]
public sealed class GameUpdateRequestTests : IDisposable
{
    private const ulong CreateParameterAddress = 0x1000;
    private const ulong CheckParameterAddress = 0x2000;
    private const ulong CheckResultAddress = 0x3000;

    public GameUpdateRequestTests() => GameUpdateExports.ResetRuntimeState();

    public void Dispose()
    {
        GameUpdateExports.ResetRuntimeState();
        GC.SuppressFinalize(this);
    }

    [Theory]
    [InlineData("UvcvKaFvupA", "sceGameUpdateCreateRequest")]
    [InlineData("LYVV9z8+owM", "sceGameUpdateCheck")]
    [InlineData("bcCyjHN5sn0", "sceGameUpdateDeleteRequest")]
    [InlineData("NSH-C-OmoNI", "sceGameUpdateTerminate")]
    public void RequestLifecycleExportsHaveExactMetadata(
        string nid,
        string name)
    {
        ExportMetadataAssert.Exact(
            nid,
            name,
            "libSceGameUpdate",
            Generation.Gen4 | Generation.Gen5);
    }

    [Fact]
    public void CreateCheckDeleteAndTerminateOwnBoundedRequestState()
    {
        var memory = new FakeGuestMemory();
        memory.AddRegion(CreateParameterAddress, new byte[48]);
        memory.AddRegion(CheckParameterAddress, SizedRecord());
        memory.AddRegion(CheckResultAddress, SizedRecord(fill: 0xCC));
        var context = new CpuContext(memory, Generation.Gen5);

        Assert.Equal(0, GameUpdateExports.GameUpdateInitialize(context));
        context[CpuRegister.Rdi] = CreateParameterAddress;
        Assert.Equal(1, GameUpdateExports.GameUpdateCreateRequest(context));
        Assert.Equal(2, GameUpdateExports.GameUpdateCreateRequest(context));

        context[CpuRegister.Rdi] = 1;
        context[CpuRegister.Rsi] = CheckParameterAddress;
        context[CpuRegister.Rdx] = CheckResultAddress;
        Assert.Equal(0, GameUpdateExports.GameUpdateCheck(context));
        Span<byte> result = stackalloc byte[48];
        Assert.True(memory.TryRead(CheckResultAddress, result));
        Assert.Equal(48UL, BinaryPrimitives.ReadUInt64LittleEndian(result));
        Assert.All(result[8..].ToArray(), value => Assert.Equal(0, value));

        context[CpuRegister.Rdi] = 1;
        Assert.Equal(0, GameUpdateExports.GameUpdateDeleteRequest(context));
        context[CpuRegister.Rdi] = CreateParameterAddress;
        Assert.Equal(1, GameUpdateExports.GameUpdateCreateRequest(context));

        Assert.Equal(0, GameUpdateExports.GameUpdateTerminate(context));
        context[CpuRegister.Rdi] = 1;
        context[CpuRegister.Rsi] = CheckParameterAddress;
        context[CpuRegister.Rdx] = CheckResultAddress;
        Assert.Equal(
            (int)OrbisGen2Result.ORBIS_GEN2_ERROR_DELETED,
            GameUpdateExports.GameUpdateCheck(context));
    }

    [Fact]
    public void CheckRejectsNullAndIncorrectlySizedRecordsWithoutMutation()
    {
        var memory = new FakeGuestMemory();
        memory.AddRegion(CreateParameterAddress, new byte[48]);
        memory.AddRegion(CheckParameterAddress, SizedRecord(size: 47));
        memory.AddRegion(CheckResultAddress, SizedRecord(fill: 0xCC));
        var context = new CpuContext(memory, Generation.Gen5);
        GameUpdateExports.GameUpdateInitialize(context);
        context[CpuRegister.Rdi] = CreateParameterAddress;
        var requestId = GameUpdateExports.GameUpdateCreateRequest(context);

        context[CpuRegister.Rdi] = unchecked((ulong)requestId);
        context[CpuRegister.Rsi] = CheckParameterAddress;
        context[CpuRegister.Rdx] = CheckResultAddress;
        Assert.Equal(
            (int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT,
            GameUpdateExports.GameUpdateCheck(context));
        Span<byte> result = stackalloc byte[48];
        Assert.True(memory.TryRead(CheckResultAddress, result));
        Assert.All(result[8..].ToArray(), value => Assert.Equal(0xCC, value));

        context[CpuRegister.Rsi] = 0;
        Assert.Equal(
            (int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT,
            GameUpdateExports.GameUpdateCheck(context));
    }

    private static byte[] SizedRecord(ulong size = 48, byte fill = 0)
    {
        var record = Enumerable.Repeat(fill, 48).ToArray();
        BinaryPrimitives.WriteUInt64LittleEndian(record, size);
        return record;
    }
}
