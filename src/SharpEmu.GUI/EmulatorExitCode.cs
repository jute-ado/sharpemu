// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.GUI;

public static class EmulatorExitCode
{
    public static string Describe(int exitCode)
    {
        var status = unchecked((uint)exitCode);
        return status switch
        {
            0 => "OK",
            1 => "invalid arguments",
            2 => "eboot not found",
            3 => "runtime exception",
            4 => "emulation error",
            0x8000_0003u => "breakpoint exception (0x80000003)",
            0xC000_0005u => "access violation (0xC0000005)",
            0xC000_0008u => "invalid handle (0xC0000008)",
            0xC000_001Du => "illegal instruction (0xC000001D)",
            0xC000_008Eu => "floating-point divide by zero (0xC000008E)",
            0xC000_0094u => "integer divide by zero (0xC0000094)",
            0xC000_00FDu => "stack overflow (0xC00000FD)",
            0xC000_0135u => "required DLL not found (0xC0000135)",
            0xC000_0139u => "DLL entry point not found (0xC0000139)",
            0xC000_0142u => "DLL initialization failed (0xC0000142)",
            0xC000_0374u => "heap corruption (0xC0000374)",
            0xC000_0409u => "stack buffer overrun or fast fail (0xC0000409)",
            134 => "aborted (signal 6)",
            137 => "killed (signal 9)",
            139 => "segmentation fault (signal 11)",
            143 => "terminated (signal 15)",
            uint.MaxValue => "exit status unavailable",
            _ when exitCode < 0 => $"unrecognized status 0x{status:X8}",
            _ => "unknown",
        };
    }
}
