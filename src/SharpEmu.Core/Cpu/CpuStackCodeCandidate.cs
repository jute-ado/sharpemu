// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.Core.Cpu;

public readonly record struct CpuStackCodeCandidate(
    int StackOffset,
    ulong Address,
    CpuCodeWindow? CodeWindow = null,
    IReadOnlyList<CpuDecodedInstruction>? Instructions = null,
    CpuDecodedInstruction? PrecedingCall = null,
    CpuCodePath? PrecedingCallTarget = null);
