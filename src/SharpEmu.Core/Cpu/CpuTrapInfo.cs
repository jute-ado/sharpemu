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
        CpuMemoryAccessKind? accessKind = null,
        string? instructionBytes = null,
        int? instructionLength = null,
        string? instructionMnemonic = null,
        string? instructionText = null,
        string? instructionFlowControl = null,
        CpuRegisterSnapshot? registers = null,
        IReadOnlyList<CpuStackFrame>? stackFrames = null,
        ulong guestThreadHandle = 0)
    {
        InstructionPointer = instructionPointer;
        Opcode = opcode;
        ExceptionCode = exceptionCode;
        AccessAddress = accessAddress;
        AccessKind = accessKind;
        InstructionBytes = instructionBytes;
        InstructionLength = instructionLength;
        InstructionMnemonic = instructionMnemonic;
        InstructionText = instructionText;
        InstructionFlowControl = instructionFlowControl;
        Registers = registers;
        StackFrames = stackFrames;
        GuestThreadHandle = guestThreadHandle;
    }

    public ulong InstructionPointer { get; }

    public byte Opcode { get; }

    public uint? ExceptionCode { get; }

    public ulong? AccessAddress { get; }

    public CpuMemoryAccessKind? AccessKind { get; }

    public string? InstructionBytes { get; }

    public int? InstructionLength { get; }

    public string? InstructionMnemonic { get; }

    public string? InstructionText { get; }

    public string? InstructionFlowControl { get; }

    public CpuRegisterSnapshot? Registers { get; }

    public IReadOnlyList<CpuStackFrame>? StackFrames { get; }

    public ulong GuestThreadHandle { get; }

    public CpuTrapInfo WithDecodedInstruction(
        string instructionBytes,
        int instructionLength,
        string instructionMnemonic,
        string instructionText,
        string instructionFlowControl) =>
        new(
            InstructionPointer,
            Opcode,
            ExceptionCode,
            AccessAddress,
            AccessKind,
            instructionBytes,
            instructionLength,
            instructionMnemonic,
            instructionText,
            instructionFlowControl,
            Registers,
            StackFrames,
            GuestThreadHandle);

    public CpuTrapInfo WithStackFrames(IReadOnlyList<CpuStackFrame> stackFrames) =>
        new(
            InstructionPointer,
            Opcode,
            ExceptionCode,
            AccessAddress,
            AccessKind,
            InstructionBytes,
            InstructionLength,
            InstructionMnemonic,
            InstructionText,
            InstructionFlowControl,
            Registers,
            stackFrames,
            GuestThreadHandle);
}
