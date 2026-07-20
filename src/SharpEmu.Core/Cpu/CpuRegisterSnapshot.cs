// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.Core.Cpu;

public sealed record CpuRegisterSnapshot(
    ulong Rax,
    ulong Rbx,
    ulong Rcx,
    ulong Rdx,
    ulong Rsi,
    ulong Rdi,
    ulong Rbp,
    ulong Rsp,
    ulong R8,
    ulong R9,
    ulong R10,
    ulong R11,
    ulong R12,
    ulong R13,
    ulong R14,
    ulong R15);
