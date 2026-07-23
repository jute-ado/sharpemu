// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using SharpEmu.Core.Cpu;
using SharpEmu.Core.Cpu.Native;
using SharpEmu.HLE;
using SharpEmu.Libs.Kernel;
using SharpEmu.Logging;
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
    private const string BlockingYieldNid = "test-blocking-yield-nid";
    private const string RegisterExternalThreadNid = "test-register-external-thread-nid";
    private const string RegisteredStackProbeNid = "test-registered-stack-probe-nid";
    private const string MemalignNid = "Ujf3KzMvRmI";
    private const string PluginInitializeNid = "Mglc7amPW4k";
    private const string ScalarLeafProbeNid = "aI+OeCz8xrQ";
    private const string FailureNid = "test-failure-nid";
    private const ulong CodeAddress = 0x0000_0008_1000_0000;
    private const ulong ImportAddress = CodeAddress + 0x100;
    private const ulong SecondImportAddress = ImportAddress + 0x10;
    private const ulong ThirdImportAddress = SecondImportAddress + 0x10;
    private const ulong FallbackImportAddress = 0x0000_6FFF_FF00_0000;
    private const ulong DlsymImportAddress = 0x0000_7000_0000_0000;
    private const ulong DlsymResultAddress = CodeAddress + 0x280;
    private const ulong DlsymSymbolAddress = CodeAddress + 0x200;
    private const ulong PluginFunctionAddress = CodeAddress + 0x300;
    private const ulong NonvolatileSentinel = 0x1122_3344_5566_7788;
    private const ulong ApplicationHeapAllocationSentinel = 0x1234_5678_9ABC_0000;

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
        Assert.Equal(1, execution.ImportsHit);
    }

    [HostX64Fact]
    public async Task NativeSessionCountsUniqueNidsAcrossRepeatedImports()
    {
        if (await NativeTestProcess.RunIfNeededAsync(typeof(NativeImportBridgeTests)))
        {
            return;
        }

        byte[] code =
        [
            0xE8, 0xFB, 0x00, 0x00, 0x00, // call ImportAddress
            0xE8, 0xF6, 0x00, 0x00, 0x00, // call ImportAddress again
            0xE8, 0x01, 0x01, 0x00, 0x00, // call SecondImportAddress
            0xE8, 0x0C, 0x01, 0x00, 0x00, // call ThirdImportAddress (same NID as first)
            0x31, 0xC0,                   // xor eax, eax
            0xC3,                         // ret
        ];
        var execution = SyntheticNativeGuest.ExecuteModuleInitializer(
            code,
            Generation.Gen5,
            "synthetic-unique-native-imports",
            new Dictionary<ulong, string>
            {
                [ImportAddress] = AddNid,
                [SecondImportAddress] = SixArgumentSumNid,
                [ThirdImportAddress] = AddNid,
            },
            moduleManager => moduleManager.RegisterExports(
            [
                new ExportedFunction(
                    "libSyntheticTest",
                    AddNid,
                    "syntheticAdd",
                    Generation.Gen5,
                    SyntheticExports.Add),
                new ExportedFunction(
                    "libSyntheticTest",
                    SixArgumentSumNid,
                    "syntheticSecondAdd",
                    Generation.Gen5,
                    SyntheticExports.Add),
            ]),
            CodeAddress);

        AssertSuccessful(execution);
        Assert.Equal(4, execution.ImportsHit);
        Assert.Equal(2, execution.UniqueNidsHit);
    }

    [HostX64Fact]
    public async Task NativeImportTraceRetainsOnlyRequestedRecentCalls()
    {
        if (await NativeTestProcess.RunIfNeededAsync(typeof(NativeImportBridgeTests)))
        {
            return;
        }

        byte[] code =
        [
            0xE8, 0xFB, 0x00, 0x00, 0x00, // call ImportAddress
            0xE8, 0xF6, 0x00, 0x00, 0x00, // call ImportAddress
            0xE8, 0xF1, 0x00, 0x00, 0x00, // call ImportAddress
            0x31, 0xC0,                   // xor eax, eax
            0xC3,                         // ret
        ];
        var execution = SyntheticNativeGuest.ExecuteModuleInitializer(
            code,
            Generation.Gen5,
            "synthetic-bounded-native-import-trace",
            new Dictionary<ulong, string> { [ImportAddress] = AddNid },
            moduleManager =>
            {
                Assert.Equal(
                    1,
                    moduleManager.RegisterExports(
                    [
                        new ExportedFunction(
                            "libSyntheticTest",
                            AddNid,
                            "syntheticAdd",
                            Generation.Gen5,
                            SyntheticExports.Add),
                    ]));
            },
            CodeAddress,
            new CpuExecutionOptions { ImportTraceLimit = 2 });

        AssertSuccessful(execution);
        Assert.Equal(3, execution.ImportsHit);
        Assert.NotNull(execution.ImportTrace);
        var lines = execution.ImportTrace.Split(Environment.NewLine);
        Assert.Equal(2, lines.Length);
        Assert.DoesNotContain($"ret=0x{CodeAddress + 5:X16}", execution.ImportTrace, StringComparison.Ordinal);
        Assert.Contains($"ret=0x{CodeAddress + 10:X16}", lines[0], StringComparison.Ordinal);
        Assert.Contains($"ret=0x{CodeAddress + 15:X16}", lines[1], StringComparison.Ordinal);
        Assert.All(lines, line => Assert.Contains($"nid={AddNid}", line, StringComparison.Ordinal));
        Assert.All(
            lines,
            line => Assert.Contains("symbol=libSyntheticTest:syntheticAdd", line, StringComparison.Ordinal));
        Assert.NotNull(execution.ImportTraceEntries);
        Assert.Collection(
            execution.ImportTraceEntries,
            entry =>
            {
                Assert.Equal(2, entry.DispatchIndex);
                Assert.Equal(AddNid, entry.Nid);
                Assert.Equal("libSyntheticTest", entry.LibraryName);
                Assert.Equal("syntheticAdd", entry.ExportName);
                Assert.Equal(CodeAddress + 10, entry.ReturnAddress);
                Assert.Equal(0UL, entry.ReturnValue);
            },
            entry =>
            {
                Assert.Equal(3, entry.DispatchIndex);
                Assert.Equal(AddNid, entry.Nid);
                Assert.Equal("libSyntheticTest", entry.LibraryName);
                Assert.Equal("syntheticAdd", entry.ExportName);
                Assert.Equal(CodeAddress + 15, entry.ReturnAddress);
                Assert.Equal(0UL, entry.ReturnValue);
            });
    }

    [HostX64Fact]
    public async Task SelectedImportFailureLogsBoundedCompletedRecentContext()
    {
        if (await NativeTestProcess.RunIfNeededAsync(typeof(NativeImportBridgeTests)))
        {
            return;
        }

        byte[] code =
        [
            0xE8, 0xFB, 0x00, 0x00, 0x00, // call ImportAddress
            0xE8, 0x06, 0x01, 0x00, 0x00, // call SecondImportAddress
            0x31, 0xC0,                   // xor eax, eax
            0xC3,                         // ret
        ];
        var previousSelector = Environment.GetEnvironmentVariable(
            "SHARPEMU_TRACE_IMPORT_FAILURE_CONTEXT");
        var previousSink = SharpEmuLog.Sink;
        var previousMinimumLevel = SharpEmuLog.MinimumLevel;
        var sink = new CollectingLogSink();
        try
        {
            Environment.SetEnvironmentVariable(
                "SHARPEMU_TRACE_IMPORT_FAILURE_CONTEXT",
                FailureNid);
            SharpEmuLog.Configure(LogLevel.Info, sink);

            var execution = SyntheticNativeGuest.ExecuteModuleInitializer(
                code,
                Generation.Gen5,
                "synthetic-selected-import-failure-context",
                new Dictionary<ulong, string>
                {
                    [ImportAddress] = AddNid,
                    [SecondImportAddress] = FailureNid,
                },
                moduleManager => moduleManager.RegisterExports(
                [
                    new ExportedFunction(
                        "libSyntheticTest",
                        AddNid,
                        "syntheticAdd",
                        Generation.Gen5,
                        SyntheticExports.Add),
                    new ExportedFunction(
                        "libSyntheticTest",
                        FailureNid,
                        "syntheticFailure",
                        Generation.Gen5,
                        context => context.SetReturn(
                            OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT)),
                ]),
                CodeAddress);

            AssertSuccessful(execution);
            Assert.Null(execution.ImportTrace);
            Assert.Contains(
                sink.Messages,
                message => message.Contains(
                    $"Recent import calls for failed import #2 {FailureNid}",
                    StringComparison.Ordinal));
            Assert.Contains(
                sink.Messages,
                message => message.Contains($"nid={AddNid}", StringComparison.Ordinal) &&
                    message.Contains("rax=0x0000000000000000", StringComparison.Ordinal));
            Assert.Contains(
                sink.Messages,
                message => message.Contains($"nid={FailureNid}", StringComparison.Ordinal) &&
                    !message.Contains("rax=<pending>", StringComparison.Ordinal));
        }
        finally
        {
            Environment.SetEnvironmentVariable(
                "SHARPEMU_TRACE_IMPORT_FAILURE_CONTEXT",
                previousSelector);
            SharpEmuLog.Configure(previousMinimumLevel, previousSink);
        }
    }

    [HostX64Fact]
    public async Task NativeImportTraceIncludesAllSysVRegisterArguments()
    {
        if (await NativeTestProcess.RunIfNeededAsync(typeof(NativeImportBridgeTests)))
        {
            return;
        }

        byte[] code =
        [
            0xBF, 0x14, 0x00, 0x00, 0x00, // mov edi, 20
            0xBE, 0x16, 0x00, 0x00, 0x00, // mov esi, 22
            0xBA, 0x0D, 0xF0, 0xAD, 0x0B, // mov edx, 0x0BADF00D
            0xB9, 0x78, 0x56, 0x34, 0x12, // mov ecx, 0x12345678
            0x41, 0xB8, 0x21, 0x43, 0x65, 0x87, // mov r8d, 0x87654321
            0x41, 0xB9, 0xEF, 0xCD, 0xAB, 0x09, // mov r9d, 0x09ABCDEF
            0xE8, 0xDB, 0x00, 0x00, 0x00, // call ImportAddress
            0x31, 0xC0,                   // xor eax, eax
            0xC3,                         // ret
        ];
        var execution = SyntheticNativeGuest.ExecuteModuleInitializer(
            code,
            Generation.Gen5,
            "synthetic-all-register-arguments-native-import-trace",
            new Dictionary<ulong, string> { [ImportAddress] = AddNid },
            moduleManager => moduleManager.RegisterExports(
            [
                new ExportedFunction(
                    "libSyntheticTest",
                    AddNid,
                    "syntheticAdd",
                    Generation.Gen5,
                    SyntheticExports.Add),
            ]),
            CodeAddress,
            new CpuExecutionOptions { ImportTraceLimit = 1 });

        AssertSuccessful(execution);
        Assert.Contains("rdi=0x0000000000000014", execution.ImportTrace, StringComparison.Ordinal);
        Assert.Contains("rsi=0x0000000000000016", execution.ImportTrace, StringComparison.Ordinal);
        Assert.Contains("rdx=0x000000000BADF00D", execution.ImportTrace, StringComparison.Ordinal);
        Assert.Contains("rcx=0x0000000012345678", execution.ImportTrace, StringComparison.Ordinal);
        Assert.Contains("r8=0x0000000087654321", execution.ImportTrace, StringComparison.Ordinal);
        Assert.Contains("r9=0x0000000009ABCDEF", execution.ImportTrace, StringComparison.Ordinal);
        Assert.Contains("rax=0x000000000000002A", execution.ImportTrace, StringComparison.Ordinal);
    }

    [HostX64Fact]
    public async Task NativeImportResultWarningIncludesAllSysVRegisterArguments()
    {
        if (await NativeTestProcess.RunIfNeededAsync(typeof(NativeImportBridgeTests)))
        {
            return;
        }

        byte[] code =
        [
            0xBF, 0x14, 0x00, 0x00, 0x00, // mov edi, 20
            0xBE, 0x16, 0x00, 0x00, 0x00, // mov esi, 22
            0xBA, 0x0D, 0xF0, 0xAD, 0x0B, // mov edx, 0x0BADF00D
            0xB9, 0x78, 0x56, 0x34, 0x12, // mov ecx, 0x12345678
            0x41, 0xB8, 0x21, 0x43, 0x65, 0x87, // mov r8d, 0x87654321
            0x41, 0xB9, 0xEF, 0xCD, 0xAB, 0x09, // mov r9d, 0x09ABCDEF
            0xE8, 0xDB, 0x00, 0x00, 0x00, // call ImportAddress
            0x31, 0xC0,                   // xor eax, eax
            0xC3,                         // ret
        ];
        var previousError = Console.Error;
        using var error = new StringWriter();
        try
        {
            Console.SetError(error);
            var execution = SyntheticNativeGuest.ExecuteModuleInitializer(
                code,
                Generation.Gen5,
                "synthetic-all-register-arguments-native-import-warning",
                new Dictionary<ulong, string> { [ImportAddress] = FailureNid },
                moduleManager => moduleManager.RegisterExports(
                [
                    new ExportedFunction(
                        "libSyntheticTest",
                        FailureNid,
                        "syntheticFailure",
                        Generation.Gen5,
                        context => context.SetReturn(
                            OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT)),
                ]),
                CodeAddress);

            AssertSuccessful(execution);
        }
        finally
        {
            Console.SetError(previousError);
        }

        var warning = error.ToString();
        Assert.Contains($"result: ORBIS_GEN2_ERROR_INVALID_ARGUMENT ({FailureNid})", warning, StringComparison.Ordinal);
        Assert.Contains("rdi=0x0000000000000014", warning, StringComparison.Ordinal);
        Assert.Contains("rsi=0x0000000000000016", warning, StringComparison.Ordinal);
        Assert.Contains("rdx=0x000000000BADF00D", warning, StringComparison.Ordinal);
        Assert.Contains("rcx=0x0000000012345678", warning, StringComparison.Ordinal);
        Assert.Contains("r8=0x0000000087654321", warning, StringComparison.Ordinal);
        Assert.Contains("r9=0x0000000009ABCDEF", warning, StringComparison.Ordinal);
    }

    [HostX64Fact]
    public async Task NativeImportTraceIncludesGuestWorkerCalls()
    {
        if (await NativeTestProcess.RunIfNeededAsync(typeof(NativeImportBridgeTests)))
        {
            return;
        }

        byte[] code =
        [
            0xE8, 0xFB, 0x00, 0x00, 0x00, // call ImportAddress
            0x31, 0xC0,                   // xor eax, eax
            0xC3,                         // ret
        ];
        var execution = Assert.Single(SyntheticNativeGuest.ExecuteModuleInitializers(
            code,
            Generation.Gen5,
            "synthetic-worker-native-import-trace",
            executionCount: 1,
            new Dictionary<ulong, string> { [ImportAddress] = AddNid },
            moduleManager => moduleManager.RegisterExports(
            [
                new ExportedFunction(
                    "libSyntheticTest",
                    AddNid,
                    "syntheticAdd",
                    Generation.Gen5,
                    SyntheticExports.Add),
            ]),
            CodeAddress,
            guestThreadHandle: 0xCAFE,
            useDedicatedHostThreads: true,
            new CpuExecutionOptions { ImportTraceLimit = 1 }));

        AssertSuccessful(execution);
        Assert.Contains($"nid={AddNid}", execution.ImportTrace, StringComparison.Ordinal);
        Assert.Contains("thread=0x000000000000CAFE", execution.ImportTrace, StringComparison.Ordinal);
    }

    [HostX64Fact]
    public async Task NativeImportTraceRetainsThreadRegisteredDuringImport()
    {
        if (await NativeTestProcess.RunIfNeededAsync(typeof(NativeImportBridgeTests)))
        {
            return;
        }

        const ulong registeredThreadHandle = 0xE17E;
        byte[] code =
        [
            0xE8, 0xFB, 0x00, 0x00, 0x00, // call ImportAddress
            0xE8, 0x06, 0x01, 0x00, 0x00, // call SecondImportAddress
            0x31, 0xC0,                   // xor eax, eax
            0xC3,                         // ret
        ];
        var execution = SyntheticNativeGuest.ExecuteModuleInitializer(
            code,
            Generation.Gen5,
            "synthetic-registered-thread-trap",
            new Dictionary<ulong, string>
            {
                [ImportAddress] = RegisterExternalThreadNid,
                [SecondImportAddress] = AddNid,
            },
            moduleManager => moduleManager.RegisterExports(
            [
                new ExportedFunction(
                    "libSyntheticTest",
                    RegisterExternalThreadNid,
                    "syntheticRegisterExternalThread",
                    Generation.Gen5,
                    context =>
                    {
                        GuestThreadExecution.Scheduler?.RegisterGuestThreadContext(
                            registeredThreadHandle,
                            context);
                        return context.SetReturn(OrbisGen2Result.ORBIS_GEN2_OK);
                    }),
                new ExportedFunction(
                    "libSyntheticTest",
                    AddNid,
                    "syntheticAdd",
                    Generation.Gen5,
                    SyntheticExports.Add),
            ]),
            CodeAddress,
            new CpuExecutionOptions { ImportTraceLimit = 1 });

        AssertSuccessful(execution);
        Assert.Contains($"nid={AddNid}", execution.ImportTrace, StringComparison.Ordinal);
        Assert.Contains($"thread=0x{registeredThreadHandle:X16}", execution.ImportTrace, StringComparison.Ordinal);
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
    public async Task DlsymApplicationHeapAllocatorPreservesAlignmentSizeAbi()
    {
        if (await NativeTestProcess.RunIfNeededAsync(typeof(NativeImportBridgeTests)))
        {
            return;
        }

        var code = CreateDlsymTwoArgumentCallProbe(
            "scriptingGetMem",
            moduleHandle: 0,
            firstArgument: 0x1000,
            secondArgument: 0x40000,
            expectedResult: ApplicationHeapAllocationSentinel);

        SyntheticExports.ApplicationHeapAlignment = 0;
        SyntheticExports.ApplicationHeapSize = 0;
        var execution = SyntheticNativeGuest.ExecuteModuleInitializer(
            code,
            Generation.Gen5,
            "synthetic-dlsym-application-heap",
            new Dictionary<ulong, string>
            {
                [DlsymImportAddress] = "LwG8g3niqwA",
            },
            moduleManager =>
            {
                Assert.Equal(
                    1,
                    moduleManager.RegisterExports(
                    [
                        new ExportedFunction(
                            "libc",
                            MemalignNid,
                            "memalign",
                            Generation.Gen5,
                            SyntheticExports.ApplicationHeapAllocate),
                    ]));
            },
            CodeAddress);

        AssertSuccessful(execution);
        Assert.Equal(0x1000UL, SyntheticExports.ApplicationHeapAlignment);
        Assert.Equal(0x40000UL, SyntheticExports.ApplicationHeapSize);
    }

    [HostX64Fact]
    public async Task DlsymResolvesUncataloguedExportFromRequestedModule()
    {
        if (await NativeTestProcess.RunIfNeededAsync(typeof(NativeImportBridgeTests)))
        {
            return;
        }

        KernelModuleRegistry.Reset();
        try
        {
            var moduleHandle = KernelModuleRegistry.RegisterModule(
                "/app0/Media/Plugins/plugin.prx",
                baseAddress: CodeAddress,
                size: 0x1000,
                entryPoint: CodeAddress,
                isMain: false);
            KernelModuleRegistry.RegisterModuleSymbols(
                moduleHandle,
                new Dictionary<string, ulong>
                {
                    [PluginInitializeNid] = PluginFunctionAddress,
                });

            var code = CreateDlsymTwoArgumentCallProbe(
                "plugin.initialize",
                moduleHandle,
                firstArgument: 0xA5,
                secondArgument: 0x5A,
                expectedResult: ApplicationHeapAllocationSentinel);
            byte[] pluginFunction =
            [
                0x81, 0xFF, 0xA5, 0x00, 0x00, 0x00,   // cmp edi, 0xA5
                0x75, 0x13,                           // jne failure
                0x81, 0xFE, 0x5A, 0x00, 0x00, 0x00,   // cmp esi, 0x5A
                0x75, 0x0B,                           // jne failure
                0x48, 0xB8, 0x00, 0x00, 0x00, 0x00,
                0x00, 0x00, 0x00, 0x00,               // mov rax, expectedResult
                0xC3,                                 // ret
                0x0F, 0x0B,                           // failure: ud2
            ];
            BinaryPrimitives.WriteUInt64LittleEndian(
                pluginFunction.AsSpan(18, 8),
                ApplicationHeapAllocationSentinel);
            pluginFunction.CopyTo(code, 0x300);
            var execution = SyntheticNativeGuest.ExecuteModuleInitializer(
                code,
                Generation.Gen5,
                "synthetic-module-dlsym",
                new Dictionary<ulong, string>
                {
                    [DlsymImportAddress] = "LwG8g3niqwA",
                },
                configureModules: null,
                CodeAddress);

            AssertSuccessful(execution);
        }
        finally
        {
            KernelModuleRegistry.Reset();
        }
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

    [HostX64Fact]
    public async Task ScalarLeafDoesNotMaterializeGuestXmmArguments()
    {
        if (await NativeTestProcess.RunIfNeededAsync(typeof(NativeImportBridgeTests)))
        {
            return;
        }

        const ulong guestXmm0 = 0x1234_5678;
        SyntheticExports.ObservedScalarLeafXmm0 = ulong.MaxValue;
        byte[] code =
        [
            0xB8, 0x78, 0x56, 0x34, 0x12, // mov eax, 0x12345678
            0x66, 0x0F, 0x6E, 0xC0,       // movd xmm0, eax
            0xE8, 0xF2, 0x00, 0x00, 0x00, // call ImportAddress
            0x31, 0xC0,                   // xor eax, eax
            0xC3,                         // ret
        ];
        var execution = SyntheticNativeGuest.ExecuteModuleInitializer(
            code,
            Generation.Gen5,
            "synthetic-scalar-leaf-xmm-elision",
            new Dictionary<ulong, string> { [ImportAddress] = ScalarLeafProbeNid },
            moduleManager => moduleManager.RegisterExports(
            [
                new ExportedFunction(
                    "libKernel",
                    ScalarLeafProbeNid,
                    "syntheticScalarLeafProbe",
                    Generation.Gen5,
                    SyntheticExports.ScalarLeafProbe),
            ]),
            CodeAddress);

        AssertSuccessful(execution);
        Assert.NotEqual(guestXmm0, SyntheticExports.ObservedScalarLeafXmm0);
    }

    [HostX64Fact]
    public async Task ReusedNativeWorkerSurvivesRepeatedInPlaceWaits()
    {
        if (await NativeTestProcess.RunIfNeededAsync(typeof(NativeImportBridgeTests)))
        {
            return;
        }

        const int executionCount = 64;
        SyntheticExports.BlockingYieldCalls = 0;
        byte[] code =
        [
            0xE8, 0xFB, 0x00, 0x00, 0x00, // call ImportAddress
            0xE8, 0x06, 0x01, 0x00, 0x00, // call SecondImportAddress
            0xB8, 0x01, 0x00, 0x00, 0x00, // failure: mov eax, 1
            0xC3,                         // ret
        ];
        var executions = SyntheticNativeGuest.ExecuteModuleInitializers(
            code,
            Generation.Gen5,
            "synthetic-repeated-blocking-yield",
            executionCount,
            new Dictionary<ulong, string>
            {
                [ImportAddress] = AddNid,
                [SecondImportAddress] = BlockingYieldNid,
            },
            moduleManager =>
            {
                var registered = moduleManager.RegisterExports(
                    SharpEmu.Core.Tests.Generated.SysAbiExportRegistry.CreateExports(
                        Generation.Gen5));
                Assert.True(registered > 0);
                Assert.True(moduleManager.TryGetExport(BlockingYieldNid, out _));
            },
            CodeAddress,
            guestThreadHandle: 0xB10C,
            useDedicatedHostThreads: true);

        Assert.Equal(executionCount, executions.Count);
        Assert.All(executions, AssertSuccessful);
        Assert.Equal(executionCount, SyntheticExports.BlockingYieldCalls);
    }

    [HostX64Fact]
    public async Task HostShutdownUnwindsActiveNativeGuestWithoutFailFast()
    {
        if (await NativeTestProcess.RunIfNeededAsync(typeof(NativeImportBridgeTests)))
        {
            return;
        }

        SyntheticExports.BlockingYieldCalls = 0;
        byte[] code =
        [
            0xE8, 0xFB, 0x00, 0x00, 0x00, // loop: call ImportAddress
            0xEB, 0xF9,                   // jmp loop
        ];
        var shutdownRequest = Task.Run(() =>
        {
            Assert.True(
                SpinWait.SpinUntil(
                    () => Volatile.Read(ref SyntheticExports.BlockingYieldCalls) >= 4,
                    TimeSpan.FromSeconds(10)),
                "Synthetic guest did not reach the import loop.");
            HostSessionControl.RequestShutdown("synthetic-native-shutdown");
        });

        var execution = Assert.Single(SyntheticNativeGuest.ExecuteModuleInitializers(
            code,
            Generation.Gen5,
            "synthetic-host-shutdown",
            executionCount: 1,
            new Dictionary<ulong, string> { [ImportAddress] = BlockingYieldNid },
            moduleManager =>
            {
                var registered = moduleManager.RegisterExports(
                    SharpEmu.Core.Tests.Generated.SysAbiExportRegistry.CreateExports(
                        Generation.Gen5));
                Assert.True(registered > 0);
                Assert.True(moduleManager.TryGetExport(BlockingYieldNid, out _));
            },
            CodeAddress,
            guestThreadHandle: 0xCAFE,
            useDedicatedHostThreads: true));
        await shutdownRequest.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(OrbisGen2Result.ORBIS_GEN2_ERROR_CPU_TRAP, execution.Result);
    }

    [HostX64Fact]
    public async Task ImportAfterSwitchingToRegisteredStackRefreshesExceptionBoundary()
    {
        if (await NativeTestProcess.RunIfNeededAsync(typeof(NativeImportBridgeTests)))
        {
            return;
        }

        const ulong alternateStackStart = 0x0000_7FFE_0000_0000;
        const ulong alternateStackSize = 0x1_0000;
        var alternateRsp = alternateStackStart + alternateStackSize - 0x10;
        SyntheticExports.RegisteredStackObserved = false;
        byte[] code =
        [
            0x49, 0x89, 0xE4,                      // mov r12, rsp
            0x48, 0xBC,
            .. BitConverter.GetBytes(alternateRsp), // mov rsp, alternateRsp
            0xE8, 0xEE, 0x00, 0x00, 0x00,          // call ImportAddress
            0x4C, 0x89, 0xE4,                      // mov rsp, r12
            0x31, 0xC0,                            // xor eax, eax
            0xC3,                                  // ret
        ];

        var execution = SyntheticNativeGuest.ExecuteModuleInitializer(
            code,
            Generation.Gen5,
            "synthetic-registered-stack-refresh",
            new Dictionary<ulong, string> { [ImportAddress] = RegisteredStackProbeNid },
            moduleManager => moduleManager.RegisterExports(
                [
                    new ExportedFunction(
                        "libSyntheticTest",
                        RegisteredStackProbeNid,
                        "syntheticRegisteredStackProbe",
                        Generation.Gen5,
                        SyntheticExports.RegisteredStackProbe),
                ]),
            codeAddress: CodeAddress,
            executionOptions: default,
            configureMemory: memory =>
            {
                Assert.Equal(
                    alternateStackStart,
                    memory.AllocateAt(
                        alternateStackStart,
                        alternateStackSize,
                        executable: false,
                        allowAlternative: false));
                memory.RegisterStackRange(alternateStackStart, alternateStackSize);
            });

        AssertSuccessful(execution);
        Assert.True(SyntheticExports.RegisteredStackObserved);
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

    private static byte[] CreateDlsymTwoArgumentCallProbe(
        string symbolName,
        int moduleHandle,
        uint firstArgument,
        uint secondArgument,
        ulong expectedResult)
    {
        var symbolBytes = System.Text.Encoding.ASCII.GetBytes(symbolName + '\0');
        const int symbolCapacity = 0x80;
        if (symbolBytes.Length > symbolCapacity)
        {
            throw new ArgumentException("Synthetic dlsym symbol is too long.", nameof(symbolName));
        }

        var code = new byte[0x400];
        byte[] instructions =
        [
            0xBF, 0x00, 0x00, 0x00, 0x00,             // mov edi, moduleHandle
            0x48, 0xBE, 0x00, 0x00, 0x00, 0x00,
            0x00, 0x00, 0x00, 0x00,                   // mov rsi, DlsymSymbolAddress
            0x48, 0xBA, 0x00, 0x00, 0x00, 0x00,
            0x00, 0x00, 0x00, 0x00,                   // mov rdx, DlsymResultAddress
            0x48, 0xB8, 0x00, 0x00, 0x00, 0x00,
            0x00, 0x00, 0x00, 0x00,                   // mov rax, DlsymImportAddress
            0xFF, 0xD0,                               // call rax
            0x85, 0xC0,                               // test eax, eax
            0x75, 0x30,                               // jne failure
            0x48, 0xBB, 0x00, 0x00, 0x00, 0x00,
            0x00, 0x00, 0x00, 0x00,                   // mov rbx, DlsymResultAddress
            0x48, 0x8B, 0x03,                         // mov rax, [rbx]
            0x48, 0x85, 0xC0,                         // test rax, rax
            0x74, 0x1E,                               // jz failure
            0xBF, 0x00, 0x00, 0x00, 0x00,             // mov edi, firstArgument
            0xBE, 0x00, 0x00, 0x00, 0x00,             // mov esi, secondArgument
            0xFF, 0xD0,                               // call rax
            0x48, 0xB9, 0x00, 0x00, 0x00, 0x00,
            0x00, 0x00, 0x00, 0x00,                   // mov rcx, expectedResult
            0x48, 0x39, 0xC8,                         // cmp rax, rcx
            0x75, 0x03,                               // jne failure
            0x31, 0xC0,                               // xor eax, eax
            0xC3,                                     // ret
            0x0F, 0x0B,                               // failure: ud2
        ];
        BinaryPrimitives.WriteInt32LittleEndian(instructions.AsSpan(1, 4), moduleHandle);
        BinaryPrimitives.WriteUInt64LittleEndian(instructions.AsSpan(7, 8), DlsymSymbolAddress);
        BinaryPrimitives.WriteUInt64LittleEndian(instructions.AsSpan(17, 8), DlsymResultAddress);
        BinaryPrimitives.WriteUInt64LittleEndian(instructions.AsSpan(27, 8), DlsymImportAddress);
        BinaryPrimitives.WriteUInt64LittleEndian(instructions.AsSpan(43, 8), DlsymResultAddress);
        BinaryPrimitives.WriteUInt32LittleEndian(instructions.AsSpan(60, 4), firstArgument);
        BinaryPrimitives.WriteUInt32LittleEndian(instructions.AsSpan(65, 4), secondArgument);
        BinaryPrimitives.WriteUInt64LittleEndian(instructions.AsSpan(73, 8), expectedResult);
        instructions.CopyTo(code, 0);
        symbolBytes.CopyTo(code, 0x200);
        return code;
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
        public static ulong ObservedScalarLeafXmm0;

        public static int ScalarLeafProbe(CpuContext context)
        {
            context.GetXmmRegister(0, out ObservedScalarLeafXmm0, out _);
            return context.SetReturn(OrbisGen2Result.ORBIS_GEN2_OK);
        }

        public static ulong ApplicationHeapAlignment { get; set; }

        public static ulong ApplicationHeapSize { get; set; }

        public static bool RegisteredStackObserved { get; set; }

        public static int ApplicationHeapAllocate(CpuContext context)
        {
            ApplicationHeapAlignment = context[CpuRegister.Rdi];
            ApplicationHeapSize = context[CpuRegister.Rsi];
            context[CpuRegister.Rax] = ApplicationHeapAllocationSentinel;
            return (int)OrbisGen2Result.ORBIS_GEN2_OK;
        }

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

        public static int RegisteredStackProbe(CpuContext context)
        {
            RegisteredStackObserved = DirectExecutionBackend.IsActiveGuestStackPointer(
                context[CpuRegister.Rsp]);
            return context.SetReturn(0);
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

        public static int BlockingYieldCalls;

        [SysAbiExport(
            Nid = BlockingYieldNid,
            ExportName = "syntheticBlockingYield",
            Target = Generation.Gen5,
            LibraryName = "libSyntheticTest")]
        public static int BlockingYield(CpuContext context)
        {
            Interlocked.Increment(ref BlockingYieldCalls);
            Thread.Sleep(1);
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

    private sealed class CollectingLogSink : ISharpEmuLogSink
    {
        public List<string> Messages { get; } = [];

        public void Write(in LogEntry entry) => Messages.Add(entry.Message);
    }
}
