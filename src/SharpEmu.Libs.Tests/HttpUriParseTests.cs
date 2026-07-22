// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using System.Text;
using SharpEmu.HLE;
using Xunit;

namespace SharpEmu.Libs.Tests;

public sealed class HttpUriParseTests
{
    private const string UriParseNid = "IWalAn-guFs";
    private const ulong SourceAddress = 0x1000;
    private const ulong RequiredAddress = 0x2000;
    private const ulong OutputAddress = 0x3000;
    private const ulong PoolAddress = 0x4000;

    [Fact]
    public void UriParseSupportsSizeQueryAndWritesGuestOwnedElements()
    {
        const string uri = "https://user:pass@example.com:8443/a/b?x=1#frag";
        const ulong expectedRequiredSize = 44;
        var source = Encoding.UTF8.GetBytes(uri + '\0');
        var required = new byte[sizeof(ulong)];
        var output = new byte[80];
        var pool = new byte[expectedRequiredSize];
        var memory = new FakeGuestMemory();
        memory.AddRegion(SourceAddress, source);
        memory.AddRegion(RequiredAddress, required);
        memory.AddRegion(OutputAddress, output);
        memory.AddRegion(PoolAddress, pool);
        var context = new CpuContext(memory, Generation.Gen5);
        var export = Assert.Single(
            SharpEmu.Generated.SysAbiExportRegistry.CreateExports(Generation.Gen5),
            candidate => candidate.Nid == UriParseNid);

        context[CpuRegister.Rdi] = 0;
        context[CpuRegister.Rsi] = SourceAddress;
        context[CpuRegister.Rdx] = 0;
        context[CpuRegister.Rcx] = RequiredAddress;
        context[CpuRegister.R8] = 0;
        Assert.Equal(0, export.Function(context));
        Assert.Equal(expectedRequiredSize, BinaryPrimitives.ReadUInt64LittleEndian(required));

        context[CpuRegister.Rdi] = OutputAddress;
        context[CpuRegister.Rdx] = PoolAddress;
        context[CpuRegister.R8] = expectedRequiredSize;
        Assert.Equal(0, export.Function(context));

        Assert.Equal(0, output[0]);
        Assert.Equal(PoolAddress, ReadPointer(output, 8));
        Assert.Equal(PoolAddress + 6, ReadPointer(output, 16));
        Assert.Equal(PoolAddress + 11, ReadPointer(output, 24));
        Assert.Equal(PoolAddress + 16, ReadPointer(output, 32));
        Assert.Equal(PoolAddress + 28, ReadPointer(output, 40));
        Assert.Equal(PoolAddress + 33, ReadPointer(output, 48));
        Assert.Equal(PoolAddress + 38, ReadPointer(output, 56));
        Assert.Equal((ushort)8443, BinaryPrimitives.ReadUInt16LittleEndian(output.AsSpan(64)));
        Assert.Equal(
            "https\0user\0pass\0example.com\0/a/b\0?x=1\0#frag\0"u8.ToArray(),
            pool);
    }

    [Fact]
    public void UriParseRejectsInsufficientPoolWithoutWritingOutput()
    {
        var source = "https://example.com/path\0"u8.ToArray();
        var required = new byte[sizeof(ulong)];
        var output = Enumerable.Repeat((byte)0xA5, 80).ToArray();
        var pool = Enumerable.Repeat((byte)0x5A, 4).ToArray();
        var memory = new FakeGuestMemory();
        memory.AddRegion(SourceAddress, source);
        memory.AddRegion(RequiredAddress, required);
        memory.AddRegion(OutputAddress, output);
        memory.AddRegion(PoolAddress, pool);
        var context = new CpuContext(memory, Generation.Gen5);
        context[CpuRegister.Rdi] = OutputAddress;
        context[CpuRegister.Rsi] = SourceAddress;
        context[CpuRegister.Rdx] = PoolAddress;
        context[CpuRegister.Rcx] = RequiredAddress;
        context[CpuRegister.R8] = unchecked((ulong)pool.Length);
        var export = Assert.Single(
            SharpEmu.Generated.SysAbiExportRegistry.CreateExports(Generation.Gen5),
            candidate => candidate.Nid == UriParseNid);

        Assert.Equal(unchecked((int)0x80431022), export.Function(context));
        Assert.All(output, value => Assert.Equal(0xA5, value));
        Assert.All(pool, value => Assert.Equal(0x5A, value));
    }

    private static ulong ReadPointer(byte[] output, int offset) =>
        BinaryPrimitives.ReadUInt64LittleEndian(output.AsSpan(offset));
}
