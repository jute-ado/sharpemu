// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using SharpEmu.HLE;
using SharpEmu.Libs.Network;
using Xunit;

namespace SharpEmu.Libs.Tests;

public sealed class Http2AsyncRequestTests
{
    private const string SendRequestAsyncNid = "A+NVAFu4eCg";
    private const int Http2ErrorInvalidId = unchecked((int)0x80436004);
    private const int Http2ErrorInvalidArgument = unchecked((int)0x80436016);

    [Theory]
    [InlineData(Generation.Gen4)]
    [InlineData(Generation.Gen5)]
    public void SendRequestAsyncHasExactCrossGenerationMetadata(Generation generation)
    {
        var manager = CreateManager(generation);

        Assert.True(manager.TryGetExport(SendRequestAsyncNid, out var export));
        Assert.Equal("sceHttp2SendRequestAsync", export.Name);
        Assert.Equal("libSceHttp2", export.LibraryName);
        Assert.Equal(Generation.Gen4 | Generation.Gen5, export.Target);
    }

    [Fact]
    public void SendRequestAsyncTransitionsRequestToDeterministicOfflineResponse()
    {
        var fixture = CreateRequest();
        var manager = CreateManager(Generation.Gen5);

        fixture.Context[CpuRegister.Rdi] = unchecked((ulong)fixture.RequestId);
        fixture.Context[CpuRegister.Rsi] = 0;
        fixture.Context[CpuRegister.Rdx] = 0;
        fixture.Context[CpuRegister.Rcx] = ulong.MaxValue;
        fixture.Context[CpuRegister.R8] = ulong.MaxValue;
        Assert.True(manager.TryDispatch(SendRequestAsyncNid, fixture.Context, out var result));
        Assert.Equal(0, (int)result);

        fixture.Context[CpuRegister.Rdi] = unchecked((ulong)fixture.RequestId);
        fixture.Context[CpuRegister.Rsi] = 0x6000;
        Assert.True(manager.TryDispatch("9XYJwCf3lEA", fixture.Context, out result));
        Assert.Equal(0, (int)result);
        Assert.Equal(503, BinaryPrimitives.ReadInt32LittleEndian(ReadBytes(fixture.Memory, 0x6000, sizeof(int))));
    }

    [Fact]
    public void SendRequestAsyncRejectsInvalidRequestsAndUnreadableBodiesWithoutSending()
    {
        var fixture = CreateRequest();
        var manager = CreateManager(Generation.Gen5);

        fixture.Context[CpuRegister.Rdi] = int.MaxValue;
        fixture.Context[CpuRegister.Rsi] = 0;
        fixture.Context[CpuRegister.Rdx] = 0;
        fixture.Context[CpuRegister.Rcx] = 0;
        fixture.Context[CpuRegister.R8] = 0;
        Assert.True(manager.TryDispatch(SendRequestAsyncNid, fixture.Context, out var result));
        Assert.Equal(Http2ErrorInvalidId, (int)result);

        fixture.Context[CpuRegister.Rdi] = unchecked((ulong)fixture.RequestId);
        fixture.Context[CpuRegister.Rsi] = 0;
        fixture.Context[CpuRegister.Rdx] = 1;
        Assert.True(manager.TryDispatch(SendRequestAsyncNid, fixture.Context, out result));
        Assert.Equal(Http2ErrorInvalidArgument, (int)result);

        fixture.Context[CpuRegister.Rdi] = unchecked((ulong)fixture.RequestId);
        fixture.Context[CpuRegister.Rsi] = 0x6000;
        Assert.True(manager.TryDispatch("9XYJwCf3lEA", fixture.Context, out result));
        Assert.True((int)result < 0);
        Assert.All(ReadBytes(fixture.Memory, 0x6000, sizeof(int)), value => Assert.Equal(0xA5, value));
    }

    private static RequestFixture CreateRequest()
    {
        Http2Exports.ResetRuntimeState();
        var manager = CreateManager(Generation.Gen5);
        var memory = new FakeGuestMemory();
        memory.AddRegion(0x1000, "GET\0"u8.ToArray());
        memory.AddRegion(0x2000, "https://example.com/\0"u8.ToArray());
        memory.AddRegion(0x6000, Enumerable.Repeat((byte)0xA5, sizeof(int)).ToArray());
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
