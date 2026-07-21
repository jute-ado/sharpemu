// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.Core.Cpu;

public readonly record struct CpuDecodedInstruction(
    ulong Address,
    int Length,
    string Bytes,
    string Mnemonic,
    string Text,
    string FlowControl,
    ulong? NearBranchTarget,
    ulong? MemoryAddress);
