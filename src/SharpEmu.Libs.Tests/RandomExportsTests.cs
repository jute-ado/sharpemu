// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using SharpEmu.Libs.Random;
using Xunit;

namespace SharpEmu.Libs.Tests;

public sealed class RandomExportsTests
{
    private const ulong OutputAddress = 0x1000;
    private const int RandomErrorInvalid = unchecked((int)0x817C0016);

    [Fact]
    public void GetRandomNumberFillsTheRequestedGuestBuffer()
    {
        var first = new byte[64];
        var second = new byte[64];

        Assert.Equal(0, Invoke(first, first.Length));
        Assert.Equal(0, Invoke(second, second.Length));
        Assert.NotEqual(first, second);
    }

    [Fact]
    public void GetRandomNumberAcceptsAnEmptyNullBuffer()
    {
        var context = new CpuContext(new FakeGuestMemory(), Generation.Gen5);

        var result = RandomExports.RandomGetRandomNumber(context);

        Assert.Equal(0, result);
        Assert.Equal(0UL, context[CpuRegister.Rax]);
    }

    [Theory]
    [InlineData(0UL, 1UL)]
    [InlineData(OutputAddress, 65UL)]
    public void GetRandomNumberRejectsInvalidArguments(ulong address, ulong size)
    {
        var context = new CpuContext(new FakeGuestMemory(), Generation.Gen5);
        context[CpuRegister.Rdi] = address;
        context[CpuRegister.Rsi] = size;

        var result = RandomExports.RandomGetRandomNumber(context);

        Assert.Equal(RandomErrorInvalid, result);
        Assert.Equal(unchecked((ulong)RandomErrorInvalid), context[CpuRegister.Rax]);
    }

    [Fact]
    public void GetRandomNumberReportsAnUnmappedGuestBuffer()
    {
        var context = new CpuContext(new FakeGuestMemory(), Generation.Gen5);
        context[CpuRegister.Rdi] = OutputAddress;
        context[CpuRegister.Rsi] = 16;

        var result = RandomExports.RandomGetRandomNumber(context);

        Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT, result);
        Assert.Equal(
            unchecked((ulong)(int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT),
            context[CpuRegister.Rax]);
    }

    [Fact]
    public void RandomExportMetadataIsExact()
    {
        var exports = SharpEmu.Generated.SysAbiExportRegistry
            .CreateExports(Generation.Gen4 | Generation.Gen5);
        var export = Assert.Single(exports, candidate => candidate.Nid == "PI7jIZj4pcE");

        Assert.Equal("sceRandomGetRandomNumber", export.Name);
        Assert.Equal("libSceRandom", export.LibraryName);
        Assert.Equal(Generation.Gen4 | Generation.Gen5, export.Target);
    }

    private static int Invoke(byte[] output, int size)
    {
        var memory = new FakeGuestMemory();
        memory.AddRegion(OutputAddress, output);
        var context = new CpuContext(memory, Generation.Gen5);
        context[CpuRegister.Rdi] = OutputAddress;
        context[CpuRegister.Rsi] = checked((ulong)size);

        var result = RandomExports.RandomGetRandomNumber(context);
        Assert.Equal(unchecked((ulong)result), context[CpuRegister.Rax]);
        return result;
    }
}
