// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;

namespace SharpEmu.Libs.Np;

public static class NpWebApi2Exports
{
    private const int NpWebApi2ErrorInvalidArgument = unchecked((int)0x80553402);

    private static int _initialized;
    private static int _nextLibraryContextHandle;
    private static int _nextPushEventHandle;
    private static int _nextPushFilterId;
    private static int _nextUserContextHandle = 1000;
    private static readonly object _contextGate = new();
    private static readonly HashSet<int> _libraryContexts = [];
    private static readonly Dictionary<int, int> _pushEventHandleOwners = [];
    private static readonly Dictionary<int, (int LibraryContextId, int HandleId)> _pushFilterOwners = [];
    private static readonly Dictionary<int, int> _userContextOwners = [];

    [SysAbiExport(
        Nid = "+o9816YQhqQ",
        ExportName = "sceNpWebApi2Initialize",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceNpWebApi2")]
    public static int NpWebApi2Initialize(CpuContext ctx)
    {
        var httpContextId = unchecked((int)ctx[CpuRegister.Rdi]);
        var poolSize = ctx[CpuRegister.Rsi];

        if (httpContextId <= 0 || poolSize == 0)
        {
            return ctx.SetReturn(NpWebApi2ErrorInvalidArgument);
        }

        var libraryContextId = CreateLibraryContextId();
        Interlocked.Exchange(ref _initialized, 1);
        TraceNpWebApi2("init", httpContextId, poolSize);
        return ctx.SetReturn(libraryContextId);
    }

    [SysAbiExport(
        Nid = "WV1GwM32NgY",
        ExportName = "sceNpWebApi2PushEventCreateHandle",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceNpWebApi2")]
    public static int NpWebApi2InitializeAlt(CpuContext ctx)
    {
        var libraryContextId = unchecked((int)ctx[CpuRegister.Rdi]);
        if (!IsValidLibraryContextId(libraryContextId))
        {
            return ctx.SetReturn(NpWebApi2ErrorInvalidArgument);
        }

        var handle = CreatePushEventHandle(libraryContextId);
        Interlocked.Exchange(ref _initialized, 1);
        TraceNpWebApi2("init-alt", libraryContextId, 0);
        return ctx.SetReturn(handle);
    }

    [SysAbiExport(
        Nid = "MsaFhR+lPE4",
        ExportName = "sceNpWebApi2PushEventCreateFilter",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceNpWebApi2")]
    public static int NpWebApi2PushEventCreateFilter(CpuContext ctx)
    {
        var libraryContextId = unchecked((int)ctx[CpuRegister.Rdi]);
        var handleId = unchecked((int)ctx[CpuRegister.Rsi]);
        if (!OwnsPushEventHandle(libraryContextId, handleId))
        {
            return ctx.SetReturn(NpWebApi2ErrorInvalidArgument);
        }

        var filterId = Interlocked.Increment(ref _nextPushFilterId);
        lock (_contextGate)
        {
            _pushFilterOwners.Add(filterId, (libraryContextId, handleId));
        }

        TraceNpWebApi2("push-filter-create", filterId, unchecked((uint)handleId));
        return ctx.SetReturn(filterId);
    }

    [SysAbiExport(
        Nid = "fY3QqeNkF8k",
        ExportName = "sceNpWebApi2PushEventRegisterCallback",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceNpWebApi2")]
    public static int NpWebApi2PushEventRegisterCallback(CpuContext ctx)
    {
        var userContextId = unchecked((int)ctx[CpuRegister.Rdi]);
        var filterId = unchecked((int)ctx[CpuRegister.Rsi]);
        lock (_contextGate)
        {
            if (!_userContextOwners.TryGetValue(userContextId, out var userOwner) ||
                !_pushFilterOwners.TryGetValue(filterId, out var filterOwner) ||
                userOwner != filterOwner.LibraryContextId)
            {
                return ctx.SetReturn(NpWebApi2ErrorInvalidArgument);
            }
        }

        TraceNpWebApi2("push-callback-register", filterId, unchecked((uint)userContextId));
        return ctx.SetReturn(0);
    }

    [SysAbiExport(
        Nid = "fIATVMo4Y1w",
        ExportName = "sceNpWebApi2PushEventDeleteHandle",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceNpWebApi2")]
    public static int NpWebApi2PushEventDeleteHandle(CpuContext ctx)
    {
        var libraryContextId = unchecked((int)ctx[CpuRegister.Rdi]);
        var handleId = unchecked((int)ctx[CpuRegister.Rsi]);
        lock (_contextGate)
        {
            if (!_pushEventHandleOwners.TryGetValue(handleId, out var owner) ||
                owner != libraryContextId)
            {
                return ctx.SetReturn(NpWebApi2ErrorInvalidArgument);
            }

            _pushEventHandleOwners.Remove(handleId);
            foreach (var filter in _pushFilterOwners
                         .Where(pair => pair.Value.HandleId == handleId)
                         .Select(pair => pair.Key)
                         .ToArray())
            {
                _pushFilterOwners.Remove(filter);
            }
        }

        TraceNpWebApi2("push-handle-delete", handleId, unchecked((uint)libraryContextId));
        return ctx.SetReturn(0);
    }

    [SysAbiExport(
        Nid = "sk54bi6FtYM",
        ExportName = "sceNpWebApi2CreateUserContext",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceNpWebApi2")]
    public static int NpWebApi2CreateUserContext(CpuContext ctx)
    {
        var libraryContextId = unchecked((int)ctx[CpuRegister.Rdi]);
        var userId = unchecked((int)ctx[CpuRegister.Rsi]);

        TraceNpWebApi2(
            "create-user-context",
            libraryContextId,
            unchecked((uint)userId));

        if (Volatile.Read(ref _initialized) == 0 ||
            !IsValidLibraryContextId(libraryContextId) ||
            userId == -1)
        {
            return ctx.SetReturn(NpWebApi2ErrorInvalidArgument);
        }

        var userContextId = Interlocked.Increment(ref _nextUserContextHandle);
        lock (_contextGate)
        {
            _userContextOwners.Add(userContextId, libraryContextId);
        }

        return ctx.SetReturn(userContextId);
    }

    [SysAbiExport(
        Nid = "bEvXpcEk200",
        ExportName = "sceNpWebApi2Terminate",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceNpWebApi2")]
    public static int NpWebApi2Terminate(CpuContext ctx)
    {
        var libraryContextId = unchecked((int)ctx[CpuRegister.Rdi]);
        if (!IsValidLibraryContextId(libraryContextId))
        {
            return ctx.SetReturn(NpWebApi2ErrorInvalidArgument);
        }

        RemoveLibraryContextId(libraryContextId);
        TraceNpWebApi2("term", libraryContextId, 0);
        return ctx.SetReturn(0);
    }

    private static int CreateLibraryContextId()
    {
        var handle = Interlocked.Increment(ref _nextLibraryContextHandle);
        lock (_contextGate)
        {
            _libraryContexts.Add(handle);
        }

        return handle;
    }

    private static int CreatePushEventHandle(int libraryContextId)
    {
        var handle = Interlocked.Increment(ref _nextPushEventHandle);
        lock (_contextGate)
        {
            _pushEventHandleOwners.Add(handle, libraryContextId);
        }

        return handle;
    }

    private static bool OwnsPushEventHandle(int libraryContextId, int handleId)
    {
        lock (_contextGate)
        {
            return _libraryContexts.Contains(libraryContextId) &&
                   _pushEventHandleOwners.TryGetValue(handleId, out var owner) &&
                   owner == libraryContextId;
        }
    }

    private static bool IsValidLibraryContextId(int libraryContextId)
    {
        if (libraryContextId <= 0 || libraryContextId >= 0x8000)
        {
            return false;
        }

        lock (_contextGate)
        {
            return _libraryContexts.Contains(libraryContextId);
        }
    }

    private static void RemoveLibraryContextId(int libraryContextId)
    {
        lock (_contextGate)
        {
            _libraryContexts.Remove(libraryContextId);
            foreach (var handle in _pushEventHandleOwners
                         .Where(pair => pair.Value == libraryContextId)
                         .Select(pair => pair.Key)
                         .ToArray())
            {
                _pushEventHandleOwners.Remove(handle);
            }

            foreach (var filter in _pushFilterOwners
                         .Where(pair => pair.Value.LibraryContextId == libraryContextId)
                         .Select(pair => pair.Key)
                         .ToArray())
            {
                _pushFilterOwners.Remove(filter);
            }

            foreach (var userContext in _userContextOwners
                         .Where(pair => pair.Value == libraryContextId)
                         .Select(pair => pair.Key)
                         .ToArray())
            {
                _userContextOwners.Remove(userContext);
            }

            if (_libraryContexts.Count == 0)
            {
                Interlocked.Exchange(ref _initialized, 0);
            }
        }
    }

    private static void TraceNpWebApi2(string operation, int id, ulong arg0)
    {
        if (!string.Equals(Environment.GetEnvironmentVariable("SHARPEMU_LOG_NP_WEB_API2"), "1", StringComparison.Ordinal))
        {
            return;
        }

        Console.Error.WriteLine(
            $"[LOADER][TRACE] npwebapi2.{operation} id={id} arg0=0x{arg0:X16} initialized={Volatile.Read(ref _initialized)}");
    }
}
