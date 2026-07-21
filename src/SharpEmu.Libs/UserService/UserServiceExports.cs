// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using System.Buffers.Binary;
using System.Text;

namespace SharpEmu.Libs.UserService;

public static class UserServiceExports
{
    private const int OrbisUserServiceErrorInvalidArgument = unchecked((int)0x80960005);
    private const int OrbisUserServiceErrorNoEvent = unchecked((int)0x80960007);
    private const int OrbisUserServiceErrorNotLoggedIn = unchecked((int)0x80960009);
    private const int OrbisUserServiceErrorBufferTooShort = unchecked((int)0x8096000A);
    private const int GamePresetsSize = 40;
    private const int InvalidUserId = -1;
    private const string PrimaryUserName = "SharpEmu";
    private static int _loginEventDelivered;

    private static readonly bool _traceUserService =
        string.Equals(Environment.GetEnvironmentVariable("SHARPEMU_LOG_USER_SERVICE"), "1", StringComparison.Ordinal);

    [SysAbiExport(
        Nid = "j3YMu1MVNNo",
        ExportName = "sceUserServiceInitialize",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceUserService")]
    public static int UserServiceInitialize(CpuContext ctx)
    {
        ctx[CpuRegister.Rax] = 0;
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    [SysAbiExport(
        Nid = "CdWp0oHWGr0",
        ExportName = "sceUserServiceGetInitialUser",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceUserService")]
    public static int UserServiceGetInitialUser(CpuContext ctx)
    {
        var userIdAddress = ctx[CpuRegister.Rdi];
        if (userIdAddress == 0)
        {
            return ctx.SetReturn(OrbisUserServiceErrorInvalidArgument);
        }

        var result = ctx.TryWriteInt32(userIdAddress, EmulatedUser.PrimaryId)
            ? 0
            : (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT;
        TraceUserService($"get_initial_user out=0x{userIdAddress:X16} value={EmulatedUser.PrimaryId} result=0x{result:X8}");
        return ctx.SetReturn(result);
    }

    [SysAbiExport(
        Nid = "fPhymKNvK-A",
        ExportName = "sceUserServiceGetLoginUserIdList",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceUserService")]
    public static int UserServiceGetLoginUserIdList(CpuContext ctx)
    {
        var userIdListAddress = ctx[CpuRegister.Rdi];
        if (userIdListAddress == 0)
        {
            return ctx.SetReturn(OrbisUserServiceErrorInvalidArgument);
        }

        Span<byte> userIds = stackalloc byte[sizeof(int) * 4];
        BinaryPrimitives.WriteInt32LittleEndian(userIds[0x00..], EmulatedUser.PrimaryId);
        BinaryPrimitives.WriteInt32LittleEndian(userIds[0x04..], InvalidUserId);
        BinaryPrimitives.WriteInt32LittleEndian(userIds[0x08..], InvalidUserId);
        BinaryPrimitives.WriteInt32LittleEndian(userIds[0x0C..], InvalidUserId);
        return ctx.Memory.TryWrite(userIdListAddress, userIds)
            ? ctx.SetReturn(0)
            : ctx.SetReturn((int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
    }

    [SysAbiExport(
        Nid = "yH17Q6NWtVg",
        ExportName = "sceUserServiceGetEvent",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceUserService")]
    public static int UserServiceGetEvent(CpuContext ctx)
    {
        var eventAddress = ctx[CpuRegister.Rdi];
        if (eventAddress == 0)
        {
            return ctx.SetReturn(OrbisUserServiceErrorInvalidArgument);
        }

        if (Interlocked.Exchange(ref _loginEventDelivered, 1) != 0)
        {
            return ctx.SetReturn(OrbisUserServiceErrorNoEvent);
        }

        Span<byte> payload = stackalloc byte[sizeof(int) * 2];
        BinaryPrimitives.WriteInt32LittleEndian(payload[0..], 0);
        BinaryPrimitives.WriteInt32LittleEndian(payload[sizeof(int)..], EmulatedUser.PrimaryId);
        return ctx.Memory.TryWrite(eventAddress, payload)
            ? ctx.SetReturn(0)
            : ctx.SetReturn((int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
    }

    [SysAbiExport(
        Nid = "1xxcMiGu2fo",
        ExportName = "sceUserServiceGetUserName",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceUserService")]
    public static int UserServiceGetUserName(CpuContext ctx)
    {
        var userId = unchecked((int)ctx[CpuRegister.Rdi]);
        var nameAddress = ctx[CpuRegister.Rsi];
        var capacity = ctx[CpuRegister.Rdx];
        // Zero selects the current user.
        if (userId != 0 && userId != EmulatedUser.PrimaryId)
        {
            return ctx.SetReturn(OrbisUserServiceErrorNotLoggedIn);
        }

        if (nameAddress == 0)
        {
            return ctx.SetReturn(OrbisUserServiceErrorInvalidArgument);
        }

        var nameBytes = Encoding.UTF8.GetBytes(PrimaryUserName);
        if (capacity <= (ulong)nameBytes.Length)
        {
            return ctx.SetReturn(OrbisUserServiceErrorBufferTooShort);
        }

        Span<byte> output = stackalloc byte[nameBytes.Length + 1];
        nameBytes.CopyTo(output);
        var result = ctx.Memory.TryWrite(nameAddress, output)
            ? 0
            : (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT;
        TraceUserService(
            $"get_user_name user={userId} out=0x{nameAddress:X16} capacity=0x{capacity:X} value='{PrimaryUserName}' result=0x{result:X8}");
        return ctx.SetReturn(result);
    }

    [SysAbiExport(
        Nid = "woNpu+45RLk",
        ExportName = "sceUserServiceGetAgeLevel",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceUserService")]
    public static int UserServiceGetAgeLevel(CpuContext ctx)
    {
        var userId = unchecked((int)ctx[CpuRegister.Rdi]);
        var ageLevelAddress = ctx[CpuRegister.Rsi];
        if (userId != EmulatedUser.PrimaryId)
        {
            return ctx.SetReturn(OrbisUserServiceErrorNotLoggedIn);
        }

        if (ageLevelAddress == 0)
        {
            return ctx.SetReturn(OrbisUserServiceErrorInvalidArgument);
        }

        var result = ctx.TryWriteInt32(ageLevelAddress, 18)
            ? 0
            : (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT;
        TraceUserService(
            $"get_age_level user={userId} out=0x{ageLevelAddress:X16} value=18 result=0x{result:X8}");
        return ctx.SetReturn(result);
    }

    [SysAbiExport(
        Nid = "-sD02mFDBh4",
        ExportName = "sceUserServiceGetGamePresets",
        Target = Generation.Gen5,
        LibraryName = "libSceUserService")]
    public static int UserServiceGetGamePresets(CpuContext ctx)
    {
        var userId = unchecked((int)ctx[CpuRegister.Rdi]);
        var presetsAddress = ctx[CpuRegister.Rsi];
        if (userId != EmulatedUser.PrimaryId)
        {
            return ctx.SetReturn(OrbisUserServiceErrorNotLoggedIn);
        }

        if (presetsAddress == 0)
        {
            return ctx.SetReturn(OrbisUserServiceErrorInvalidArgument);
        }

        Span<byte> header = stackalloc byte[sizeof(ulong)];
        if (!ctx.Memory.TryRead(presetsAddress, header))
        {
            return ctx.SetReturn((int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }

        var callerSize = BinaryPrimitives.ReadUInt64LittleEndian(header);
        var initializedSize = callerSize == 0 || callerSize > GamePresetsSize
            ? GamePresetsSize
            : (int)callerSize;
        initializedSize = Math.Max(initializedSize, sizeof(ulong));

        Span<byte> defaults = stackalloc byte[GamePresetsSize];
        defaults.Clear();
        BinaryPrimitives.WriteUInt64LittleEndian(defaults, GamePresetsSize);
        if (!ctx.Memory.TryWrite(presetsAddress, defaults[..initializedSize]))
        {
            return ctx.SetReturn((int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }

        TraceUserService(
            $"get_game_presets user={userId} out=0x{presetsAddress:X16} " +
            $"caller_size={callerSize} initialized_size={initializedSize} result=0x00000000");
        return ctx.SetReturn(0);
    }

    [SysAbiExport(
        Nid = "rnEhHqG-4xo",
        ExportName = "sceUserServiceGetAccessibilityChatTranscription",
        Target = Generation.Gen5,
        LibraryName = "libSceUserService")]
    public static int UserServiceGetAccessibilityChatTranscription(CpuContext ctx) =>
        GetAccessibilitySetting(ctx, 0, "chat_transcription");

    [SysAbiExport(
        Nid = "ZKJtxdgvzwg",
        ExportName = "sceUserServiceGetAccessibilityPressAndHoldDelay",
        Target = Generation.Gen5,
        LibraryName = "libSceUserService")]
    public static int UserServiceGetAccessibilityPressAndHoldDelay(CpuContext ctx) =>
        GetAccessibilitySetting(ctx, 0, "press_and_hold_delay");

    [SysAbiExport(
        Nid = "qWYHOFwqCxY",
        ExportName = "sceUserServiceGetAccessibilityVibration",
        Target = Generation.Gen5,
        LibraryName = "libSceUserService")]
    public static int UserServiceGetAccessibilityVibration(CpuContext ctx) =>
        GetAccessibilitySetting(ctx, 1, "vibration");

    [SysAbiExport(
        Nid = "-3Y5GO+-i78",
        ExportName = "sceUserServiceGetAccessibilityTriggerEffect",
        Target = Generation.Gen5,
        LibraryName = "libSceUserService")]
    public static int UserServiceGetAccessibilityTriggerEffect(CpuContext ctx) =>
        GetAccessibilitySetting(ctx, 1, "trigger_effect");

    [SysAbiExport(
        Nid = "hD-H81EN9Vg",
        ExportName = "sceUserServiceGetAccessibilityZoomEnabled",
        Target = Generation.Gen5,
        LibraryName = "libSceUserService")]
    public static int UserServiceGetAccessibilityZoomEnabled(CpuContext ctx) =>
        GetAccessibilitySetting(ctx, 0, "zoom_enabled");

    [SysAbiExport(
        Nid = "O6IW1-Dwm-w",
        ExportName = "sceUserServiceGetAccessibilityZoomFollowFocus",
        Target = Generation.Gen5,
        LibraryName = "libSceUserService")]
    public static int UserServiceGetAccessibilityZoomFollowFocus(CpuContext ctx) =>
        GetAccessibilitySetting(ctx, 0, "zoom_follow_focus");

    [SysAbiExport(
        Nid = "D-CzAxQL0XI",
        ExportName = "sceUserServiceGetPlatformPrivacySetting",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceUserService")]
    public static int UserServiceGetPlatformPrivacySetting(CpuContext ctx)
    {
        var parameterId = unchecked((int)ctx[CpuRegister.Rdi]);
        var valueAddress = ctx[CpuRegister.Rsi];
        if (parameterId != 1000)
        {
            return ctx.SetReturn(OrbisUserServiceErrorNotLoggedIn);
        }

        if (valueAddress == 0)
        {
            return ctx.SetReturn(OrbisUserServiceErrorInvalidArgument);
        }

        return ctx.TryWriteInt32(valueAddress, 0)
            ? ctx.SetReturn(0)
            : ctx.SetReturn((int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
    }

    private static int GetAccessibilitySetting(CpuContext ctx, int value, string settingName)
    {
        var userId = unchecked((int)ctx[CpuRegister.Rdi]);
        var valueAddress = ctx[CpuRegister.Rsi];
        if (valueAddress == 0)
        {
            return ctx.SetReturn(OrbisUserServiceErrorInvalidArgument);
        }

        if (userId != EmulatedUser.PrimaryId)
        {
            return ctx.SetReturn(OrbisUserServiceErrorNotLoggedIn);
        }

        var result = ctx.TryWriteInt32(valueAddress, value)
            ? 0
            : (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT;
        TraceUserService(
            $"get_accessibility_{settingName} user={userId} out=0x{valueAddress:X16} " +
            $"value={value} result=0x{result:X8}");
        return ctx.SetReturn(result);
    }

    private static void TraceUserService(string message)
    {
        if (_traceUserService)
        {
            Console.Error.WriteLine($"[LOADER][TRACE] user_service.{message}");
        }
    }
}
