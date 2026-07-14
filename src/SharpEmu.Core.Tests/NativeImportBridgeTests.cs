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
    private const string SixArgumentSumNid = "test-six-argument-sum-nid";
    private const string EightArgumentSumNid = "test-eight-argument-sum-nid";
    private const string FloatReturnNid = "test-float-return-nid";
    private const string FloatAddNid = "test-float-add-nid";
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

    [WindowsX64Fact]
    public void ImportBridgeCarriesSixArgumentsAndPreservesNonvolatileRegister()
    {
        byte[] code =
        [
            0x48, 0xBB, 0x88, 0x77, 0x66, 0x55,
            0x44, 0x33, 0x22, 0x11,       // mov rbx, 0x1122334455667788
            0xBF, 0x01, 0x00, 0x00, 0x00, // mov edi, 1
            0xBE, 0x02, 0x00, 0x00, 0x00, // mov esi, 2
            0xBA, 0x04, 0x00, 0x00, 0x00, // mov edx, 4
            0xB9, 0x08, 0x00, 0x00, 0x00, // mov ecx, 8
            0x41, 0xB8, 0x10, 0x00, 0x00, 0x00, // mov r8d, 16
            0x41, 0xB9, 0x20, 0x00, 0x00, 0x00, // mov r9d, 32
            0xE8, 0xD1, 0x00, 0x00, 0x00, // call ImportAddress
            0x48, 0xB9, 0x88, 0x77, 0x66, 0x55,
            0x44, 0x33, 0x22, 0x11,       // mov rcx, 0x1122334455667788
            0x48, 0x39, 0xCB,             // cmp rbx, rcx
            0x75, 0x08,                   // jne failure
            0x83, 0xF8, 0x3F,             // cmp eax, 63
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
        Assert.True(registered >= 2);
        Assert.True(moduleManager.TryGetExport(SixArgumentSumNid, out _));
        moduleManager.Freeze();
        using var dispatcher = new CpuDispatcher(memory, moduleManager);

        var result = dispatcher.DispatchModuleInitializer(
            entryPoint,
            Generation.Gen5,
            new Dictionary<ulong, string> { [ImportAddress] = SixArgumentSumNid },
            moduleName: "synthetic-six-argument-import-roundtrip");

        Assert.True(
            result == OrbisGen2Result.ORBIS_GEN2_OK,
            dispatcher.LastNotImplementedInfo?.Detail ?? $"Unexpected result: {result}");
        Assert.Equal(CpuExitReason.ReturnedToHost, dispatcher.LastSessionSummary.Reason);
    }

    [WindowsX64Fact]
    public void ImportBridgeCarriesIntegerArgumentsFromGuestStack()
    {
        byte[] code =
        [
            0x48, 0x83, 0xEC, 0x10,       // sub rsp, 16
            0x48, 0xC7, 0x04, 0x24, 0x40, 0x00, 0x00, 0x00, // mov qword [rsp], 64
            0x48, 0xC7, 0x44, 0x24, 0x08, 0x80, 0x00, 0x00, 0x00, // mov qword [rsp+8], 128
            0xBF, 0x01, 0x00, 0x00, 0x00, // mov edi, 1
            0xBE, 0x02, 0x00, 0x00, 0x00, // mov esi, 2
            0xBA, 0x04, 0x00, 0x00, 0x00, // mov edx, 4
            0xB9, 0x08, 0x00, 0x00, 0x00, // mov ecx, 8
            0x41, 0xB8, 0x10, 0x00, 0x00, 0x00, // mov r8d, 16
            0x41, 0xB9, 0x20, 0x00, 0x00, 0x00, // mov r9d, 32
            0xE8, 0xC6, 0x00, 0x00, 0x00, // call ImportAddress
            0x48, 0x83, 0xC4, 0x10,       // add rsp, 16
            0x3D, 0xFF, 0x00, 0x00, 0x00, // cmp eax, 255
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
        Assert.True(registered >= 3);
        Assert.True(moduleManager.TryGetExport(EightArgumentSumNid, out _));
        moduleManager.Freeze();
        using var dispatcher = new CpuDispatcher(memory, moduleManager);

        var result = dispatcher.DispatchModuleInitializer(
            entryPoint,
            Generation.Gen5,
            new Dictionary<ulong, string> { [ImportAddress] = EightArgumentSumNid },
            moduleName: "synthetic-stack-argument-import-roundtrip");

        Assert.True(
            result == OrbisGen2Result.ORBIS_GEN2_OK,
            dispatcher.LastNotImplementedInfo?.Detail ?? $"Unexpected result: {result}");
        Assert.Equal(CpuExitReason.ReturnedToHost, dispatcher.LastSessionSummary.Reason);
    }

    [WindowsX64Fact]
    public void ImportBridgeReturnsFloatingPointValueInXmm0()
    {
        byte[] code =
        [
            0xE8, 0xFB, 0x00, 0x00, 0x00, // call ImportAddress
            0x66, 0x0F, 0x7E, 0xC0,       // movd eax, xmm0
            0x3D, 0x00, 0x00, 0xC0, 0x3F, // cmp eax, 0x3fc00000 (1.5f)
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
        Assert.True(registered >= 3);
        Assert.True(moduleManager.TryGetExport(FloatReturnNid, out _));
        moduleManager.Freeze();
        using var dispatcher = new CpuDispatcher(memory, moduleManager);

        var result = dispatcher.DispatchModuleInitializer(
            entryPoint,
            Generation.Gen5,
            new Dictionary<ulong, string> { [ImportAddress] = FloatReturnNid },
            moduleName: "synthetic-float-import-roundtrip");

        Assert.True(
            result == OrbisGen2Result.ORBIS_GEN2_OK,
            dispatcher.LastNotImplementedInfo?.Detail ?? $"Unexpected result: {result}");
        Assert.Equal(CpuExitReason.ReturnedToHost, dispatcher.LastSessionSummary.Reason);
    }

    [WindowsX64Fact]
    public void ImportBridgeCarriesFloatingPointArgumentsAndReturnValue()
    {
        byte[] code =
        [
            0xB8, 0x00, 0x00, 0xC0, 0x3F, // mov eax, 0x3fc00000 (1.5f)
            0x66, 0x0F, 0x6E, 0xC0,       // movd xmm0, eax
            0xB8, 0x00, 0x00, 0x10, 0x40, // mov eax, 0x40100000 (2.25f)
            0x66, 0x0F, 0x6E, 0xC8,       // movd xmm1, eax
            0xE8, 0xE9, 0x00, 0x00, 0x00, // call ImportAddress
            0x66, 0x0F, 0x7E, 0xC0,       // movd eax, xmm0
            0x3D, 0x00, 0x00, 0x70, 0x40, // cmp eax, 0x40700000 (3.75f)
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
        Assert.True(registered >= 4);
        Assert.True(moduleManager.TryGetExport(FloatAddNid, out _));
        moduleManager.Freeze();
        using var dispatcher = new CpuDispatcher(memory, moduleManager);

        var result = dispatcher.DispatchModuleInitializer(
            entryPoint,
            Generation.Gen5,
            new Dictionary<ulong, string> { [ImportAddress] = FloatAddNid },
            moduleName: "synthetic-float-argument-roundtrip");

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

        [SysAbiExport(
            Nid = SixArgumentSumNid,
            ExportName = "syntheticSixArgumentSum",
            Target = Generation.Gen5,
            LibraryName = "libSyntheticTest")]
        public static int SixArgumentSum(CpuContext context)
        {
            var result = checked((int)(
                context[CpuRegister.Rdi] +
                context[CpuRegister.Rsi] +
                context[CpuRegister.Rdx] +
                context[CpuRegister.Rcx] +
                context[CpuRegister.R8] +
                context[CpuRegister.R9]));
            return context.SetReturn(result);
        }

        [SysAbiExport(
            Nid = EightArgumentSumNid,
            ExportName = "syntheticEightArgumentSum",
            Target = Generation.Gen5,
            LibraryName = "libSyntheticTest")]
        public static int EightArgumentSum(CpuContext context)
        {
            if (!context.TryReadStackArgumentUInt64(0, out var seventh) ||
                !context.TryReadStackArgumentUInt64(1, out var eighth))
            {
                return context.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
            }

            var result = checked((int)(
                context[CpuRegister.Rdi] +
                context[CpuRegister.Rsi] +
                context[CpuRegister.Rdx] +
                context[CpuRegister.Rcx] +
                context[CpuRegister.R8] +
                context[CpuRegister.R9] +
                seventh +
                eighth));
            return context.SetReturn(result);
        }

        [SysAbiExport(
            Nid = FloatReturnNid,
            ExportName = "syntheticFloatReturn",
            Target = Generation.Gen5,
            LibraryName = "libSyntheticTest")]
        public static int FloatReturn(CpuContext context)
        {
            context.SetXmmRegister(0, 0x3FC0_0000, 0);
            return context.SetReturn(OrbisGen2Result.ORBIS_GEN2_OK);
        }

        [SysAbiExport(
            Nid = FloatAddNid,
            ExportName = "syntheticFloatAdd",
            Target = Generation.Gen5,
            LibraryName = "libSyntheticTest")]
        public static int FloatAdd(CpuContext context)
        {
            context.GetXmmRegister(0, out var leftBits, out _);
            context.GetXmmRegister(1, out var rightBits, out _);
            var left = BitConverter.Int32BitsToSingle(unchecked((int)leftBits));
            var right = BitConverter.Int32BitsToSingle(unchecked((int)rightBits));
            var sumBits = unchecked((uint)BitConverter.SingleToInt32Bits(left + right));
            context.SetXmmRegister(0, sumBits, 0);
            return context.SetReturn(OrbisGen2Result.ORBIS_GEN2_OK);
        }
    }
}
