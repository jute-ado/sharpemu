// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using System.Buffers.Binary;

namespace SharpEmu.Libs.Np;

public static class NpManagerExports
{
    private const int NpTitleIdSize = 16;
    private const int NpTitleSecretSize = 128;
    private const int NpErrorInvalidArgument = unchecked((int)0x80550003);
    private const int NpErrorSignedOut = unchecked((int)0x80550006);
    private const int NpErrorRequestNotFound = unchecked((int)0x80550014);
    private const int NpErrorInvalidAsyncParameterSize =
        unchecked((int)0x80550011);
    private const ulong NpAsyncParameterSize = 0x18;

    internal static void ResetRuntimeState() =>
        NpManagerRequestRegistry.Reset();

    [SysAbiExport(
        Nid = "3Zl8BePTh9Y",
        ExportName = "sceNpCheckCallback",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceNpManager")]
    public static int NpCheckCallback(CpuContext ctx)
    {
        ctx[CpuRegister.Rax] = 0;
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    [SysAbiExport(
        Nid = "GpLQDNKICac",
        ExportName = "sceNpCreateRequest",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceNpManager")]
    public static int NpCreateRequest(CpuContext ctx)
    {
        return ctx.SetReturn(NpManagerRequestRegistry.CreateSync());
    }

    [SysAbiExport(
        Nid = "eiqMCt9UshI",
        ExportName = "sceNpCreateAsyncRequest",
        Target = Generation.Gen5,
        LibraryName = "libSceNpManager")]
    public static int NpCreateAsyncRequest(CpuContext ctx)
    {
        if (!NpManagerRequestRegistry.IsInitialized)
        {
            return SetReturn(ctx, NpManagerRequestRegistry.ErrorNotInitialized);
        }

        var parameterAddress = ctx[CpuRegister.Rdi];
        if (parameterAddress == 0)
        {
            return SetReturn(ctx, NpManagerRequestRegistry.ErrorInvalidArgument);
        }

        if (!ctx.TryReadUInt64(parameterAddress, out var size))
        {
            return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }

        if (size != NpAsyncParameterSize)
        {
            return SetReturn(ctx, NpErrorInvalidAsyncParameterSize);
        }

        if (!ctx.TryReadUInt64(parameterAddress + 0x08, out var affinity) ||
            !ctx.TryReadUInt32(parameterAddress + 0x10, out var priority))
        {
            return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }

        return SetReturn(
            ctx,
            NpManagerRequestRegistry.CreateAsync(priority, affinity));
    }

    [SysAbiExport(
        Nid = "+4DegjBqV1g",
        ExportName = "sceNpGetAccountAge",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceNpManager")]
    public static int NpGetAccountAge(CpuContext ctx)
    {
        var requestId = unchecked((int)ctx[CpuRegister.Rdi]);
        var ageAddress = ctx[CpuRegister.Rdx];
        if (requestId <= 0 || ageAddress == 0)
        {
            return ctx.SetReturn(NpErrorInvalidArgument);
        }

        if (!NpManagerRequestRegistry.ContainsSync(requestId))
        {
            return ctx.SetReturn(NpErrorRequestNotFound);
        }

        Span<byte> age = stackalloc byte[1];
        return ctx.Memory.TryWrite(ageAddress, age)
            ? ctx.SetReturn(NpErrorSignedOut)
            : ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
    }

    [SysAbiExport(
        Nid = "S7QTn72PrDw",
        ExportName = "sceNpDeleteRequest",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceNpManager")]
    public static int NpDeleteRequest(CpuContext ctx)
    {
        var requestId = unchecked((int)ctx[CpuRegister.Rdi]);
        return SetReturn(ctx, NpManagerRequestRegistry.Delete(requestId));
    }

    [SysAbiExport(
        Nid = "OzKvTvg3ZYU",
        ExportName = "sceNpAbortRequest",
        Target = Generation.Gen5,
        LibraryName = "libSceNpManager")]
    public static int NpAbortRequest(CpuContext ctx)
    {
        return SetReturn(
            ctx,
            NpManagerRequestRegistry.Abort(
                unchecked((int)ctx[CpuRegister.Rdi])));
    }

    [SysAbiExport(
        Nid = "uqcPJLWL08M",
        ExportName = "sceNpPollAsync",
        Target = Generation.Gen5,
        LibraryName = "libSceNpManager")]
    public static int NpPollAsync(CpuContext ctx)
    {
        if (!NpManagerRequestRegistry.IsInitialized)
        {
            return SetReturn(ctx, NpManagerRequestRegistry.ErrorNotInitialized);
        }

        var resultAddress = ctx[CpuRegister.Rsi];
        if (resultAddress == 0)
        {
            return SetReturn(ctx, NpManagerRequestRegistry.ErrorInvalidArgument);
        }

        var pollResult = NpManagerRequestRegistry.Poll(
            unchecked((int)ctx[CpuRegister.Rdi]),
            out var completed,
            out var operationResult);
        if (pollResult != 0 || !completed)
        {
            return SetReturn(ctx, pollResult);
        }

        return ctx.TryWriteInt32(resultAddress, operationResult)
            ? SetReturn(ctx, 0)
            : ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
    }

    [SysAbiExport(
        Nid = "KfGZg2y73oM",
        ExportName = "sceNpCheckNpReachability",
        Target = Generation.Gen5,
        LibraryName = "libSceNpManager")]
    public static int NpCheckNpReachability(CpuContext ctx)
    {
        if (!NpManagerRequestRegistry.IsInitialized)
        {
            return SetReturn(ctx, NpManagerRequestRegistry.ErrorNotInitialized);
        }

        var requestId = unchecked((int)ctx[CpuRegister.Rdi]);
        var userId = unchecked((int)ctx[CpuRegister.Rsi]);
        if (requestId <= 0 || userId == -1)
        {
            return SetReturn(ctx, NpManagerRequestRegistry.ErrorInvalidArgument);
        }

        var result = NpManagerRequestRegistry.StartLocalOperation(
            requestId,
            _ => 0);
        TraceNp(
            $"check_np_reachability request={requestId} user={userId} " +
            $"result=0x{result:X8}");
        return SetReturn(ctx, result);
    }

    [SysAbiExport(
        Nid = "JELHf4xPufo",
        ExportName = "sceNpCheckCallbackForLib",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceNpManager")]
    public static int NpCheckCallbackForLib(CpuContext ctx)
    {
        ctx[CpuRegister.Rax] = 0;
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    // Offline profile: the online id payload is left untouched and the call
    // reports success, matching the other offline NpManager stubs here.
    [SysAbiExport(
        Nid = "XDncXQIJUSk",
        ExportName = "sceNpGetOnlineId",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceNpManager")]
    public static int NpGetOnlineId(CpuContext ctx)
    {
        // Gen5 ABI: user ID, then output structure.
        return WriteOfflineOnlineId(ctx, ctx[CpuRegister.Rsi]);
    }

    [SysAbiExport(
        Nid = "VfRSmPmj8Q8",
        ExportName = "sceNpRegisterStateCallback",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceNpManager")]
    public static int NpRegisterStateCallback(CpuContext ctx)
    {
        ctx[CpuRegister.Rax] = 0;
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    /// <summary>
    /// Accepts the reachability callback and never invokes it. Reachability
    /// transitions only ever fire on a real PSN connection, which an offline
    /// session does not have, so registering successfully and staying silent is
    /// the accurate emulation of a signed-out console rather than a stub.
    /// </summary>
    [SysAbiExport(
        Nid = "hw5KNqAAels",
        ExportName = "sceNpRegisterNpReachabilityStateCallback",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceNpManager")]
    public static int NpRegisterNpReachabilityStateCallback(CpuContext ctx)
    {
        ctx[CpuRegister.Rax] = 0;
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    [SysAbiExport(
        Nid = "qQJfO8HAiaY",
        ExportName = "sceNpRegisterStateCallbackA",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceNpManager")]
    public static int NpRegisterStateCallbackA(CpuContext ctx)
    {
        ctx[CpuRegister.Rax] = 0;
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    [SysAbiExport(
        Nid = "0c7HbXRKUt4",
        ExportName = "sceNpRegisterStateCallbackForToolkit",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceNpManagerForToolkit")]
    public static int NpRegisterStateCallbackForToolkit(CpuContext ctx)
    {
        ctx[CpuRegister.Rax] = 0;
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    [SysAbiExport(
        Nid = "eQH7nWPcAgc",
        ExportName = "sceNpGetState",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceNpManager")]
    public static int NpGetState(CpuContext ctx)
    {
        var stateAddress = ctx[CpuRegister.Rsi];
        if (stateAddress == 0)
        {
            return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        }

        Span<byte> stateBytes = stackalloc byte[sizeof(uint)];
        BinaryPrimitives.WriteUInt32LittleEndian(stateBytes, 1);
        return ctx.Memory.TryWrite(stateAddress, stateBytes)
            ? ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_OK)
            : ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
    }

    [SysAbiExport(
        Nid = "Oad3rvY-NJQ",
        ExportName = "sceNpHasSignedUp",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceNpManager")]
    public static int NpHasSignedUp(CpuContext ctx)
    {
        var userId = unchecked((int)ctx[CpuRegister.Rdi]);
        var resultAddress = ctx[CpuRegister.Rsi];
        if (userId == -1 || resultAddress == 0)
        {
            return ctx.SetReturn(NpErrorInvalidArgument);
        }

        // This process-local offline profile exposes a stable account and a
        // signed-in NP state. Report the same account as registered so callers
        // do not enter a contradictory sign-up flow that cannot be completed
        // without a real network service.
        Span<byte> hasSignedUp = stackalloc byte[] { 1 };
        return ctx.Memory.TryWrite(resultAddress, hasSignedUp)
            ? ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_OK)
            : ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
    }

    [SysAbiExport(
        Nid = "rbknaUjpqWo",
        ExportName = "sceNpGetAccountIdA",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceNpManager")]
    public static int NpGetAccountIdA(CpuContext ctx)
    {
        var userId = unchecked((int)ctx[CpuRegister.Rdi]);
        var accountIdAddress = ctx[CpuRegister.Rsi];
        if (userId == -1 || accountIdAddress == 0)
        {
            return SetReturn(ctx, NpErrorInvalidArgument);
        }

        // The offline profile exposed by sceNpGetState is signed in. Keep the
        // account query consistent with that state: Unity's PSN integration
        // treats SIGNED_OUT as an exceptional state and retries it every frame.
        // A stable local-only id is sufficient for titles which only use the
        // value as a profile key.
        Span<byte> accountId = stackalloc byte[sizeof(ulong)];
        BinaryPrimitives.WriteUInt64LittleEndian(accountId, 1);
        return ctx.Memory.TryWrite(accountIdAddress, accountId)
            ? SetReturn(ctx, (int)OrbisGen2Result.ORBIS_GEN2_OK)
            : SetReturn(ctx, (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
    }

    [SysAbiExport(
        Nid = "JT+t00a3TxA",
        ExportName = "sceNpGetAccountCountryA",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceNpManager")]
    public static int NpGetAccountCountryA(CpuContext ctx)
    {
        var userId = unchecked((int)ctx[CpuRegister.Rdi]);
        var countryAddress = ctx[CpuRegister.Rsi];
        if (userId == -1 || countryAddress == 0)
        {
            return SetReturn(ctx, NpErrorInvalidArgument);
        }

        Span<byte> country = stackalloc byte[4];
        country[0] = (byte)'U';
        country[1] = (byte)'S';
        country[2] = 0;
        country[3] = 0;
        return ctx.Memory.TryWrite(countryAddress, country)
            ? SetReturn(ctx, (int)OrbisGen2Result.ORBIS_GEN2_OK)
            : SetReturn(ctx, (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
    }

    [SysAbiExport(
        Nid = "e-ZuhGEoeC4",
        ExportName = "sceNpGetNpReachabilityState",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceNpManager")]
    public static int NpGetNpReachabilityState(CpuContext ctx)
    {
        var userId = unchecked((int)ctx[CpuRegister.Rdi]);
        var stateAddress = ctx[CpuRegister.Rsi];
        if (userId == -1 || stateAddress == 0)
        {
            return SetReturn(ctx, NpErrorInvalidArgument);
        }

        Span<byte> state = stackalloc byte[sizeof(uint)];
        BinaryPrimitives.WriteUInt32LittleEndian(state, 0); // Unavailable while offline.
        return ctx.Memory.TryWrite(stateAddress, state)
            ? SetReturn(ctx, (int)OrbisGen2Result.ORBIS_GEN2_OK)
            : SetReturn(ctx, (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
    }

    [SysAbiExport(
        Nid = "Ec63y59l9tw",
        ExportName = "sceNpSetNpTitleId",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceNpManager")]
    public static int NpSetNpTitleId(CpuContext ctx)
    {
        var titleIdAddress = ctx[CpuRegister.Rdi];
        var titleSecretAddress = ctx[CpuRegister.Rsi];
        if (titleIdAddress == 0 || titleSecretAddress == 0)
        {
            return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        }

        Span<byte> titleId = stackalloc byte[NpTitleIdSize];
        Span<byte> titleSecret = stackalloc byte[NpTitleSecretSize];
        if (!ctx.Memory.TryRead(titleIdAddress, titleId) ||
            !ctx.Memory.TryRead(titleSecretAddress, titleSecret))
        {
            return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }

        TraceNp($"set_np_title_id title='{ReadTitleId(titleId)}'");
        return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_OK);
    }

    private static int SetReturn(CpuContext ctx, int result)
    {
        ctx[CpuRegister.Rax] = unchecked((ulong)result);
        return result;
    }

    private static string ReadTitleId(ReadOnlySpan<byte> bytes)
    {
        var length = 0;
        while (length < 12 && length < bytes.Length && bytes[length] != 0)
        {
            length++;
        }

        return length == 0
            ? string.Empty
            : System.Text.Encoding.ASCII.GetString(bytes[..length]);
    }

    private static void TraceNp(string message)
    {
        if (!string.Equals(Environment.GetEnvironmentVariable("SHARPEMU_LOG_NP"), "1", StringComparison.Ordinal))
        {
            return;
        }

        Console.Error.WriteLine($"[LOADER][TRACE] np.{message}");
    }

    private static int WriteOfflineOnlineId(CpuContext ctx, ulong address)
    {
        if (address == 0)
        {
            return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        }

        // SceNpOnlineId is a 16-byte handle plus four trailing bytes.
        Span<byte> onlineId = stackalloc byte[20];
        "Player"u8.CopyTo(onlineId);
        return ctx.Memory.TryWrite(address, onlineId)
            ? ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_OK)
            : ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
    }
}
