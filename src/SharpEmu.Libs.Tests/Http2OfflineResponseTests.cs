// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using SharpEmu.HLE;
using Xunit;

namespace SharpEmu.Libs.Tests;

public sealed class Http2OfflineResponseTests
{
    [Fact]
    public void SentRequestExposesDeterministicServiceUnavailableResponse()
    {
        var exports = SharpEmu.Generated.SysAbiExportRegistry.CreateExports(Generation.Gen5);
        var initialize = Find(exports, "3JCe3lCbQ8A");
        var createTemplate = Find(exports, "+wCt7fCijgk");
        var createRequest = Find(exports, "mmyOCxQMVYQ");
        var sendRequest = Find(exports, "rbqZig38AT8");
        var getStatus = Find(exports, "9XYJwCf3lEA");
        var getContentLength = Find(exports, "o0DBQpFE13o");
        var getHeaders = Find(exports, "-rdXUi2XW90");
        var status = Enumerable.Repeat((byte)0xA5, sizeof(int)).ToArray();
        var contentLengthResult = new byte[sizeof(int)];
        var contentLength = Enumerable.Repeat((byte)0xA5, sizeof(ulong)).ToArray();
        var headerAddress = Enumerable.Repeat((byte)0xA5, sizeof(ulong)).ToArray();
        var headerSize = Enumerable.Repeat((byte)0xA5, sizeof(ulong)).ToArray();
        var memory = new FakeGuestMemory();
        memory.AddRegion(0x1000, "GET\0"u8.ToArray());
        memory.AddRegion(0x2000, "https://example.com/\0"u8.ToArray());
        memory.AddRegion(0x6000, status);
        memory.AddRegion(0x7000, contentLengthResult);
        memory.AddRegion(0x8000, contentLength);
        memory.AddRegion(0x9000, headerAddress);
        memory.AddRegion(0xA000, headerSize);
        var context = new CpuContext(memory, Generation.Gen5);

        context[CpuRegister.Rdi] = 1;
        context[CpuRegister.Rsi] = 1;
        context[CpuRegister.Rdx] = 0x1000;
        context[CpuRegister.Rcx] = 4;
        Assert.Equal(0, initialize.Function(context));
        var contextId = unchecked((int)context[CpuRegister.Rax]);
        context[CpuRegister.Rdi] = unchecked((ulong)contextId);
        context[CpuRegister.Rsi] = 0;
        context[CpuRegister.Rdx] = 2;
        context[CpuRegister.Rcx] = 1;
        var templateId = createTemplate.Function(context);
        context[CpuRegister.Rdi] = unchecked((ulong)templateId);
        context[CpuRegister.Rsi] = 0x1000;
        context[CpuRegister.Rdx] = 0x2000;
        context[CpuRegister.Rcx] = 0;
        var requestId = createRequest.Function(context);

        context[CpuRegister.Rdi] = unchecked((ulong)requestId);
        context[CpuRegister.Rsi] = 0x6000;
        Assert.True(getStatus.Function(context) < 0);
        Assert.All(status, value => Assert.Equal(0xA5, value));

        context[CpuRegister.Rdi] = unchecked((ulong)requestId);
        context[CpuRegister.Rsi] = 0;
        context[CpuRegister.Rdx] = 0;
        Assert.Equal(0, sendRequest.Function(context));

        context[CpuRegister.Rdi] = unchecked((ulong)requestId);
        context[CpuRegister.Rsi] = 0x6000;
        Assert.Equal(0, getStatus.Function(context));
        Assert.Equal(503, BinaryPrimitives.ReadInt32LittleEndian(status));

        context[CpuRegister.Rdi] = unchecked((ulong)requestId);
        context[CpuRegister.Rsi] = 0x7000;
        context[CpuRegister.Rdx] = 0x8000;
        Assert.Equal(0, getContentLength.Function(context));
        Assert.Equal(0, BinaryPrimitives.ReadInt32LittleEndian(contentLengthResult));
        Assert.Equal(0UL, BinaryPrimitives.ReadUInt64LittleEndian(contentLength));

        context[CpuRegister.Rdi] = unchecked((ulong)requestId);
        context[CpuRegister.Rsi] = 0x9000;
        context[CpuRegister.Rdx] = 0xA000;
        Assert.Equal(0, getHeaders.Function(context));
        Assert.Equal(0UL, BinaryPrimitives.ReadUInt64LittleEndian(headerAddress));
        Assert.Equal(0UL, BinaryPrimitives.ReadUInt64LittleEndian(headerSize));
    }

    private static ExportedFunction Find(IEnumerable<ExportedFunction> exports, string nid) =>
        Assert.Single(exports, export => export.Nid == nid);
}
