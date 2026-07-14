// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Reflection;
using SharpEmu.Core.Cpu;
using SharpEmu.Core.Memory;
using SharpEmu.HLE;
using Xunit;

namespace SharpEmu.Core.Tests;

public sealed class NativeImportBridgeTests
{
    private const string AddNid = "test-add-nid";
    private const ulong CodeAddress = 0x0000_0008_1000_0000;
    private const ulong ImportAddress = CodeAddress + 0x100;

    [WindowsX64Fact]
    public void GuestCallDispatchesHleExportAndReturnsValue()
    {
        byte[] code =
        [
            0xBF, 0x14, 0x00, 0x00, 0x00, // mov edi, 20
            0xBE, 0x16, 0x00, 0x00, 0x00, // mov esi, 22
            0xE8, 0xF1, 0x00, 0x00, 0x00, // call ImportAddress
            0x83, 0xF8, 0x2A,             // cmp eax, 42
            0x75, 0x03,                   // jne failure
            0x31, 0xC0,                   // xor eax, eax
            0xC3,                         // ret
            0xB8, 0x01, 0x00, 0x00, 0x00, // failure: mov eax, 1
            0xC3,                         // ret
        ];
        using var memory = new PhysicalVirtualMemory();
        var entryPoint = memory.AllocateAt(CodeAddress, 0x1000, executable: true);
        Assert.Equal(CodeAddress, entryPoint);
        Assert.True(memory.TryWrite(entryPoint, code));
        Assert.True(memory.TryWrite(ImportAddress, [0xCC, 0xC3]));

        var moduleManager = new ModuleManager();
        var registered = moduleManager.RegisterFromAssembly(
            Assembly.GetExecutingAssembly(),
            Generation.Gen5);
        Assert.True(registered >= 1);
        Assert.True(moduleManager.TryGetExport(AddNid, out _));
        moduleManager.Freeze();
        using var dispatcher = new CpuDispatcher(memory, moduleManager);

        var result = dispatcher.DispatchModuleInitializer(
            entryPoint,
            Generation.Gen5,
            new Dictionary<ulong, string> { [ImportAddress] = AddNid },
            moduleName: "synthetic-import-roundtrip");

        Assert.True(
            result == OrbisGen2Result.ORBIS_GEN2_OK,
            dispatcher.LastNotImplementedInfo?.Detail ?? $"Unexpected result: {result}");
        Assert.Equal(CpuExitReason.ReturnedToHost, dispatcher.LastSessionSummary.Reason);
    }

    private static class SyntheticExports
    {
        [SysAbiExport(
            Nid = AddNid,
            ExportName = "syntheticAdd",
            Target = Generation.Gen5,
            LibraryName = "libSyntheticTest")]
        public static int Add(CpuContext context)
        {
            var result = checked((int)(
                context[CpuRegister.Rdi] +
                context[CpuRegister.Rsi]));
            return context.SetReturn(result);
        }
    }
}
