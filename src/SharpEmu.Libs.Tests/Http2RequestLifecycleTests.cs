// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using Xunit;

namespace SharpEmu.Libs.Tests;

public sealed class Http2RequestLifecycleTests
{
    [Fact]
    public void RequestOperationsRequireLiveOwningTemplate()
    {
        var exports = SharpEmu.Generated.SysAbiExportRegistry.CreateExports(Generation.Gen5);
        var initialize = Find(exports, "3JCe3lCbQ8A");
        var terminate = Find(exports, "YiBUtz-pGkc");
        var createTemplate = Find(exports, "+wCt7fCijgk");
        var createRequest = Find(exports, "mmyOCxQMVYQ");
        var addHeader = Find(exports, "nrPfOE8TQu0");
        var setContentLength = Find(exports, "FSAFOzi0FpM");
        var sendRequest = Find(exports, "rbqZig38AT8");
        var deleteRequest = Find(exports, "c8D9qIjo8EY");
        var memory = new FakeGuestMemory();
        memory.AddRegion(0x1000, "POST\0"u8.ToArray());
        memory.AddRegion(0x2000, "https://example.com/session\0"u8.ToArray());
        memory.AddRegion(0x3000, "Content-Type\0"u8.ToArray());
        memory.AddRegion(0x4000, "application/json\0"u8.ToArray());
        memory.AddRegion(0x5000, "{}"u8.ToArray());
        memory.AddRegion(ulong.MaxValue, new byte[1]);
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
        Assert.True(templateId > 0);

        context[CpuRegister.Rdi] = unchecked((ulong)templateId);
        context[CpuRegister.Rsi] = 0x1000;
        context[CpuRegister.Rdx] = 0x2000;
        context[CpuRegister.Rcx] = 2;
        var requestId = createRequest.Function(context);
        Assert.True(requestId > 0);

        context[CpuRegister.Rdi] = unchecked((ulong)requestId);
        context[CpuRegister.Rsi] = 0x3000;
        context[CpuRegister.Rdx] = 0x4000;
        context[CpuRegister.Rcx] = 1;
        Assert.Equal(0, addHeader.Function(context));

        context[CpuRegister.Rdi] = unchecked((ulong)requestId);
        context[CpuRegister.Rsi] = 2;
        Assert.Equal(0, setContentLength.Function(context));

        context[CpuRegister.Rdi] = unchecked((ulong)requestId);
        context[CpuRegister.Rsi] = 0x5000;
        context[CpuRegister.Rdx] = 2;
        Assert.Equal(0, sendRequest.Function(context));

        context[CpuRegister.Rdi] = unchecked((ulong)requestId);
        context[CpuRegister.Rsi] = ulong.MaxValue;
        context[CpuRegister.Rdx] = 2;
        Assert.True(sendRequest.Function(context) < 0);

        context[CpuRegister.Rdi] = unchecked((ulong)requestId);
        Assert.Equal(0, deleteRequest.Function(context));
        Assert.True(deleteRequest.Function(context) < 0);

        context[CpuRegister.Rdi] = unchecked((ulong)requestId);
        Assert.True(setContentLength.Function(context) < 0);

        context[CpuRegister.Rdi] = unchecked((ulong)contextId);
        Assert.Equal(0, terminate.Function(context));
    }

    [Fact]
    public void TerminatingContextInvalidatesOwnedRequests()
    {
        var exports = SharpEmu.Generated.SysAbiExportRegistry.CreateExports(Generation.Gen5);
        var initialize = Find(exports, "3JCe3lCbQ8A");
        var terminate = Find(exports, "YiBUtz-pGkc");
        var createTemplate = Find(exports, "+wCt7fCijgk");
        var createRequest = Find(exports, "mmyOCxQMVYQ");
        var deleteRequest = Find(exports, "c8D9qIjo8EY");
        var memory = new FakeGuestMemory();
        memory.AddRegion(0x1000, "GET\0"u8.ToArray());
        memory.AddRegion(0x2000, "https://example.com/\0"u8.ToArray());
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
        Assert.True(requestId > 0);

        context[CpuRegister.Rdi] = unchecked((ulong)contextId);
        Assert.Equal(0, terminate.Function(context));
        context[CpuRegister.Rdi] = unchecked((ulong)requestId);
        Assert.True(deleteRequest.Function(context) < 0);
    }

    private static ExportedFunction Find(IEnumerable<ExportedFunction> exports, string nid) =>
        Assert.Single(exports, export => export.Nid == nid);
}
