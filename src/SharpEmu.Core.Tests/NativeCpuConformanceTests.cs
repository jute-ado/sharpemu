// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Runtime.InteropServices;
using SharpEmu.Core.Cpu;
using SharpEmu.Core.Memory;
using SharpEmu.HLE;
using Xunit;

namespace SharpEmu.Core.Tests;

public sealed class NativeCpuConformanceTests
{
    private const ulong PreferredCodeAddress = 0x0000_0008_1000_0000;

    public static TheoryData<string, byte[]> InstructionSequences => new()
    {
        {
            "return-zero",
            [
                0x31, 0xC0, // xor eax, eax
                0xC3,       // ret
            ]
        },
        {
            "overflow-flag",
            [
                0xB8, 0xFF, 0xFF, 0xFF, 0x7F, // mov eax, 0x7fffffff
                0x83, 0xC0, 0x01,             // add eax, 1
                0x70, 0x06,                   // jo success
                0xB8, 0x01, 0x00, 0x00, 0x00, // mov eax, 1
                0xC3,                         // ret
                0x31, 0xC0,                   // success: xor eax, eax
                0xC3,                         // ret
            ]
        },
        {
            "nested-call",
            [
                0xE8, 0x0D, 0x00, 0x00, 0x00, // call helper
                0x85, 0xC0,                   // test eax, eax
                0x75, 0x03,                   // jne failure
                0x31, 0xC0,                   // xor eax, eax
                0xC3,                         // ret
                0xB8, 0x01, 0x00, 0x00, 0x00, // failure: mov eax, 1
                0xC3,                         // ret
                0x31, 0xC0,                   // helper: xor eax, eax
                0xC3,                         // ret
            ]
        },
        {
            "tls-preserves-registers-and-flags",
            [
                0xB9, 0x44, 0x33, 0x22, 0x11,       // mov ecx, 0x11223344
                0x81, 0xF9, 0x44, 0x33, 0x22, 0x11, // cmp ecx, 0x11223344
                0x64, 0x48, 0x8B, 0x04, 0x25,
                0x00, 0x00, 0x00, 0x00,             // mov rax, fs:[0]
                0x75, 0x0B,                         // jne failure
                0x81, 0xF9, 0x44, 0x33, 0x22, 0x11, // cmp ecx, 0x11223344
                0x75, 0x03,                         // jne failure
                0x31, 0xC0,                         // xor eax, eax
                0xC3,                               // ret
                0xB8, 0x01, 0x00, 0x00, 0x00,       // failure: mov eax, 1
                0xC3,                               // ret
            ]
        },
        {
            "tls-load-preserves-rax-for-other-destinations",
            [
                0x48, 0xB8, 0x88, 0x77, 0x66, 0x55,
                0x44, 0x33, 0x22, 0x11,             // mov rax, 0x1122334455667788
                0x64, 0x48, 0x8B, 0x14, 0x25,
                0x00, 0x00, 0x00, 0x00,             // mov rdx, fs:[0]
                0x48, 0xB9, 0x88, 0x77, 0x66, 0x55,
                0x44, 0x33, 0x22, 0x11,             // mov rcx, 0x1122334455667788
                0x48, 0x39, 0xC8,                   // cmp rax, rcx
                0x75, 0x08,                         // jne failure
                0x48, 0x85, 0xD2,                   // test rdx, rdx
                0x74, 0x03,                         // jz failure
                0x31, 0xC0,                         // xor eax, eax
                0xC3,                               // ret
                0xB8, 0x01, 0x00, 0x00, 0x00,       // failure: mov eax, 1
                0xC3,                               // ret
            ]
        },
        {
            "tls-load-supports-nonvolatile-destinations",
            [
                0x48, 0xB8, 0x88, 0x77, 0x66, 0x55,
                0x44, 0x33, 0x22, 0x11,             // mov rax, 0x1122334455667788
                0x64, 0x48, 0x8B, 0x1C, 0x25,
                0x00, 0x00, 0x00, 0x00,             // mov rbx, fs:[0]
                0x48, 0xB9, 0x88, 0x77, 0x66, 0x55,
                0x44, 0x33, 0x22, 0x11,             // mov rcx, 0x1122334455667788
                0x48, 0x39, 0xC8,                   // cmp rax, rcx
                0x75, 0x08,                         // jne failure
                0x48, 0x85, 0xDB,                   // test rbx, rbx
                0x74, 0x03,                         // jz failure
                0x31, 0xC0,                         // xor eax, eax
                0xC3,                               // ret
                0xB8, 0x01, 0x00, 0x00, 0x00,       // failure: mov eax, 1
                0xC3,                               // ret
            ]
        },
    };

    [Theory]
    [MemberData(nameof(InstructionSequences))]
    public void ExecutesNativeInstructionSequence(string name, byte[] code)
    {
        if (!OperatingSystem.IsWindows() ||
            RuntimeInformation.ProcessArchitecture != Architecture.X64)
        {
            return;
        }

        var previousWatchdog = Environment.GetEnvironmentVariable(
            "SHARPEMU_STALL_WATCHDOG_SECONDS");
        Environment.SetEnvironmentVariable("SHARPEMU_STALL_WATCHDOG_SECONDS", "0");
        try
        {
            using var memory = new PhysicalVirtualMemory();
            var entryPoint = memory.AllocateAt(
                PreferredCodeAddress,
                0x1000,
                executable: true);
            Assert.True(memory.TryWrite(entryPoint, code), name);
            using var dispatcher = new CpuDispatcher(memory, new ModuleManager());

            var result = dispatcher.DispatchModuleInitializer(
                entryPoint,
                Generation.Gen5,
                moduleName: $"conformance-{name}");

            Assert.Equal(OrbisGen2Result.ORBIS_GEN2_OK, result);
            Assert.Equal(CpuExitReason.ReturnedToHost, dispatcher.LastSessionSummary.Reason);
        }
        finally
        {
            Environment.SetEnvironmentVariable(
                "SHARPEMU_STALL_WATCHDOG_SECONDS",
                previousWatchdog);
        }
    }
}
