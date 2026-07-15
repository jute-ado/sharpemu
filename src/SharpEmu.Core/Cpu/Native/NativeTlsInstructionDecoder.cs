// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using Iced.Intel;

namespace SharpEmu.Core.Cpu.Native;

internal enum NativeTlsInstructionKind
{
    Load,
    RegisterStore,
    ImmediateStore,
    StackCanaryXor,
    StackCanarySub,
}

internal readonly record struct NativeTlsInstruction(
    NativeTlsInstructionKind Kind,
    int Length,
    int Register,
    int Displacement,
    int ImmediateValue,
    bool Is64Bit,
    int MemorySize,
    bool SignExtend);

internal static class NativeTlsInstructionDecoder
{
    private const int MaxInstructionLength = 15;

    public static bool TryDecode(
        ReadOnlySpan<byte> bytes,
        out NativeTlsInstruction tlsInstruction)
    {
        tlsInstruction = default;
        if (bytes.IsEmpty)
        {
            return false;
        }

        var decodeBytes = bytes[..Math.Min(bytes.Length, MaxInstructionLength)].ToArray();
        try
        {
            var decoder = Decoder.Create(64, new ByteArrayCodeReader(decodeBytes));
            decoder.Decode(out var instruction);
            if (instruction.Code == Code.INVALID ||
                instruction.Length <= 0 ||
                instruction.Length > decodeBytes.Length ||
                instruction.SegmentPrefix != Register.FS ||
                instruction.MemoryBase != Register.None ||
                instruction.MemoryIndex != Register.None ||
                instruction.MemoryDisplSize != sizeof(long) ||
                HasAddressSizeOverride(decodeBytes.AsSpan(0, instruction.Length)))
            {
                return false;
            }

            var displacement = unchecked((int)instruction.MemoryDisplacement64);
            switch (instruction.Code)
            {
                case Code.Mov_r32_rm32:
                case Code.Mov_r64_rm64:
                    return TryCreateRegisterInstruction(
                        in instruction,
                        NativeTlsInstructionKind.Load,
                        instruction.Op0Register,
                        displacement,
                        instruction.Code == Code.Mov_r64_rm64,
                        instruction.Code == Code.Mov_r64_rm64 ? 8 : 4,
                        signExtend: false,
                        out tlsInstruction);

                case Code.Mov_rm32_r32:
                case Code.Mov_rm64_r64:
                    return TryCreateRegisterInstruction(
                        in instruction,
                        NativeTlsInstructionKind.RegisterStore,
                        instruction.Op1Register,
                        displacement,
                        instruction.Code == Code.Mov_rm64_r64,
                        instruction.Code == Code.Mov_rm64_r64 ? 8 : 4,
                        signExtend: false,
                        out tlsInstruction);

                case Code.Movzx_r32_rm8:
                case Code.Movzx_r64_rm8:
                case Code.Movzx_r32_rm16:
                case Code.Movzx_r64_rm16:
                    return TryCreateRegisterInstruction(
                        in instruction,
                        NativeTlsInstructionKind.Load,
                        instruction.Op0Register,
                        displacement,
                        instruction.Code is Code.Movzx_r64_rm8 or Code.Movzx_r64_rm16,
                        instruction.Code is Code.Movzx_r32_rm8 or Code.Movzx_r64_rm8 ? 1 : 2,
                        signExtend: false,
                        out tlsInstruction);

                case Code.Movsx_r32_rm8:
                case Code.Movsx_r64_rm8:
                case Code.Movsx_r32_rm16:
                case Code.Movsx_r64_rm16:
                    return TryCreateRegisterInstruction(
                        in instruction,
                        NativeTlsInstructionKind.Load,
                        instruction.Op0Register,
                        displacement,
                        instruction.Code is Code.Movsx_r64_rm8 or Code.Movsx_r64_rm16,
                        instruction.Code is Code.Movsx_r32_rm8 or Code.Movsx_r64_rm8 ? 1 : 2,
                        signExtend: true,
                        out tlsInstruction);

                case Code.Movsxd_r64_rm32:
                    return TryCreateRegisterInstruction(
                        in instruction,
                        NativeTlsInstructionKind.Load,
                        instruction.Op0Register,
                        displacement,
                        is64Bit: true,
                        memorySize: 4,
                        signExtend: true,
                        out tlsInstruction);

                case Code.Mov_rm32_imm32:
                    tlsInstruction = new NativeTlsInstruction(
                        NativeTlsInstructionKind.ImmediateStore,
                        instruction.Length,
                        Register: 0,
                        displacement,
                        unchecked((int)instruction.Immediate32),
                        Is64Bit: false,
                        MemorySize: 4,
                        SignExtend: false);
                    return true;

                case Code.Mov_rm64_imm32:
                    tlsInstruction = new NativeTlsInstruction(
                        NativeTlsInstructionKind.ImmediateStore,
                        instruction.Length,
                        Register: 0,
                        displacement,
                        unchecked((int)instruction.Immediate32to64),
                        Is64Bit: true,
                        MemorySize: 8,
                        SignExtend: false);
                    return true;

                case Code.Xor_r32_rm32:
                case Code.Xor_r64_rm64:
                    if (displacement != 0x28)
                    {
                        return false;
                    }

                    return TryCreateRegisterInstruction(
                        in instruction,
                        NativeTlsInstructionKind.StackCanaryXor,
                        instruction.Op0Register,
                        displacement,
                        instruction.Code == Code.Xor_r64_rm64,
                        instruction.Code == Code.Xor_r64_rm64 ? 8 : 4,
                        signExtend: false,
                        out tlsInstruction);

                case Code.Sub_r32_rm32:
                case Code.Sub_r64_rm64:
                    if (displacement != 0x28)
                    {
                        return false;
                    }

                    return TryCreateRegisterInstruction(
                        in instruction,
                        NativeTlsInstructionKind.StackCanarySub,
                        instruction.Op0Register,
                        displacement,
                        instruction.Code == Code.Sub_r64_rm64,
                        instruction.Code == Code.Sub_r64_rm64 ? 8 : 4,
                        signExtend: false,
                        out tlsInstruction);

                default:
                    return false;
            }
        }
        catch
        {
            return false;
        }
    }

    private static bool TryCreateRegisterInstruction(
        in Instruction instruction,
        NativeTlsInstructionKind kind,
        Register register,
        int displacement,
        bool is64Bit,
        int memorySize,
        bool signExtend,
        out NativeTlsInstruction tlsInstruction)
    {
        var expectedBase = is64Bit ? Register.RAX : Register.EAX;
        if (register.GetBaseRegister() != expectedBase)
        {
            tlsInstruction = default;
            return false;
        }

        tlsInstruction = new NativeTlsInstruction(
            kind,
            instruction.Length,
            register.GetNumber(),
            displacement,
            ImmediateValue: 0,
            is64Bit,
            memorySize,
            signExtend);
        return true;
    }

    private static bool HasAddressSizeOverride(ReadOnlySpan<byte> instructionBytes)
    {
        foreach (var value in instructionBytes)
        {
            if (value == 0x67)
            {
                return true;
            }

            if (!IsLegacyPrefix(value) && value is not (>= 0x40 and <= 0x4F))
            {
                return false;
            }
        }

        return false;
    }

    private static bool IsLegacyPrefix(byte value)
    {
        return value is
            0xF0 or 0xF2 or 0xF3 or
            0x2E or 0x36 or 0x3E or 0x26 or 0x64 or 0x65 or
            0x66 or 0x67;
    }
}
