// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Collections.Concurrent;
using SharpEmu.HLE;

namespace SharpEmu.Libs.Network;

public static class Http2Exports
{
    private const int Http2ErrorInvalidId = unchecked((int)0x80436004);
    private const int Http2ErrorInvalidArgument = unchecked((int)0x80436016);

    private static readonly ConcurrentDictionary<int, Http2Context> _contexts = new();
    private static readonly ConcurrentDictionary<int, Http2Template> _templates = new();
    private static readonly ConcurrentDictionary<int, Http2Request> _requests = new();
    private static int _nextContextId;
    private static int _nextTemplateId = 0x1000;
    private static int _nextRequestId = 0x2000;

    private sealed record Http2Context(int NetId, int SslId, ulong PoolSize, int MaxRequests);
    private sealed record Http2Template(
        int ContextId,
        ulong UserAgentAddress,
        int HttpVersion,
        bool AutoProxyConfig);

    private sealed class Http2Request(
        int contextId,
        int templateId,
        string method,
        string url,
        ulong contentLength)
    {
        public object Gate { get; } = new();
        public int ContextId { get; } = contextId;
        public int TemplateId { get; } = templateId;
        public string Method { get; } = method;
        public string Url { get; } = url;
        public ulong ContentLength { get; set; } = contentLength;
        public bool Sent { get; set; }
        public Dictionary<string, string> Headers { get; } = new(StringComparer.OrdinalIgnoreCase);
    }

    internal static void ResetRuntimeState()
    {
        _contexts.Clear();
        _templates.Clear();
        _requests.Clear();
        Interlocked.Exchange(ref _nextContextId, 0);
        Interlocked.Exchange(ref _nextTemplateId, 0x1000);
        Interlocked.Exchange(ref _nextRequestId, 0x2000);
    }

    [SysAbiExport(
        Nid = "3JCe3lCbQ8A",
        ExportName = "sceHttp2Init",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceHttp2")]
    public static int Http2Init(CpuContext ctx)
    {
        var netId = unchecked((int)ctx[CpuRegister.Rdi]);
        var sslId = unchecked((int)ctx[CpuRegister.Rsi]);
        var poolSize = ctx[CpuRegister.Rdx];
        var maxRequests = unchecked((int)ctx[CpuRegister.Rcx]);

        if (poolSize == 0 || maxRequests <= 0)
        {
            return ctx.SetReturn(Http2ErrorInvalidArgument);
        }

        var id = Interlocked.Increment(ref _nextContextId);
        _contexts[id] = new Http2Context(netId, sslId, poolSize, maxRequests);

        TraceHttp2("init", id, unchecked((ulong)netId), unchecked((ulong)sslId), poolSize, unchecked((ulong)maxRequests));
        ctx[CpuRegister.Rax] = unchecked((ulong)id);
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    [SysAbiExport(
        Nid = "YiBUtz-pGkc",
        ExportName = "sceHttp2Term",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceHttp2")]
    public static int Http2Term(CpuContext ctx)
    {
        var id = unchecked((int)ctx[CpuRegister.Rdi]);
        if (!_contexts.TryRemove(id, out _))
        {
            return ctx.SetReturn(Http2ErrorInvalidId);
        }

        foreach (var template in _templates)
        {
            if (template.Value.ContextId == id)
            {
                _templates.TryRemove(template.Key, out _);
            }
        }

        foreach (var request in _requests)
        {
            if (request.Value.ContextId == id)
            {
                _requests.TryRemove(request.Key, out _);
            }
        }

        TraceHttp2("term", id, 0, 0, 0, 0);
        return ctx.SetReturn(0);
    }

    [SysAbiExport(
        Nid = "+wCt7fCijgk",
        ExportName = "sceHttp2CreateTemplate",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceHttp2")]
    public static int Http2CreateTemplate(CpuContext ctx)
    {
        var contextId = unchecked((int)ctx[CpuRegister.Rdi]);
        if (!_contexts.ContainsKey(contextId))
        {
            return ctx.SetReturn(Http2ErrorInvalidId);
        }

        var templateId = Interlocked.Increment(ref _nextTemplateId);
        _templates[templateId] = new Http2Template(
            contextId,
            ctx[CpuRegister.Rsi],
            unchecked((int)ctx[CpuRegister.Rdx]),
            ctx[CpuRegister.Rcx] != 0);
        TraceHttp2(
            "create_template",
            templateId,
            unchecked((ulong)contextId),
            ctx[CpuRegister.Rsi],
            ctx[CpuRegister.Rdx],
            ctx[CpuRegister.Rcx]);
        return ctx.SetReturn(templateId);
    }

    [SysAbiExport(
        Nid = "pDom5-078DA",
        ExportName = "sceHttp2DeleteTemplate",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceHttp2")]
    public static int Http2DeleteTemplate(CpuContext ctx)
    {
        var templateId = unchecked((int)ctx[CpuRegister.Rdi]);
        if (!_templates.TryRemove(templateId, out _))
        {
            return ctx.SetReturn(Http2ErrorInvalidId);
        }

        foreach (var request in _requests)
        {
            if (request.Value.TemplateId == templateId)
            {
                _requests.TryRemove(request.Key, out _);
            }
        }

        return ctx.SetReturn(0);
    }

    [SysAbiExport(
        Nid = "mmyOCxQMVYQ",
        ExportName = "sceHttp2CreateRequestWithURL",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceHttp2")]
    public static int Http2CreateRequestWithUrl(CpuContext ctx)
    {
        var templateId = unchecked((int)ctx[CpuRegister.Rdi]);
        if (!_templates.TryGetValue(templateId, out var template) ||
            !ctx.TryReadNullTerminatedUtf8(ctx[CpuRegister.Rsi], 64, out var method) ||
            !ctx.TryReadNullTerminatedUtf8(ctx[CpuRegister.Rdx], 0x4000, out var url) ||
            string.IsNullOrWhiteSpace(method) ||
            string.IsNullOrWhiteSpace(url))
        {
            return ctx.SetReturn(Http2ErrorInvalidArgument);
        }

        var requestId = Interlocked.Increment(ref _nextRequestId);
        _requests[requestId] = new Http2Request(
            template.ContextId,
            templateId,
            method,
            url,
            ctx[CpuRegister.Rcx]);
        TraceHttp2(
            "create_request",
            requestId,
            unchecked((ulong)templateId),
            ctx[CpuRegister.Rsi],
            ctx[CpuRegister.Rdx],
            ctx[CpuRegister.Rcx]);
        return ctx.SetReturn(requestId);
    }

    [SysAbiExport(
        Nid = "nrPfOE8TQu0",
        ExportName = "sceHttp2AddRequestHeader",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceHttp2")]
    public static int Http2AddRequestHeader(CpuContext ctx)
    {
        var id = unchecked((int)ctx[CpuRegister.Rdi]);
        if (!ctx.TryReadNullTerminatedUtf8(ctx[CpuRegister.Rsi], 0x1000, out var name) ||
            !ctx.TryReadNullTerminatedUtf8(ctx[CpuRegister.Rdx], 0x4000, out var value) ||
            string.IsNullOrEmpty(name))
        {
            return ctx.SetReturn(Http2ErrorInvalidArgument);
        }

        if (_requests.TryGetValue(id, out var request))
        {
            lock (request.Gate)
            {
                request.Headers[name] = value;
            }

            return ctx.SetReturn(0);
        }

        return _templates.ContainsKey(id)
            ? ctx.SetReturn(0)
            : ctx.SetReturn(Http2ErrorInvalidId);
    }

    [SysAbiExport(
        Nid = "FSAFOzi0FpM",
        ExportName = "sceHttp2SetRequestContentLength",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceHttp2")]
    public static int Http2SetRequestContentLength(CpuContext ctx)
    {
        if (!_requests.TryGetValue(unchecked((int)ctx[CpuRegister.Rdi]), out var request))
        {
            return ctx.SetReturn(Http2ErrorInvalidId);
        }

        lock (request.Gate)
        {
            request.ContentLength = ctx[CpuRegister.Rsi];
        }

        return ctx.SetReturn(0);
    }

    [SysAbiExport(
        Nid = "rbqZig38AT8",
        ExportName = "sceHttp2SendRequest",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceHttp2")]
    public static int Http2SendRequest(CpuContext ctx)
    {
        if (!_requests.TryGetValue(unchecked((int)ctx[CpuRegister.Rdi]), out var request))
        {
            return ctx.SetReturn(Http2ErrorInvalidId);
        }

        var dataAddress = ctx[CpuRegister.Rsi];
        var size = ctx[CpuRegister.Rdx];
        if (size != 0 &&
            (dataAddress == 0 ||
             size - 1 > ulong.MaxValue - dataAddress ||
             !ctx.TryReadByte(dataAddress, out _) ||
             !ctx.TryReadByte(dataAddress + size - 1, out _)))
        {
            return ctx.SetReturn(Http2ErrorInvalidArgument);
        }

        lock (request.Gate)
        {
            request.Sent = true;
        }

        return ctx.SetReturn(0);
    }

    [SysAbiExport(
        Nid = "c8D9qIjo8EY",
        ExportName = "sceHttp2DeleteRequest",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceHttp2")]
    public static int Http2DeleteRequest(CpuContext ctx) =>
        _requests.TryRemove(unchecked((int)ctx[CpuRegister.Rdi]), out _)
            ? ctx.SetReturn(0)
            : ctx.SetReturn(Http2ErrorInvalidId);

    [SysAbiExport(
        Nid = "jjFahkBPCYs",
        ExportName = "sceHttp2SetAuthEnabled",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceHttp2")]
    public static int Http2SetAuthEnabled(CpuContext ctx) => SetOption(ctx);

    [SysAbiExport(
        Nid = "B37SruheQ5Y",
        ExportName = "sceHttp2SslDisableOption",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceHttp2")]
    public static int Http2SslDisableOption(CpuContext ctx) => SetOption(ctx);

    [SysAbiExport(
        Nid = "EWcwMpbr5F8",
        ExportName = "sceHttp2SslEnableOption",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceHttp2")]
    public static int Http2SslEnableOption(CpuContext ctx) => SetOption(ctx);

    [SysAbiExport(
        Nid = "BJgi0CH7al4",
        ExportName = "sceHttp2SetRedirectCallback",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceHttp2")]
    public static int Http2SetRedirectCallback(CpuContext ctx) => SetOption(ctx);

    [SysAbiExport(
        Nid = "izvHhqgDt44",
        ExportName = "sceHttp2SetRecvTimeOut",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceHttp2")]
    public static int Http2SetRecvTimeOut(CpuContext ctx) => SetOption(ctx);

    [SysAbiExport(
        Nid = "XPtW45xiLHk",
        ExportName = "sceHttp2SetSendTimeOut",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceHttp2")]
    public static int Http2SetSendTimeOut(CpuContext ctx) => SetOption(ctx);

    [SysAbiExport(
        Nid = "-HIO4VT87v8",
        ExportName = "sceHttp2SetConnectTimeOut",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceHttp2")]
    public static int Http2SetConnectTimeOut(CpuContext ctx) => SetOption(ctx);

    [SysAbiExport(
        Nid = "YrWX+DhPHQY",
        ExportName = "sceHttp2SetSslCallback",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceHttp2")]
    public static int Http2SetSslCallback(CpuContext ctx) => SetOption(ctx);

    private static int SetOption(CpuContext ctx)
    {
        var id = unchecked((int)ctx[CpuRegister.Rdi]);
        return _contexts.ContainsKey(id) || _templates.ContainsKey(id)
            ? ctx.SetReturn(0)
            : ctx.SetReturn(Http2ErrorInvalidId);
    }

    private static void TraceHttp2(string operation, int id, ulong arg0, ulong arg1, ulong arg2, ulong arg3)
    {
        if (!string.Equals(Environment.GetEnvironmentVariable("SHARPEMU_LOG_HTTP2"), "1", StringComparison.Ordinal))
        {
            return;
        }

        Console.Error.WriteLine(
            $"[LOADER][TRACE] http2.{operation} id={id} arg0=0x{arg0:X16} arg1=0x{arg1:X16} arg2=0x{arg2:X16} arg3=0x{arg3:X16}");
    }
}
