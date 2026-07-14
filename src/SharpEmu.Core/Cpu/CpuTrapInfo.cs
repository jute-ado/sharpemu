// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.Core.Cpu;

public readonly struct CpuTrapInfo
{
    public CpuTrapInfo(
        ulong instructionPointer,
        byte opcode,
        uint? exceptionCode = null,
        ulong? accessAddress = null,
        CpuMemoryAccessKind? accessKind = null)
    {
        InstructionPointer = instructionPointer;
        Opcode = opcode;
        ExceptionCode = exceptionCode;
        AccessAddress = accessAddress;
        AccessKind = accessKind;
    }

    public ulong InstructionPointer { get; }

    public byte Opcode { get; }

    public uint? ExceptionCode { get; }

    public ulong? AccessAddress { get; }

    public CpuMemoryAccessKind? AccessKind { get; }
}
