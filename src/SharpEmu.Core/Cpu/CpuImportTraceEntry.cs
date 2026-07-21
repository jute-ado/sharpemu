// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.Core.Cpu;

public sealed record CpuImportTraceEntry(
    long DispatchIndex,
    string Nid,
    string? LibraryName,
    string? ExportName,
    ulong GuestThreadHandle,
    ulong ReturnAddress,
    ulong Arg0,
    ulong Arg1,
    ulong Arg2,
    ulong Arg3,
    ulong Arg4,
    ulong Arg5,
    ulong? ReturnValue);
