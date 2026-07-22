// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;

namespace SharpEmu.Libs.Np;

public static class NpAuthExports
{
    private const int InvalidArgument = unchecked((int)0x80550301);
    private const int InvalidSize = unchecked((int)0x80550302);
    private const int RequestMaximum = unchecked((int)0x80550305);
    private const int RequestNotFound = unchecked((int)0x80550306);
    private const int InvalidId = unchecked((int)0x80550307);
    private const int SignedOut = unchecked((int)0x80550006);
    private const int RequestIdOffset = 0x1000_0000;
    private const int RequestLimit = 16;
    private const ulong AsyncParameterSize = 24;

    private static readonly object _gate = new();
    private static readonly Dictionary<int, AuthRequest> _activeRequests = [];

    private sealed class AuthRequest
    {
        public bool Complete { get; set; }
        public int Result { get; set; }
    }

    internal static void ResetRuntimeState()
    {
        lock (_gate)
        {
            _activeRequests.Clear();
        }
    }

    [SysAbiExport(
        Nid = "N+mr7GjTvr8",
        ExportName = "sceNpAuthCreateAsyncRequest",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceNpAuth")]
    public static int NpAuthCreateAsyncRequest(CpuContext ctx)
    {
        var parameterAddress = ctx[CpuRegister.Rdi];
        if (parameterAddress == 0 || !ctx.TryReadUInt64(parameterAddress, out var size))
        {
            return ctx.SetReturn(InvalidArgument);
        }

        if (size != AsyncParameterSize)
        {
            return ctx.SetReturn(InvalidSize);
        }

        lock (_gate)
        {
            if (_activeRequests.Count >= RequestLimit)
            {
                return ctx.SetReturn(RequestMaximum);
            }

            for (var slot = 1; slot <= RequestLimit; slot++)
            {
                var requestId = RequestIdOffset + slot;
                if (!_activeRequests.ContainsKey(requestId))
                {
                    _activeRequests.Add(requestId, new AuthRequest());
                    return ctx.SetReturn(requestId);
                }
            }
        }

        return ctx.SetReturn(RequestMaximum);
    }

    [SysAbiExport(
        Nid = "H8wG9Bk-nPc",
        ExportName = "sceNpAuthDeleteRequest",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceNpAuth")]
    public static int NpAuthDeleteRequest(CpuContext ctx)
    {
        lock (_gate)
        {
            return _activeRequests.Remove(unchecked((int)ctx[CpuRegister.Rdi]))
                ? ctx.SetReturn(0)
                : ctx.SetReturn(RequestNotFound);
        }
    }

    [SysAbiExport(
        Nid = "KI4dHLlTNl0",
        ExportName = "sceNpAuthGetAuthorizationCodeV3",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceNpAuth")]
    public static int NpAuthGetAuthorizationCodeV3(CpuContext ctx)
    {
        var requestId = unchecked((int)ctx[CpuRegister.Rdi]);
        var parameterAddress = ctx[CpuRegister.Rsi];
        var authCodeAddress = ctx[CpuRegister.Rdx];
        if (parameterAddress == 0 || authCodeAddress == 0 ||
            !ctx.TryReadUInt64(parameterAddress, out var size) ||
            !ctx.TryReadInt32(parameterAddress + 8, out var userId) ||
            !ctx.TryReadUInt64(parameterAddress + 16, out var clientIdAddress) ||
            !ctx.TryReadUInt64(parameterAddress + 24, out var scopeAddress) ||
            !ctx.TryReadByte(authCodeAddress, out _))
        {
            return ctx.SetReturn(InvalidArgument);
        }

        if (size != 32)
        {
            return ctx.SetReturn(InvalidSize);
        }

        if (userId == -1 || clientIdAddress == 0 || scopeAddress == 0)
        {
            return ctx.SetReturn(InvalidArgument);
        }

        lock (_gate)
        {
            if (!_activeRequests.TryGetValue(requestId, out var request))
            {
                return ctx.SetReturn(RequestNotFound);
            }

            if (request.Complete)
            {
                return ctx.SetReturn(InvalidArgument);
            }

            request.Complete = true;
            request.Result = SignedOut;
        }

        return ctx.SetReturn(0);
    }

    [SysAbiExport(
        Nid = "gjSyfzSsDcE",
        ExportName = "sceNpAuthPollAsync",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceNpAuth")]
    public static int NpAuthPollAsync(CpuContext ctx) => CompleteAsyncWait(ctx);

    [SysAbiExport(
        Nid = "SK-S7daqJSE",
        ExportName = "sceNpAuthWaitAsync",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceNpAuth")]
    public static int NpAuthWaitAsync(CpuContext ctx) => CompleteAsyncWait(ctx);

    private static int CompleteAsyncWait(CpuContext ctx)
    {
        var resultAddress = ctx[CpuRegister.Rsi];
        if (resultAddress == 0 || !ctx.TryReadInt32(resultAddress, out _))
        {
            return ctx.SetReturn(InvalidArgument);
        }

        int result;
        lock (_gate)
        {
            if (!_activeRequests.TryGetValue(unchecked((int)ctx[CpuRegister.Rdi]), out var request))
            {
                return ctx.SetReturn(RequestNotFound);
            }

            if (!request.Complete)
            {
                return ctx.SetReturn(InvalidId);
            }

            result = request.Result;
        }

        return ctx.TryWriteInt32(resultAddress, result)
            ? ctx.SetReturn(0)
            : ctx.SetReturn(InvalidArgument);
    }
}
