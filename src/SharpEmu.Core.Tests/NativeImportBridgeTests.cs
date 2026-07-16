// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Core.Cpu;
using SharpEmu.HLE;
using Xunit;

namespace SharpEmu.Core.Tests;

public sealed class NativeImportBridgeTests
{
    private const string AddNid = "test-add-nid";
    private const string SixArgumentSumNid = "test-six-argument-sum-nid";
    private const string EightArgumentSumNid = "test-eight-argument-sum-nid";
    private const string ClobberNonvolatileNid = "test-clobber-nonvolatile-nid";
    private const string FloatReturnNid = "test-float-return-nid";
    private const string FloatAddNid = "test-float-add-nid";
    private const string ColdHandlerNid = "test-cold-handler-nid";
    private const ulong CodeAddress = 0x0000_0008_1000_0000;
    private const ulong ImportAddress = CodeAddress + 0x100;
    private const ulong FallbackImportAddress = 0x0000_6FFF_FF00_0000;
    private const ulong NonvolatileSentinel = 0x1122_3344_5566_7788;

    [HostX64Fact]
    public async Task GuestCallDispatchesHleExportAndReturnsValue()
    {
        if (await NativeTestProcess.RunIfNeededAsync(typeof(NativeImportBridgeTests)))
        {
            return;
        }

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
        var execution = ExecuteImport(code, AddNid, "synthetic-import-roundtrip");
        AssertSuccessful(execution);
    }

    [HostX64Fact]
    public async Task FirstGuestImportInitializesColdHandlerOnHostStack()
    {
        if (await NativeTestProcess.RunIfNeededAsync(typeof(NativeImportBridgeTests)))
        {
            return;
        }

        Assert.Equal(0, ColdHandlerState.InitializerCalls);
        SysAbiFunction handler = ColdHandler.Invoke;
        Assert.Equal(0, ColdHandlerState.InitializerCalls);

        byte[] code =
        [
            0xE8, 0xFB, 0x00, 0x00, 0x00, // call ImportAddress
            0x83, 0xF8, 0x2A,             // cmp eax, 42
            0x75, 0x03,                   // jne failure
            0x31, 0xC0,                   // xor eax, eax
            0xC3,                         // ret
            0xB8, 0x01, 0x00, 0x00, 0x00, // failure: mov eax, 1
            0xC3,                         // ret
        ];
        var execution = SyntheticNativeGuest.ExecuteModuleInitializer(
            code,
            Generation.Gen5,
            "synthetic-cold-handler-import",
            new Dictionary<ulong, string> { [ImportAddress] = ColdHandlerNid },
            moduleManager =>
            {
                Assert.Equal(
                    1,
                    moduleManager.RegisterExports(
                    [
                        new ExportedFunction(
                            "libSyntheticTest",
                            ColdHandlerNid,
                            "syntheticColdHandler",
                            Generation.Gen5,
                            handler),
                    ]));
                Assert.Equal(0, ColdHandlerState.InitializerCalls);
            },
            CodeAddress);

        AssertSuccessful(execution);
        Assert.Equal(1, ColdHandlerState.InitializerCalls);
        Assert.Equal(1, ColdHandlerState.HandlerCalls);
        Assert.True(ColdHandlerState.HandlerUsedHostStack);
    }

    [HostX64Fact]
    public async Task GuestCallDispatchesImportFromFallbackStubRegion()
    {
        if (await NativeTestProcess.RunIfNeededAsync(typeof(NativeImportBridgeTests)))
        {
            return;
        }

        var code = new List<byte>
        {
            0xBF, 0x14, 0x00, 0x00, 0x00, // mov edi, 20
            0xBE, 0x16, 0x00, 0x00, 0x00, // mov esi, 22
            0x48, 0xB8,                   // mov rax, FallbackImportAddress
        };
        for (var shift = 0; shift < 64; shift += 8)
        {
            code.Add((byte)(FallbackImportAddress >> shift));
        }
        code.AddRange(
        [
            0xFF, 0xD0,                   // call rax
            0x83, 0xF8, 0x2A,             // cmp eax, 42
            0x75, 0x03,                   // jne failure
            0x31, 0xC0,                   // xor eax, eax
            0xC3,                         // ret
            0xB8, 0x01, 0x00, 0x00, 0x00, // failure: mov eax, 1
            0xC3,                         // ret
        ]);

        var execution = ExecuteImport(
            code.ToArray(),
            AddNid,
            "synthetic-fallback-import-roundtrip",
            FallbackImportAddress);
        AssertSuccessful(execution);
    }

    [HostX64Fact]
    public async Task ImportBridgeCarriesSixArgumentsAndPreservesNonvolatileRegister()
    {
        if (await NativeTestProcess.RunIfNeededAsync(typeof(NativeImportBridgeTests)))
        {
            return;
        }

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
        var execution = ExecuteImport(
            code,
            SixArgumentSumNid,
            "synthetic-six-argument-import-roundtrip");
        AssertSuccessful(execution);
    }

    [HostX64Fact]
    public async Task ImportBridgeCarriesIntegerArgumentsFromGuestStack()
    {
        if (await NativeTestProcess.RunIfNeededAsync(typeof(NativeImportBridgeTests)))
        {
            return;
        }

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
        var execution = ExecuteImport(
            code,
            EightArgumentSumNid,
            "synthetic-stack-argument-import-roundtrip");
        AssertSuccessful(execution);
    }

    [HostX64Fact]
    public async Task ImportBridgePreservesGuestNonvolatileRegistersAcrossManagedHandler()
    {
        if (await NativeTestProcess.RunIfNeededAsync(typeof(NativeImportBridgeTests)))
        {
            return;
        }

        var code = CreateNonvolatileRegisterProbe();
        var execution = ExecuteImport(
            code,
            ClobberNonvolatileNid,
            "synthetic-nonvolatile-import-roundtrip");
        AssertSuccessful(execution);
    }

    [HostX64Fact]
    public async Task ImportBridgeReturnsFloatingPointValueInXmm0()
    {
        if (await NativeTestProcess.RunIfNeededAsync(typeof(NativeImportBridgeTests)))
        {
            return;
        }

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
        var execution = ExecuteImport(
            code,
            FloatReturnNid,
            "synthetic-float-import-roundtrip");
        AssertSuccessful(execution);
    }

    [HostX64Fact]
    public async Task ImportBridgeCarriesFloatingPointArgumentsAndReturnValue()
    {
        if (await NativeTestProcess.RunIfNeededAsync(typeof(NativeImportBridgeTests)))
        {
            return;
        }

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
        var execution = ExecuteImport(
            code,
            FloatAddNid,
            "synthetic-float-argument-roundtrip");
        AssertSuccessful(execution);
    }

    private static SyntheticGuestExecutionResult ExecuteImport(
        byte[] code,
        string nid,
        string moduleName,
        ulong importAddress = ImportAddress)
    {
        return SyntheticNativeGuest.ExecuteModuleInitializer(
            code,
            Generation.Gen5,
            moduleName,
            new Dictionary<ulong, string> { [importAddress] = nid },
            moduleManager =>
            {
                var registered = moduleManager.RegisterExports(
                    SharpEmu.Core.Tests.Generated.SysAbiExportRegistry.CreateExports(
                        Generation.Gen5));
                Assert.True(registered > 0);
                Assert.True(moduleManager.TryGetExport(nid, out _));
            },
            CodeAddress);
    }

    private static void AssertSuccessful(SyntheticGuestExecutionResult execution)
    {
        Assert.True(
            execution.Result == OrbisGen2Result.ORBIS_GEN2_OK,
            execution.FailureDetail ?? $"Unexpected result: {execution.Result}");
        Assert.Equal(CpuExitReason.ReturnedToHost, execution.ExitReason);
    }

    internal static class SyntheticExports
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
            Nid = ClobberNonvolatileNid,
            ExportName = "syntheticClobberNonvolatile",
            Target = Generation.Gen5,
            LibraryName = "libSyntheticTest")]
        public static int ClobberNonvolatile(CpuContext context)
        {
            var receivedExpectedValues =
                context[CpuRegister.Rbx] == NonvolatileSentinel &&
                context[CpuRegister.Rbp] == NonvolatileSentinel &&
                context[CpuRegister.R12] == NonvolatileSentinel &&
                context[CpuRegister.R13] == NonvolatileSentinel &&
                context[CpuRegister.R14] == NonvolatileSentinel &&
                context[CpuRegister.R15] == NonvolatileSentinel;

            context[CpuRegister.Rbx] = 0;
            context[CpuRegister.Rbp] = 0;
            context[CpuRegister.R12] = 0;
            context[CpuRegister.R13] = 0;
            context[CpuRegister.R14] = 0;
            context[CpuRegister.R15] = 0;
            return context.SetReturn(receivedExpectedValues
                ? OrbisGen2Result.ORBIS_GEN2_OK
                : OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
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

    private static class ColdHandlerState
    {
        public static int InitializerCalls;

        public static int HandlerCalls;

        public static bool HandlerUsedHostStack;
    }

    private static class ColdHandler
    {
        static ColdHandler()
        {
            ColdHandlerState.InitializerCalls++;
        }

        public static unsafe int Invoke(CpuContext context)
        {
            ColdHandlerState.HandlerCalls++;
            byte* local = stackalloc byte[1];
            var localAddress = unchecked((ulong)local);
            ColdHandlerState.HandlerUsedHostStack =
                context.Memory is IGuestStackMemory guestStacks &&
                guestStacks.TryGetStackRange(
                    context[CpuRegister.Rsp],
                    out var guestStackStart,
                    out var guestStackEnd) &&
                (localAddress < guestStackStart || localAddress >= guestStackEnd);
            return context.SetReturn(42);
        }
    }

    private static byte[] CreateNonvolatileRegisterProbe()
    {
        var code = new List<byte>();
        var failureBranches = new List<int>();

        void Emit(params byte[] bytes) => code.AddRange(bytes);
        void EmitUInt64(ulong value)
        {
            for (var shift = 0; shift < 64; shift += 8)
            {
                code.Add((byte)(value >> shift));
            }
        }
        void EmitMovImmediate(byte rex, byte opcode)
        {
            Emit(rex, opcode);
            EmitUInt64(NonvolatileSentinel);
        }
        void JumpToFailure()
        {
            Emit(0x0F, 0x85, 0, 0, 0, 0); // jne failure
            failureBranches.Add(code.Count - sizeof(int));
        }
        void PatchInt32(int offset, int value)
        {
            code[offset] = (byte)value;
            code[offset + 1] = (byte)(value >> 8);
            code[offset + 2] = (byte)(value >> 16);
            code[offset + 3] = (byte)(value >> 24);
        }

        EmitMovImmediate(0x48, 0xBB); // mov rbx, sentinel
        EmitMovImmediate(0x48, 0xBD); // mov rbp, sentinel
        EmitMovImmediate(0x49, 0xBC); // mov r12, sentinel
        EmitMovImmediate(0x49, 0xBD); // mov r13, sentinel
        EmitMovImmediate(0x49, 0xBE); // mov r14, sentinel
        EmitMovImmediate(0x49, 0xBF); // mov r15, sentinel

        Emit(0xE8, 0, 0, 0, 0); // call ImportAddress
        PatchInt32(code.Count - sizeof(int), checked((int)(ImportAddress - CodeAddress) - code.Count));

        Emit(0x85, 0xC0); // test eax, eax
        JumpToFailure();
        EmitMovImmediate(0x49, 0xBA); // mov r10, sentinel
        Emit(0x4C, 0x39, 0xD3); // cmp rbx, r10
        JumpToFailure();
        Emit(0x4C, 0x39, 0xD5); // cmp rbp, r10
        JumpToFailure();
        Emit(0x4D, 0x39, 0xD4); // cmp r12, r10
        JumpToFailure();
        Emit(0x4D, 0x39, 0xD5); // cmp r13, r10
        JumpToFailure();
        Emit(0x4D, 0x39, 0xD6); // cmp r14, r10
        JumpToFailure();
        Emit(0x4D, 0x39, 0xD7); // cmp r15, r10
        JumpToFailure();
        Emit(0x31, 0xC0, 0xC3); // xor eax, eax; ret

        var failureOffset = code.Count;
        Emit(0xB8, 0x01, 0x00, 0x00, 0x00, 0xC3); // mov eax, 1; ret
        foreach (var displacementOffset in failureBranches)
        {
            PatchInt32(
                displacementOffset,
                checked(failureOffset - (displacementOffset + sizeof(int))));
        }

        return code.ToArray();
    }
}
