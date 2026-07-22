// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using SharpEmu.HLE;

namespace SharpEmu.Libs.Network;

public static class HttpExports
{
    private const int HttpErrorInvalidId = unchecked((int)0x80431100);
    private const int HttpErrorOutOfMemory = unchecked((int)0x80431022);
    private const int HttpErrorInvalidValue = unchecked((int)0x804311FE);
    private const int HttpErrorInvalidUrl = unchecked((int)0x80433060);
    private const int MaximumUriBytes = 0x4000;
    private const int UriElementSize = 80;
    private const uint UriBuildWithScheme = 0x01;
    private const uint UriBuildWithHostname = 0x02;
    private const uint UriBuildWithPort = 0x04;
    private const uint UriBuildWithPath = 0x08;
    private const uint UriBuildWithUsername = 0x10;
    private const uint UriBuildWithPassword = 0x20;
    private const uint UriBuildWithQuery = 0x40;
    private const uint UriBuildWithFragment = 0x80;

    private static readonly ConcurrentDictionary<int, HttpContext> Contexts = new();
    private static readonly ConcurrentDictionary<int, HttpTemplate> Templates = new();
    private static int _nextContextId;
    private static int _nextTemplateId = 0x1000;

    private sealed record HttpContext(int NetMemoryId, int SslContextId, ulong PoolSize);

    private sealed record HttpTemplate(int ContextId, ulong UserAgentAddress, int HttpVersion, bool AutoProxyConfig);

    private sealed record ParsedUri(
        bool Opaque,
        string Scheme,
        string Username,
        string Password,
        string Hostname,
        string Path,
        string Query,
        string Fragment,
        ushort Port);

    internal static void ResetRuntimeState()
    {
        Contexts.Clear();
        Templates.Clear();
        Interlocked.Exchange(ref _nextContextId, 0);
        Interlocked.Exchange(ref _nextTemplateId, 0x1000);
    }

    [SysAbiExport(
        Nid = "A9cVMUtEp4Y",
        ExportName = "sceHttpInit",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceHttp")]
    public static int HttpInit(CpuContext ctx)
    {
        var netMemoryId = unchecked((int)ctx[CpuRegister.Rdi]);
        var sslContextId = unchecked((int)ctx[CpuRegister.Rsi]);
        var poolSize = ctx[CpuRegister.Rdx];
        if (poolSize == 0)
        {
            return ctx.SetReturn(HttpErrorInvalidValue);
        }

        var id = Interlocked.Increment(ref _nextContextId);
        Contexts[id] = new HttpContext(netMemoryId, sslContextId, poolSize);
        TraceHttp("init", id, unchecked((ulong)netMemoryId), unchecked((ulong)sslContextId), poolSize, 0);
        ctx[CpuRegister.Rax] = unchecked((ulong)id);
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    [SysAbiExport(
        Nid = "0gYjPTR-6cY",
        ExportName = "sceHttpCreateTemplate",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceHttp")]
    public static int HttpCreateTemplate(CpuContext ctx)
    {
        var contextId = unchecked((int)ctx[CpuRegister.Rdi]);
        if (!Contexts.ContainsKey(contextId))
        {
            return ctx.SetReturn(HttpErrorInvalidId);
        }

        var userAgentAddress = ctx[CpuRegister.Rsi];
        var httpVersion = unchecked((int)ctx[CpuRegister.Rdx]);
        var autoProxyConfig = ctx[CpuRegister.Rcx] != 0;
        var id = Interlocked.Increment(ref _nextTemplateId);
        Templates[id] = new HttpTemplate(contextId, userAgentAddress, httpVersion, autoProxyConfig);
        TraceHttp("create_template", id, unchecked((ulong)contextId), userAgentAddress, unchecked((ulong)httpVersion), autoProxyConfig ? 1UL : 0UL);
        ctx[CpuRegister.Rax] = unchecked((ulong)id);
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    [SysAbiExport(
        Nid = "4I8vEpuEhZ8",
        ExportName = "sceHttpDeleteTemplate",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceHttp")]
    public static int HttpDeleteTemplate(CpuContext ctx)
    {
        var templateId = unchecked((int)ctx[CpuRegister.Rdi]);
        return Templates.TryRemove(templateId, out _)
            ? ctx.SetReturn(0)
            : ctx.SetReturn(HttpErrorInvalidId);
    }

    [SysAbiExport(
        Nid = "Ik-KpLTlf7Q",
        ExportName = "sceHttpTerm",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceHttp")]
    public static int HttpTerm(CpuContext ctx)
    {
        var contextId = unchecked((int)ctx[CpuRegister.Rdi]);
        if (!Contexts.TryRemove(contextId, out _))
        {
            return ctx.SetReturn(HttpErrorInvalidId);
        }

        foreach (var pair in Templates)
        {
            if (pair.Value.ContextId == contextId)
            {
                Templates.TryRemove(pair.Key, out _);
            }
        }

        return ctx.SetReturn(0);
    }

    [SysAbiExport(
        Nid = "IWalAn-guFs",
        ExportName = "sceHttpUriParse",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceHttp")]
    public static int HttpUriParse(CpuContext ctx)
    {
        var outputAddress = ctx[CpuRegister.Rdi];
        var sourceAddress = ctx[CpuRegister.Rsi];
        var poolAddress = ctx[CpuRegister.Rdx];
        var requiredAddress = ctx[CpuRegister.Rcx];
        var preparedSize = ctx[CpuRegister.R8];
        if (!ctx.TryReadNullTerminatedUtf8(sourceAddress, MaximumUriBytes, out var source) ||
            !TryParseUri(source, out var parsed))
        {
            return ctx.SetReturn(HttpErrorInvalidUrl);
        }

        var components = new[]
        {
            parsed.Scheme,
            parsed.Username,
            parsed.Password,
            parsed.Hostname,
            parsed.Path,
            parsed.Query,
            parsed.Fragment,
        };
        var encoded = components.Select(Encoding.UTF8.GetBytes).ToArray();
        var requiredSize = encoded.Aggregate(0UL, (size, value) => size + (ulong)value.Length + 1);
        var writeOutput = outputAddress != 0 && poolAddress != 0;
        if (!writeOutput && requiredAddress == 0)
        {
            return ctx.SetReturn(HttpErrorInvalidValue);
        }

        if (writeOutput && preparedSize < requiredSize)
        {
            return ctx.SetReturn(HttpErrorOutOfMemory);
        }

        if (!PreflightWrite(ctx, requiredAddress, sizeof(ulong)) ||
            (writeOutput &&
             (!PreflightWrite(ctx, outputAddress, UriElementSize) ||
              !PreflightWrite(ctx, poolAddress, checked((int)requiredSize)))))
        {
            return ctx.SetReturn(HttpErrorInvalidValue);
        }

        if (requiredAddress != 0 && !ctx.TryWriteUInt64(requiredAddress, requiredSize))
        {
            return ctx.SetReturn(HttpErrorInvalidValue);
        }

        if (!writeOutput)
        {
            return ctx.SetReturn(0);
        }

        var pool = new byte[checked((int)requiredSize)];
        var offsets = new int[components.Length];
        var cursor = 0;
        for (var index = 0; index < encoded.Length; index++)
        {
            offsets[index] = cursor;
            encoded[index].CopyTo(pool.AsSpan(cursor));
            cursor += encoded[index].Length + 1;
        }

        var output = new byte[UriElementSize];
        output[0] = parsed.Opaque ? (byte)1 : (byte)0;
        for (var index = 0; index < offsets.Length; index++)
        {
            BinaryPrimitives.WriteUInt64LittleEndian(
                output.AsSpan(8 + (index * sizeof(ulong))),
                checked(poolAddress + (ulong)offsets[index]));
        }

        BinaryPrimitives.WriteUInt16LittleEndian(output.AsSpan(64), parsed.Port);
        if (!ctx.Memory.TryWrite(poolAddress, pool) ||
            !ctx.Memory.TryWrite(outputAddress, output))
        {
            return ctx.SetReturn(HttpErrorInvalidValue);
        }

        return ctx.SetReturn(0);
    }

    [SysAbiExport(
        Nid = "5LZA+KPISVA",
        ExportName = "sceHttpUriBuild",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceHttp")]
    public static int HttpUriBuild(CpuContext ctx)
    {
        var outputAddress = ctx[CpuRegister.Rdi];
        var requiredAddress = ctx[CpuRegister.Rsi];
        var preparedSize = ctx[CpuRegister.Rdx];
        var elementAddress = ctx[CpuRegister.Rcx];
        var options = unchecked((uint)ctx[CpuRegister.R8]);
        if (elementAddress == 0)
        {
            return ctx.SetReturn(HttpErrorInvalidUrl);
        }

        if (outputAddress == 0 && requiredAddress == 0)
        {
            return ctx.SetReturn(HttpErrorInvalidValue);
        }

        Span<byte> element = stackalloc byte[UriElementSize];
        if (!ctx.Memory.TryRead(elementAddress, element) ||
            !TryReadUriComponent(ctx, ReadUriPointer(element, 8), out var scheme) ||
            !TryReadUriComponent(ctx, ReadUriPointer(element, 16), out var username) ||
            !TryReadUriComponent(ctx, ReadUriPointer(element, 24), out var password) ||
            !TryReadUriComponent(ctx, ReadUriPointer(element, 32), out var hostname) ||
            !TryReadUriComponent(ctx, ReadUriPointer(element, 40), out var path) ||
            !TryReadUriComponent(ctx, ReadUriPointer(element, 48), out var query) ||
            !TryReadUriComponent(ctx, ReadUriPointer(element, 56), out var fragment))
        {
            return ctx.SetReturn(HttpErrorInvalidUrl);
        }

        var opaque = element[0] != 0;
        var port = BinaryPrimitives.ReadUInt16LittleEndian(element[64..]);
        var builder = new StringBuilder();
        if ((options & UriBuildWithScheme) != 0 && scheme.Length != 0)
        {
            builder.Append(scheme).Append(':');
        }

        if (!opaque)
        {
            builder.Append("//");
        }

        var includeUsername = (options & UriBuildWithUsername) != 0 && username.Length != 0;
        var includePassword = (options & UriBuildWithPassword) != 0 && password.Length != 0;
        if (includeUsername)
        {
            builder.Append(username);
        }
        if (includePassword)
        {
            builder.Append(':').Append(password);
        }
        if (includeUsername || includePassword)
        {
            builder.Append('@');
        }

        if ((options & UriBuildWithHostname) != 0)
        {
            builder.Append(hostname);
        }

        if ((options & UriBuildWithPort) != 0 &&
            port != 0 &&
            port != GetDefaultUriPort(scheme))
        {
            builder.Append(':').Append(port.ToString(CultureInfo.InvariantCulture));
        }

        if ((options & UriBuildWithPath) != 0)
        {
            builder.Append(path);
        }
        if ((options & UriBuildWithQuery) != 0)
        {
            builder.Append(query);
        }
        if ((options & UriBuildWithFragment) != 0)
        {
            builder.Append(fragment);
        }

        var encoded = Encoding.UTF8.GetBytes(builder.ToString());
        if (encoded.Length >= MaximumUriBytes)
        {
            return ctx.SetReturn(HttpErrorInvalidUrl);
        }

        var requiredSize = checked((ulong)encoded.Length + 1);
        var canWriteOutput = outputAddress != 0 && preparedSize >= requiredSize;
        if (!PreflightWrite(ctx, requiredAddress, sizeof(ulong)) ||
            (canWriteOutput && !PreflightWrite(ctx, outputAddress, checked((int)requiredSize))))
        {
            return ctx.SetReturn(HttpErrorInvalidValue);
        }

        if (requiredAddress != 0 && !ctx.TryWriteUInt64(requiredAddress, requiredSize))
        {
            return ctx.SetReturn(HttpErrorInvalidValue);
        }

        if (outputAddress == 0)
        {
            return ctx.SetReturn(0);
        }
        if (!canWriteOutput)
        {
            return ctx.SetReturn(HttpErrorOutOfMemory);
        }

        var output = new byte[checked((int)requiredSize)];
        encoded.CopyTo(output, 0);
        return ctx.Memory.TryWrite(outputAddress, output)
            ? ctx.SetReturn(0)
            : ctx.SetReturn(HttpErrorInvalidValue);
    }

    private static ulong ReadUriPointer(ReadOnlySpan<byte> element, int offset)
        => BinaryPrimitives.ReadUInt64LittleEndian(element[offset..]);

    private static bool TryReadUriComponent(CpuContext ctx, ulong address, out string value)
    {
        if (address == 0)
        {
            value = string.Empty;
            return true;
        }

        return ctx.TryReadNullTerminatedUtf8(address, MaximumUriBytes, out value);
    }

    private static ushort GetDefaultUriPort(string scheme)
    {
        if (string.Equals(scheme, "http", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(scheme, "ttp", StringComparison.OrdinalIgnoreCase))
        {
            return 80;
        }

        return string.Equals(scheme, "https", StringComparison.OrdinalIgnoreCase)
            ? (ushort)443
            : (ushort)0;
    }

    private static bool TryParseUri(string source, out ParsedUri parsed)
    {
        parsed = null!;
        if (string.IsNullOrEmpty(source))
        {
            return false;
        }

        var scheme = string.Empty;
        var position = 0;
        var colon = source.IndexOf(':');
        if (colon is > 0 and <= 0x20 &&
            char.IsAsciiLetter(source[0]) &&
            source.AsSpan(1, colon - 1).ToString().All(
                character => char.IsAsciiLetterOrDigit(character) || character is '+' or '-' or '.'))
        {
            scheme = source[..colon];
            position = colon + 1;
        }

        var opaque = true;
        if (source.AsSpan(position).StartsWith("//", StringComparison.Ordinal))
        {
            opaque = false;
            position += 2;
        }

        var authorityEnd = source.AsSpan(position).IndexOfAny('/', '?', '#');
        authorityEnd = authorityEnd < 0 ? source.Length : position + authorityEnd;
        var authority = source[position..authorityEnd];
        position = authorityEnd;
        var username = string.Empty;
        var password = string.Empty;
        var at = authority.IndexOf('@');
        if (at >= 0)
        {
            var userInfo = authority[..at];
            authority = authority[(at + 1)..];
            var separator = userInfo.IndexOf(':');
            username = separator < 0 ? userInfo : userInfo[..separator];
            password = separator < 0 ? string.Empty : userInfo[(separator + 1)..];
        }

        if (!TryParseAuthority(authority, out var hostname, out var explicitPort))
        {
            return false;
        }

        var fragmentStart = source.IndexOf('#', position);
        var queryStart = source.IndexOf('?', position);
        if (queryStart >= 0 && fragmentStart >= 0 && queryStart > fragmentStart)
        {
            queryStart = -1;
        }

        var pathEnd = queryStart >= 0 ? queryStart : fragmentStart >= 0 ? fragmentStart : source.Length;
        var path = SweepPath(source[position..pathEnd]);
        var query = queryStart < 0
            ? string.Empty
            : source[queryStart..(fragmentStart >= 0 ? fragmentStart : source.Length)];
        var fragment = fragmentStart < 0 ? string.Empty : source[fragmentStart..];
        var port = explicitPort ?? (scheme.Equals("https", StringComparison.OrdinalIgnoreCase)
            ? (ushort)443
            : scheme.Equals("http", StringComparison.OrdinalIgnoreCase) ? (ushort)80 : (ushort)0);
        parsed = new ParsedUri(
            opaque,
            scheme,
            username,
            password,
            hostname,
            path,
            query,
            fragment,
            port);
        return true;
    }

    private static bool TryParseAuthority(string authority, out string hostname, out ushort? port)
    {
        hostname = authority;
        port = null;
        string? portText = null;
        if (authority.StartsWith("[", StringComparison.Ordinal))
        {
            var close = authority.IndexOf(']');
            if (close < 0)
            {
                return false;
            }

            hostname = authority[1..close];
            if (close + 1 < authority.Length)
            {
                if (authority[close + 1] != ':')
                {
                    return false;
                }

                portText = authority[(close + 2)..];
            }
        }
        else
        {
            var colon = authority.LastIndexOf(':');
            if (colon >= 0)
            {
                hostname = authority[..colon];
                portText = authority[(colon + 1)..];
            }
        }

        if (portText is not null)
        {
            if (portText.Length == 0 || !ushort.TryParse(portText, out var value))
            {
                return false;
            }

            port = value;
        }

        return hostname.Length <= 0xFF;
    }

    private static string SweepPath(string path)
    {
        if (!path.Contains("/./", StringComparison.Ordinal) &&
            !path.Contains("/../", StringComparison.Ordinal) &&
            !path.EndsWith("/.", StringComparison.Ordinal) &&
            !path.EndsWith("/..", StringComparison.Ordinal))
        {
            return path;
        }

        var leadingSlash = path.StartsWith("/", StringComparison.Ordinal);
        var segments = new List<string>();
        foreach (var segment in path.Split('/'))
        {
            if (segment.Length == 0 || segment == ".")
            {
                continue;
            }

            if (segment == "..")
            {
                if (segments.Count > 0)
                {
                    segments.RemoveAt(segments.Count - 1);
                }

                continue;
            }

            segments.Add(segment);
        }

        return (leadingSlash ? "/" : string.Empty) + string.Join('/', segments);
    }

    private static bool PreflightWrite(CpuContext ctx, ulong address, int size)
    {
        if (address == 0)
        {
            return true;
        }

        var buffer = new byte[size];
        return ctx.Memory.TryRead(address, buffer);
    }

    private static void TraceHttp(string operation, int id, ulong arg0, ulong arg1, ulong arg2, ulong arg3)
    {
        if (!string.Equals(Environment.GetEnvironmentVariable("SHARPEMU_LOG_HTTP"), "1", StringComparison.Ordinal))
        {
            return;
        }

        Console.Error.WriteLine(
            $"[LOADER][TRACE] http.{operation} id={id} arg0=0x{arg0:X16} arg1=0x{arg1:X16} arg2=0x{arg2:X16} arg3=0x{arg3:X16}");
    }
}
