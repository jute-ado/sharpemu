// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using SharpEmu.HLE;
using SharpEmu.Libs.Network;
using Xunit;

namespace SharpEmu.Libs.Tests;

public sealed class Http2AsyncWaitTests
{
    private const string WaitAsyncNid = "MOp-AUhdfi8";
    private const int Http2ErrorInvalidId = unchecked((int)0x80436004);
    private const int Http2ErrorInvalidArgument = unchecked((int)0x80436016);
    private const ulong ResultAddress = 0x6000;
    private const ulong TimeoutAddress = 0x7000;

    [Theory]
    [InlineData(Generation.Gen4)]
    [InlineData(Generation.Gen5)]
    public void WaitAsyncHasExactCrossGenerationMetadata(Generation generation)
    {
        var manager = CreateManager(generation);

        Assert.True(manager.TryGetExport(WaitAsyncNid, out var export));
        Assert.Equal("sceHttp2WaitAsync", export.Name);
        Assert.Equal("libSceHttp2", export.LibraryName);
        Assert.Equal(Generation.Gen4 | Generation.Gen5, export.Target);
    }

    [Fact]
    public void WaitAsyncWritesCompleteResultAndPreservesTimeout()
    {
        var fixture = CreateRequest(resultSize: 24);
        var manager = CreateManager(Generation.Gen5);
        var timeoutBefore = ReadBytes(fixture.Memory, TimeoutAddress, sizeof(uint));

        SetWaitArguments(fixture.Context, fixture.RequestId, ResultAddress, TimeoutAddress, ulong.MaxValue);
        Assert.True(manager.TryDispatch(WaitAsyncNid, fixture.Context, out var result));
        Assert.Equal(0, (int)result);

        var output = ReadBytes(fixture.Memory, ResultAddress, 24);
        Assert.Equal(0, BinaryPrimitives.ReadInt32LittleEndian(output.AsSpan(0, 4)));
        Assert.Equal(fixture.RequestId, BinaryPrimitives.ReadInt32LittleEndian(output.AsSpan(4, 4)));
        Assert.Equal(0, BinaryPrimitives.ReadInt32LittleEndian(output.AsSpan(8, 4)));
        Assert.All(output.AsSpan(12).ToArray(), value => Assert.Equal(0, value));
        Assert.Equal(timeoutBefore, ReadBytes(fixture.Memory, TimeoutAddress, sizeof(uint)));

        fixture.Context[CpuRegister.Rdi] = unchecked((ulong)fixture.RequestId);
        fixture.Context[CpuRegister.Rsi] = 0x8000;
        Assert.True(manager.TryDispatch("9XYJwCf3lEA", fixture.Context, out result));
        Assert.Equal(0, (int)result);
        Assert.Equal(503, BinaryPrimitives.ReadInt32LittleEndian(ReadBytes(fixture.Memory, 0x8000, sizeof(int))));
    }

    [Fact]
    public void WaitAsyncRejectsInvalidIdsAndShortOutputsWithoutPartialWrites()
    {
        var fixture = CreateRequest(resultSize: 23);
        var manager = CreateManager(Generation.Gen5);
        var resultBefore = ReadBytes(fixture.Memory, ResultAddress, 23);

        SetWaitArguments(fixture.Context, int.MaxValue, ResultAddress, TimeoutAddress, 0);
        Assert.True(manager.TryDispatch(WaitAsyncNid, fixture.Context, out var result));
        Assert.Equal(Http2ErrorInvalidId, (int)result);
        Assert.Equal(resultBefore, ReadBytes(fixture.Memory, ResultAddress, 23));

        SetWaitArguments(fixture.Context, fixture.RequestId, ResultAddress, TimeoutAddress, 0);
        Assert.True(manager.TryDispatch(WaitAsyncNid, fixture.Context, out result));
        Assert.Equal(Http2ErrorInvalidArgument, (int)result);
        Assert.Equal(resultBefore, ReadBytes(fixture.Memory, ResultAddress, 23));

        fixture.Context[CpuRegister.Rdi] = unchecked((ulong)fixture.RequestId);
        fixture.Context[CpuRegister.Rsi] = 0x8000;
        Assert.True(manager.TryDispatch("9XYJwCf3lEA", fixture.Context, out result));
        Assert.True((int)result < 0);
        Assert.All(ReadBytes(fixture.Memory, 0x8000, sizeof(int)), value => Assert.Equal(0xA5, value));
    }

    private static RequestFixture CreateRequest(int resultSize)
    {
        Http2Exports.ResetRuntimeState();
        var manager = CreateManager(Generation.Gen5);
        var memory = new FakeGuestMemory();
        memory.AddRegion(0x1000, "GET\0"u8.ToArray());
        memory.AddRegion(0x2000, "https://example.com/\0"u8.ToArray());
        memory.AddRegion(ResultAddress, Enumerable.Repeat((byte)0xA5, resultSize).ToArray());
        memory.AddRegion(TimeoutAddress, new byte[] { 0x78, 0x56, 0x34, 0x12 });
        memory.AddRegion(0x8000, Enumerable.Repeat((byte)0xA5, sizeof(int)).ToArray());
        var context = new CpuContext(memory, Generation.Gen5);

        context[CpuRegister.Rdi] = 1;
        context[CpuRegister.Rsi] = 1;
        context[CpuRegister.Rdx] = 0x1000;
        context[CpuRegister.Rcx] = 4;
        Assert.True(manager.TryDispatch("3JCe3lCbQ8A", context, out var result));
        Assert.Equal(0, (int)result);
        var contextId = unchecked((int)context[CpuRegister.Rax]);

        context[CpuRegister.Rdi] = unchecked((ulong)contextId);
        context[CpuRegister.Rsi] = 0;
        context[CpuRegister.Rdx] = 2;
        context[CpuRegister.Rcx] = 1;
        Assert.True(manager.TryDispatch("+wCt7fCijgk", context, out result));
        var templateId = unchecked((int)result);
        Assert.True(templateId > 0);

        context[CpuRegister.Rdi] = unchecked((ulong)templateId);
        context[CpuRegister.Rsi] = 0x1000;
        context[CpuRegister.Rdx] = 0x2000;
        context[CpuRegister.Rcx] = 0;
        Assert.True(manager.TryDispatch("mmyOCxQMVYQ", context, out result));
        var requestId = unchecked((int)result);
        Assert.True(requestId > 0);

        return new RequestFixture(memory, context, requestId);
    }

    private static void SetWaitArguments(
        CpuContext context,
        int requestId,
        ulong resultAddress,
        ulong timeoutAddress,
        ulong optionAddress)
    {
        context[CpuRegister.Rdi] = unchecked((ulong)requestId);
        context[CpuRegister.Rsi] = resultAddress;
        context[CpuRegister.Rdx] = timeoutAddress;
        context[CpuRegister.Rcx] = optionAddress;
    }

    private static ModuleManager CreateManager(Generation generation)
    {
        var manager = new ModuleManager();
        manager.RegisterExports(SharpEmu.Generated.SysAbiExportRegistry.CreateExports(generation));
        return manager;
    }

    private static byte[] ReadBytes(FakeGuestMemory memory, ulong address, int length)
    {
        var bytes = new byte[length];
        Assert.True(memory.TryRead(address, bytes));
        return bytes;
    }

    private sealed record RequestFixture(FakeGuestMemory Memory, CpuContext Context, int RequestId);
}
