// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using SharpEmu.HLE;

namespace SharpEmu.Libs.Remoteplay;

/// <summary>
/// Offline Remote Play probes used by titles during controller and network
/// initialization. SharpEmu does not expose a Remote Play server, so a
/// successful disconnected state is the only supported runtime state.
/// </summary>
public static class RemoteplayExports
{
    private const int ConnectionStatusDisconnected = 0;

    [SysAbiExport(
        Nid = "k1SwgkMSOM8",
        ExportName = "sceRemoteplayInitialize",
        Target = Generation.Gen5,
        LibraryName = "libSceRemoteplay")]
    public static int RemoteplayInitialize(CpuContext ctx) =>
        ctx.SetReturn(0);

    [SysAbiExport(
        Nid = "BOwybKVa3Do",
        ExportName = "sceRemoteplayTerminate",
        Target = Generation.Gen5,
        LibraryName = "libSceRemoteplay")]
    public static int RemoteplayTerminate(CpuContext ctx) =>
        ctx.SetReturn(0);

    [SysAbiExport(
        Nid = "g3PNjYKWqnQ",
        ExportName = "sceRemoteplayGetConnectionStatus",
        Target = Generation.Gen5,
        LibraryName = "libSceRemoteplay")]
    public static int RemoteplayGetConnectionStatus(CpuContext ctx)
    {
        var statusAddress = ctx[CpuRegister.Rsi];
        if (statusAddress == 0)
        {
            return ctx.SetReturn(-1);
        }

        Span<byte> status = stackalloc byte[sizeof(int)];
        BinaryPrimitives.WriteInt32LittleEndian(
            status,
            ConnectionStatusDisconnected);
        return ctx.Memory.TryWrite(statusAddress, status)
            ? ctx.SetReturn(0)
            : ctx.SetReturn(
                (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
    }
}
