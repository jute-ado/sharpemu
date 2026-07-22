// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using SharpEmu.Libs.Np;
using Xunit;

namespace SharpEmu.Libs.Tests;

public sealed class NpHasSignedUpTests
{
    private const int InvalidArgument = unchecked((int)0x80550003);
    private const ulong ResultAddress = 0x1000;

    [Fact]
    public void ExportMetadataIsExact()
    {
        ExportMetadataAssert.Exact(
            "Oad3rvY-NJQ",
            "sceNpHasSignedUp",
            "libSceNpManager",
            Generation.Gen4 | Generation.Gen5);
    }

    [Fact]
    public void ValidLocalUserReportsSignedUpConsistentlyWithOfflineProfile()
    {
        var memory = new FakeGuestMemory();
        memory.AddRegion(ResultAddress, new byte[] { 0xCC });
        var context = new CpuContext(memory, Generation.Gen5);
        context[CpuRegister.Rdi] = 1000;
        context[CpuRegister.Rsi] = ResultAddress;

        Assert.Equal(0, NpManagerExports.NpHasSignedUp(context));
        Span<byte> result = stackalloc byte[1];
        Assert.True(memory.TryRead(ResultAddress, result));
        Assert.Equal(1, result[0]);
    }

    [Theory]
    [InlineData(unchecked((ulong)-1), ResultAddress)]
    [InlineData(1000UL, 0UL)]
    public void InvalidArgumentsDoNotWriteOutput(ulong userId, ulong resultAddress)
    {
        var memory = new FakeGuestMemory();
        memory.AddRegion(ResultAddress, new byte[] { 0xCC });
        var context = new CpuContext(memory, Generation.Gen5);
        context[CpuRegister.Rdi] = userId;
        context[CpuRegister.Rsi] = resultAddress;

        Assert.Equal(InvalidArgument, NpManagerExports.NpHasSignedUp(context));
        Span<byte> result = stackalloc byte[1];
        Assert.True(memory.TryRead(ResultAddress, result));
        Assert.Equal(0xCC, result[0]);
    }

    [Fact]
    public void UnmappedResultReturnsMemoryFault()
    {
        var context = new CpuContext(new FakeGuestMemory(), Generation.Gen5);
        context[CpuRegister.Rdi] = 1000;
        context[CpuRegister.Rsi] = ResultAddress;

        Assert.Equal(
            (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT,
            NpManagerExports.NpHasSignedUp(context));
    }
}
