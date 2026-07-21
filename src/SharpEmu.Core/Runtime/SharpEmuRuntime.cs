// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Core;
using SharpEmu.Core.Cpu;
using SharpEmu.Core.Cpu.Disasm;
using SharpEmu.Core.Loader;
using SharpEmu.Core.Memory;
using SharpEmu.HLE;
using SharpEmu.HLE.Host;
using SharpEmu.Libs.VideoOut;
using SharpEmu.Libs.Kernel;
using SharpEmu.Libs.AppContent;
using SharpEmu.Libs.SaveData;
using SharpEmu.Libs.Fiber;
using SharpEmu.Libs.SystemService;
using SharpEmu.Logging;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.IO;
using Iced.Intel;

namespace SharpEmu.Core.Runtime;

public sealed class SharpEmuRuntime : ISharpEmuRuntime
{
    private static readonly SharpEmuLogger Log = SharpEmuLog.For("Runtime");

    private static readonly HashSet<string> PreloadSkipModules = new(StringComparer.OrdinalIgnoreCase)
    {
        "libkernel.prx",
        "libkernel_sys.prx",
    };

    private readonly ISelfLoader _selfLoader;
    private readonly IVirtualMemory _virtualMemory;
    private readonly ICpuDispatcher _cpuDispatcher;
    private readonly IModuleManager _moduleManager;
    private readonly ISymbolCatalog _symbolCatalog;
    private readonly CpuExecutionOptions _cpuExecutionOptions;
    private readonly IFileSystem _fileSystem;
    private readonly bool _allowExecution;
    private readonly List<ModuleInitializerExecution> _moduleInitializerExecutions = [];
    private bool _disposed;

    public string? LastExecutionDiagnostics { get; private set; }

    public string? LastExecutionTrace { get; private set; }

    public IReadOnlyList<CpuImportTraceEntry>? LastExecutionTraceEntries { get; private set; }

    public string? LastSessionSummary { get; private set; }

    public string? LastBasicBlockTrace { get; private set; }

    public string? LastMilestoneLog { get; private set; }

    public CpuSessionSummary? LastCpuSessionSummary { get; private set; }

    public CpuTrapInfo? LastCpuTrapInfo { get; private set; }

    public CpuMemoryFaultInfo? LastCpuMemoryFaultInfo { get; private set; }

    public CpuNotImplementedInfo? LastCpuNotImplementedInfo { get; private set; }

    public Ps5ApplicationMetadata? LastApplicationMetadata { get; private set; }

    public SelfImage? LastLoadedImage { get; private set; }

    public PreparedApplication? LastPreparedApplication { get; private set; }

    public IReadOnlyList<ModuleInitializerExecution> LastModuleInitializerExecutions =>
        _moduleInitializerExecutions;

    public SharpEmuRuntime(
        ISelfLoader selfLoader,
        IVirtualMemory virtualMemory,
        ICpuDispatcher cpuDispatcher,
        IModuleManager moduleManager,
        ISymbolCatalog? symbolCatalog = null,
        CpuExecutionOptions cpuExecutionOptions = default,
        IFileSystem? fileSystem = null,
        bool allowExecution = true)
    {
        _selfLoader = selfLoader ?? throw new ArgumentNullException(nameof(selfLoader));
        _virtualMemory = virtualMemory ?? throw new ArgumentNullException(nameof(virtualMemory));
        _cpuDispatcher = cpuDispatcher ?? throw new ArgumentNullException(nameof(cpuDispatcher));
        _moduleManager = moduleManager ?? throw new ArgumentNullException(nameof(moduleManager));
        _symbolCatalog = symbolCatalog ?? Aerolib.Empty;
        _cpuExecutionOptions = new CpuExecutionOptions
        {
            CpuEngine = cpuExecutionOptions.CpuEngine,
            StrictDynlibResolution = cpuExecutionOptions.StrictDynlibResolution,
            ImportTraceLimit = Math.Max(0, cpuExecutionOptions.ImportTraceLimit),
        };
        _fileSystem = fileSystem ?? new PhysicalFileSystem();
        _allowExecution = allowExecution;
    }

    public static ISharpEmuRuntime CreateDefault(SharpEmuRuntimeOptions options = default)
    {
        var hostPlatform = HostPlatform.Current;
        return CreateWithMemory(
            new PhysicalVirtualMemory(hostPlatform.Memory),
            options,
            allowExecution: true,
            hostPlatform: hostPlatform);
    }

    public static ISharpEmuRuntime CreateForInspection(SharpEmuRuntimeOptions options = default)
    {
        return CreateWithMemory(new VirtualMemory(), options, allowExecution: false);
    }

    private static ISharpEmuRuntime CreateWithMemory(
        IVirtualMemory virtualMemory,
        SharpEmuRuntimeOptions options,
        bool allowExecution,
        IHostPlatform? hostPlatform = null)
    {
        var cpuExecutionOptions = new CpuExecutionOptions
        {
            CpuEngine = options.CpuEngine,
            StrictDynlibResolution = options.StrictDynlibResolution,
            ImportTraceLimit = Math.Max(0, options.ImportTraceLimit),
        };
        var moduleManager = new ModuleManager();
        moduleManager.RegisterExports(
            SharpEmu.Generated.SysAbiExportRegistry.CreateExports(
                Generation.Gen4 | Generation.Gen5));
        moduleManager.Freeze();

        var fileSystem = new PhysicalFileSystem();

        return new SharpEmuRuntime(
            new SelfLoader(),
            virtualMemory,
            new CpuDispatcher(virtualMemory, moduleManager, hostPlatform: hostPlatform),
            moduleManager,
            Aerolib.Instance,
            cpuExecutionOptions,
            fileSystem,
            allowExecution);
    }

    public SelfImage LoadImage(string ebootPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ebootPath);

        LastLoadedImage = null;
        LastApplicationMetadata = null;
        LastPreparedApplication = null;

        var fullPath = Path.GetFullPath(ebootPath);
        if (!File.Exists(fullPath))
        {
            throw new FileNotFoundException("Executable file was not found.", fullPath);
        }

        var fileInfo = new FileInfo(fullPath);
        if (fileInfo.Length > int.MaxValue)
        {
            throw new NotSupportedException("Images larger than 2 GB are not currently supported.");
        }

        var bytes = GC.AllocateUninitializedArray<byte>((int)fileInfo.Length);
        using (var stream = File.OpenRead(fullPath))
        {
            stream.ReadExactly(bytes);
        }

        var mountRoot = Path.GetDirectoryName(fullPath);

        var image = _selfLoader.Load(bytes.AsSpan(), _virtualMemory, _moduleManager, _fileSystem, mountRoot);
        LastLoadedImage = image;
        LastApplicationMetadata = new Ps5ApplicationMetadata(
            image.Title,
            image.TitleId,
            image.ContentId,
            image.Version);
        return image;
    }

    public PreparedApplication PrepareApplication(string ebootPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ebootPath);

        var normalizedEbootPath = Path.GetFullPath(ebootPath);
        using var app0Binding = BindApp0Root(normalizedEbootPath);
        ResetRunState();
        return PrepareApplicationCore(normalizedEbootPath);
    }

    public OrbisGen2Result Run(
        string ebootPath,
        Action? onExecutionStarting = null,
        Action<PreparedApplication>? onApplicationPrepared = null)
    {
        if (!_allowExecution)
        {
            throw new InvalidOperationException(
                "This runtime was created for image inspection and cannot execute guest code.");
        }

        var normalizedEbootPath = Path.GetFullPath(ebootPath);
        using var app0Binding = BindApp0Root(normalizedEbootPath);
        ResetRunState();
        var preparedApplication = PrepareApplicationCore(normalizedEbootPath);
        onApplicationPrepared?.Invoke(preparedApplication);
        var image = preparedApplication.MainImage;
        var loadedModuleImages = preparedApplication.Modules;
        var generation = preparedApplication.Generation;
        var activeImportStubs = preparedApplication.ImportStubs;
        var activeRuntimeSymbols = preparedApplication.RuntimeSymbols;
        var processImageName = preparedApplication.ProcessImageName;
        var initializerResult = RunAllInitializers(
            image,
            loadedModuleImages,
            generation,
            activeImportStubs,
            activeRuntimeSymbols,
            processImageName);
        if (initializerResult is { } failedInitializerResult)
        {
            Log.Error($"Initializer dispatch failed: {failedInitializerResult}");
            CaptureCpuOutcome();
            LastExecutionTrace = _cpuDispatcher.LastImportResolutionTrace;
            LastExecutionTraceEntries = _cpuDispatcher.LastImportTraceEntries;
            LastMilestoneLog = _cpuDispatcher.LastMilestoneLog;
            LastSessionSummary = BuildSessionSummary(_cpuDispatcher.LastSessionSummary);
            LastBasicBlockTrace = _cpuDispatcher.LastBasicBlockTrace;
            return failedInitializerResult;
        }

        onExecutionStarting?.Invoke();
        Log.Info($"Dispatching, gen: {generation}");
        Log.Debug($"About to call DispatchEntry with entryPoint=0x{image.EntryPoint:X16}");

        var result = _cpuDispatcher.DispatchEntry(
            image.EntryPoint,
            generation,
            activeImportStubs,
            activeRuntimeSymbols,
            processImageName,
            _cpuExecutionOptions);

        Log.Info($"DispatchEntry returned: {result}");
        Log.Info($"Dispatch result: {result}");
        CaptureCpuOutcome();
        LastExecutionTrace = _cpuDispatcher.LastImportResolutionTrace;
        LastExecutionTraceEntries = _cpuDispatcher.LastImportTraceEntries;
        LastMilestoneLog = _cpuDispatcher.LastMilestoneLog;
        LastSessionSummary = BuildSessionSummary(_cpuDispatcher.LastSessionSummary);
        LastBasicBlockTrace = _cpuDispatcher.LastBasicBlockTrace;
        if (result == OrbisGen2Result.ORBIS_GEN2_ERROR_CPU_TRAP && _cpuDispatcher.LastTrapInfo is { } trapInfo)
        {
            var opcodeBytes = ReadOpcodePreview(trapInfo.InstructionPointer, 8);
            var decodedTrapText = string.Empty;
            var ud2Hint = string.Empty;
            var enrichedTrapInfo = trapInfo;
            if (TryDecodeInstructionAt(trapInfo.InstructionPointer, out var trapInstruction))
            {
                decodedTrapText = BuildDecodedInstructionFields(in trapInstruction);
                enrichedTrapInfo = enrichedTrapInfo.WithDecodedInstruction(
                    IcedDecoder.FormatBytes(trapInstruction.Bytes),
                    trapInstruction.Length,
                    trapInstruction.Mnemonic,
                    trapInstruction.Text,
                    trapInstruction.FlowControl.ToString());
                if (string.Equals(trapInstruction.Mnemonic, "Ud2", StringComparison.OrdinalIgnoreCase))
                {
                    ud2Hint = ", trap=ud2";
                }
            }
            else if (opcodeBytes.StartsWith("0F 0B", StringComparison.Ordinal))
            {
                ud2Hint = ", trap=ud2";
            }

            var stackFrames = CaptureTrapStackFrames(
                _virtualMemory,
                trapInfo.Registers,
                CaptureTrapCodeWindow,
                address => CaptureTrapStackCandidateInstructions(_virtualMemory, address));
            enrichedTrapInfo = enrichedTrapInfo.WithStackFrames(stackFrames);
            if (CaptureTrapCodeWindow(trapInfo.InstructionPointer) is { } codeWindow)
            {
                enrichedTrapInfo = enrichedTrapInfo.WithCodeWindow(codeWindow);
            }
            if (CaptureTrapStackWindow(_virtualMemory, trapInfo.Registers) is { } stackWindow)
            {
                enrichedTrapInfo = enrichedTrapInfo.WithStackWindow(stackWindow);
            }
            enrichedTrapInfo = enrichedTrapInfo.WithStackCodeCandidates(
                CaptureTrapStackCodeCandidates(
                    _virtualMemory,
                    trapInfo.Registers,
                    address => CaptureTrapStackCandidateCodeWindow(_virtualMemory, address),
                    address => CaptureTrapStackCandidateInstructions(_virtualMemory, address)));
            LastCpuTrapInfo = enrichedTrapInfo;

            var longModeHint = IsInvalidLongModeOpcode(trapInfo.Opcode)
                ? ", hint=invalid opcode for x64 long mode; likely wrong jump target or decode desync"
                : string.Empty;
            var exceptionText = trapInfo.ExceptionCode is { } exceptionCode
                ? $", exception=0x{exceptionCode:X8}"
                : string.Empty;
            var accessText = trapInfo.AccessAddress is { } accessAddress &&
                trapInfo.AccessKind is { } accessKind
                    ? $", access={accessKind.ToString().ToLowerInvariant()}@0x{accessAddress:X16}"
                    : string.Empty;
            var registerText = trapInfo.Registers is { } registers
                ? $", rax=0x{registers.Rax:X16}, rdi=0x{registers.Rdi:X16}, " +
                  $"rsp=0x{registers.Rsp:X16}, rbp=0x{registers.Rbp:X16}"
                : string.Empty;
            var threadText = $", thread=0x{trapInfo.GuestThreadHandle:X16}";

            var hint = string.Empty;
            if (image.IsSelf &&
                activeImportStubs.Count == 0 &&
                trapInfo.InstructionPointer == 0 &&
                trapInfo.Opcode == 0xCC)
            {
                hint = ", hint=SELF appears encrypted or unresolved; use a decrypted ELF/FSELF image";
            }

            var transferText = string.Empty;
            if (_cpuDispatcher.LastControlTransferInfo is { } transferInfo)
            {
                var transferMode = transferInfo.IsIndirect ? "indirect" : "direct";
                var transferBytes = ReadOpcodePreview(transferInfo.SourceInstructionPointer, 8);
                var transferDecodedText = TryDecodeInstructionAt(transferInfo.SourceInstructionPointer, out var transferInstruction)
                    ? BuildDecodedInstructionFields(in transferInstruction, fieldPrefix: "src_inst")
                    : string.Empty;
                transferText =
                    $", last_transfer={transferInfo.Kind}:{transferMode} src=0x{transferInfo.SourceInstructionPointer:X16} op=0x{transferInfo.Opcode:X2} bytes={transferBytes} dst=0x{transferInfo.TargetInstructionPointer:X16}{transferDecodedText}";
            }

            var ripStubText = activeImportStubs.TryGetValue(trapInfo.InstructionPointer, out var trapStubNid)
                ? $", rip_stub={trapStubNid}"
                : string.Empty;
            var diagnosticsBuilder = new StringBuilder(1024);
            diagnosticsBuilder.Append(
                $"CPU trap at RIP=0x{trapInfo.InstructionPointer:X16}, opcode=0x{trapInfo.Opcode:X2}, bytes={opcodeBytes}{decodedTrapText}{exceptionText}{accessText}{registerText}{threadText}, import_stubs={activeImportStubs.Count}{ud2Hint}{longModeHint}{hint}{ripStubText}{transferText}");
            if (!string.IsNullOrWhiteSpace(_cpuDispatcher.LastRecentControlTransferTrace))
            {
                diagnosticsBuilder.AppendLine();
                diagnosticsBuilder.Append("Recent transfers:");
                diagnosticsBuilder.AppendLine();
                diagnosticsBuilder.Append(_cpuDispatcher.LastRecentControlTransferTrace);
            }

            if (!string.IsNullOrWhiteSpace(_cpuDispatcher.LastRecentInstructionWindow))
            {
                diagnosticsBuilder.AppendLine();
                diagnosticsBuilder.Append("Recent instructions:");
                diagnosticsBuilder.AppendLine();
                diagnosticsBuilder.Append(_cpuDispatcher.LastRecentInstructionWindow);
            }

            LastExecutionDiagnostics = diagnosticsBuilder.ToString();
        }
        else if (result == OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT && _cpuDispatcher.LastMemoryFaultInfo is { } faultInfo)
        {
            var opcodeText = faultInfo.Opcode.HasValue ? $"0x{faultInfo.Opcode.Value:X2}" : "??";
            var decodedFaultText = string.Empty;
            if (_cpuExecutionOptions.EnableDisasmDiagnostics && TryDecodeInstructionAt(faultInfo.InstructionPointer, out var faultInstruction))
            {
                decodedFaultText = BuildDecodedInstructionFields(in faultInstruction);
                if (!faultInfo.Opcode.HasValue && faultInstruction.Bytes.Length > 0)
                {
                    opcodeText = $"0x{faultInstruction.Bytes[0]:X2}";
                }
            }

            var accessType = faultInfo.Access.IsWrite ? "write" : "read";
            var transferText = string.Empty;
            if (_cpuDispatcher.LastControlTransferInfo is { } transferInfo)
            {
                var transferMode = transferInfo.IsIndirect ? "indirect" : "direct";
                var transferBytes = ReadOpcodePreview(transferInfo.SourceInstructionPointer, 8);
                var transferDecodedText = TryDecodeInstructionAt(transferInfo.SourceInstructionPointer, out var transferInstruction)
                    ? BuildDecodedInstructionFields(in transferInstruction, fieldPrefix: "src_inst")
                    : string.Empty;
                var rbxTarget = TryReadUInt64At(transferInfo.Rbx, out var rbxDeref)
                    ? $"*rbx=0x{rbxDeref:X16}"
                    : "*rbx=??";
                transferText =
                    $", last_transfer={transferInfo.Kind}:{transferMode} src=0x{transferInfo.SourceInstructionPointer:X16} op=0x{transferInfo.Opcode:X2} bytes={transferBytes} dst=0x{transferInfo.TargetInstructionPointer:X16}{transferDecodedText} rax=0x{transferInfo.Rax:X16} rbx=0x{transferInfo.Rbx:X16} {rbxTarget} rsp=0x{transferInfo.Rsp:X16} rbp=0x{transferInfo.Rbp:X16}";
            }

            var ripStubText = activeImportStubs.TryGetValue(faultInfo.InstructionPointer, out var faultStubNid)
                ? $", rip_stub={faultStubNid}"
                : string.Empty;

            LastExecutionDiagnostics =
                $"Memory fault at RIP=0x{faultInfo.InstructionPointer:X16}, opcode={opcodeText}{decodedFaultText}, {accessType}@0x{faultInfo.Access.Address:X16} size={faultInfo.Access.Size}, import_stubs={activeImportStubs.Count}{ripStubText}{transferText}";
        }
        else if (result == OrbisGen2Result.ORBIS_GEN2_ERROR_NOT_IMPLEMENTED && _cpuDispatcher.LastNotImplementedInfo is { } notImplementedInfo)
        {
            var inferredNid = notImplementedInfo.Nid;
            var decodedNotImplementedText = TryDecodeInstructionAt(notImplementedInfo.InstructionPointer, out var notImplementedInstruction)
                ? BuildDecodedInstructionFields(in notImplementedInstruction)
                : string.Empty;
            var ripStubText = string.Empty;
            if (activeImportStubs.TryGetValue(notImplementedInfo.InstructionPointer, out var ripStubNid))
            {
                ripStubText = $", rip_stub={ripStubNid}";
                if (string.IsNullOrWhiteSpace(inferredNid))
                {
                    inferredNid = ripStubNid;
                }
            }

            var inferredExportName = notImplementedInfo.ExportName;
            var inferredLibraryName = notImplementedInfo.LibraryName;
            if (!string.IsNullOrWhiteSpace(inferredNid) &&
                _moduleManager.TryGetExport(inferredNid, out var export))
            {
                inferredExportName = string.IsNullOrWhiteSpace(inferredExportName) ? export.Name : inferredExportName;
                inferredLibraryName = string.IsNullOrWhiteSpace(inferredLibraryName) ? export.LibraryName : inferredLibraryName;
            }

            var nidText = string.IsNullOrWhiteSpace(inferredNid) ? "?" : inferredNid;
            var exportText = string.IsNullOrWhiteSpace(inferredExportName) ? "?" : inferredExportName;
            var libraryText = string.IsNullOrWhiteSpace(inferredLibraryName) ? "?" : inferredLibraryName;
            var detailText = string.IsNullOrWhiteSpace(notImplementedInfo.Detail) ? string.Empty : $", detail={notImplementedInfo.Detail}";
            var transferText = string.Empty;
            if (_cpuDispatcher.LastControlTransferInfo is { } transferInfo)
            {
                var transferMode = transferInfo.IsIndirect ? "indirect" : "direct";
                var transferBytes = ReadOpcodePreview(transferInfo.SourceInstructionPointer, 8);
                var transferDecodedText = TryDecodeInstructionAt(transferInfo.SourceInstructionPointer, out var transferInstruction)
                    ? BuildDecodedInstructionFields(in transferInstruction, fieldPrefix: "src_inst")
                    : string.Empty;
                var transferStubText = activeImportStubs.TryGetValue(transferInfo.TargetInstructionPointer, out var transferStubNid)
                    ? $" stub={transferStubNid}"
                    : string.Empty;
                transferText =
                    $", last_transfer={transferInfo.Kind}:{transferMode} src=0x{transferInfo.SourceInstructionPointer:X16} op=0x{transferInfo.Opcode:X2} bytes={transferBytes} dst=0x{transferInfo.TargetInstructionPointer:X16}{transferDecodedText}{transferStubText}";
            }

            var aerolibText = string.Empty;
            if (!string.IsNullOrWhiteSpace(inferredExportName) &&
                _symbolCatalog.TryGetByExportName(inferredExportName, out var symbol))
            {
                aerolibText = $", aerolib_nid={symbol.Nid}";
            }
            else if (!string.IsNullOrWhiteSpace(inferredNid) &&
                     _symbolCatalog.TryGetByNid(inferredNid, out var symbolByNid))
            {
                aerolibText = $", aerolib_export={symbolByNid.ExportName}";
            }

            LastExecutionDiagnostics =
                $"Not implemented: source={notImplementedInfo.Source}, rip=0x{notImplementedInfo.InstructionPointer:X16}{decodedNotImplementedText}, nid={nidText}, export={exportText}, library={libraryText}, import_stubs={activeImportStubs.Count}{ripStubText}{aerolibText}{detailText}{transferText}";
        }

        return result;
    }

    private void CaptureCpuOutcome()
    {
        LastCpuSessionSummary = _cpuDispatcher.LastSessionSummary;
        LastCpuTrapInfo = _cpuDispatcher.LastTrapInfo;
        LastCpuMemoryFaultInfo = _cpuDispatcher.LastMemoryFaultInfo;
        LastCpuNotImplementedInfo = _cpuDispatcher.LastNotImplementedInfo;
    }

    private static void LogAppBundleInfo(string ebootPath, SelfImage image)
    {
        var executableName = Path.GetFileName(ebootPath);
        if (string.IsNullOrWhiteSpace(executableName))
        {
            executableName = "eboot.bin";
        }

        var displayName = string.IsNullOrWhiteSpace(image.Title) ? "(unknown)" : image.Title!.Trim();
        var titleId = string.IsNullOrWhiteSpace(image.TitleId) ? "(unknown)" : image.TitleId!.Trim();
        var version = string.IsNullOrWhiteSpace(image.Version) ? "(unknown)" : image.Version!.Trim();
        var contentId = string.IsNullOrWhiteSpace(image.ContentId) ? "(unknown)" : image.ContentId!.Trim();

        var builder = new StringBuilder();
        builder.AppendLine("App bundle info:");
        builder.AppendLine($"- Display name: {displayName}");
        builder.AppendLine($"- Version: {version}");
        builder.AppendLine($"- Title ID: {titleId}");
        builder.AppendLine($"- Content ID: {contentId}");
        builder.AppendLine($"- Executable: {executableName}");
        builder.Append("- Platform: PlayStation 5");
        Log.Info(builder.ToString());
    }

    private void ResetRunState()
    {
        LastExecutionDiagnostics = null;
        LastExecutionTrace = null;
        LastExecutionTraceEntries = null;
        LastSessionSummary = null;
        LastBasicBlockTrace = null;
        LastMilestoneLog = null;
        LastCpuSessionSummary = null;
        LastCpuTrapInfo = null;
        LastCpuMemoryFaultInfo = null;
        LastCpuNotImplementedInfo = null;
        LastApplicationMetadata = null;
        LastLoadedImage = null;
        LastPreparedApplication = null;
        _moduleInitializerExecutions.Clear();
        FiberExports.ResetRuntimeState();
        KernelPthreadLifecycle.ResetRuntimeState();
        KernelIoLifecycle.ResetRuntimeState();
        KernelEventObjectLifecycle.ResetRuntimeState();
        KernelMemoryLifecycle.ResetRuntimeState();
    }

    private PreparedApplication PrepareApplicationCore(string normalizedEbootPath)
    {
        Log.Info($"Loading: {normalizedEbootPath}");
        KernelModuleRegistry.Reset();
        var image = LoadImage(normalizedEbootPath);
        VideoOutExports.ConfigureApplicationInfo(image.Title, image.TitleId, image.Version, BuildInfo.CommitSha);
        SaveDataExports.ConfigureApplicationInfo(image.TitleId);
        SystemServiceExports.ConfigureApplicationInfo(image.TitleId);
        LogAppBundleInfo(normalizedEbootPath, image);
        RegisterLoadedModule(normalizedEbootPath, image, isMain: true, isSystemModule: false);
        KernelRuntimeCompatExports.ConfigureProcessProcParamAddress(image.ProcParamAddress);
        Log.Info($"Entry: 0x{image.EntryPoint:X16}");

        var generation = image.ElfHeader.AbiVersion == 2 ? Generation.Gen5 : Generation.Gen4;
        var activeImportStubs = new Dictionary<ulong, string>(image.ImportStubs);
        var activeRuntimeSymbols = new Dictionary<string, ulong>(image.RuntimeSymbols, StringComparer.Ordinal);
        var processImageName = Path.GetFileName(normalizedEbootPath);
        if (string.IsNullOrWhiteSpace(processImageName))
        {
            processImageName = "eboot.bin";
        }

        HleDataSymbols.ConfigureProcessImageName(processImageName);
        MergeKnownHleDataSymbols(activeRuntimeSymbols);
        var moduleLoadFailures = new List<ModuleLoadFailure>();
        var skippedModules = new List<SkippedModule>();
        var loadedModuleImages = LoadAdjacentSceModules(
            normalizedEbootPath,
            activeImportStubs,
            activeRuntimeSymbols,
            skippedModules,
            moduleLoadFailures);
        RebindImportedDataSymbols(image, loadedModuleImages, activeRuntimeSymbols);

        var preparedApplication = new PreparedApplication(
            image,
            loadedModuleImages,
            skippedModules,
            moduleLoadFailures,
            generation,
            activeImportStubs,
            activeRuntimeSymbols,
            processImageName);
        LastPreparedApplication = preparedApplication;
        return preparedApplication;
    }

    private static App0BindingScope? BindApp0Root(string normalizedEbootPath)
    {
        const string app0VariableName = "SHARPEMU_APP0_DIR";
        if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(app0VariableName)))
        {
            return null;
        }

        var app0Root = Path.GetDirectoryName(normalizedEbootPath);
        if (string.IsNullOrWhiteSpace(app0Root))
        {
            return null;
        }

        Environment.SetEnvironmentVariable(app0VariableName, app0Root);
        return new App0BindingScope(app0VariableName);
    }

    private sealed class App0BindingScope(string variableName) : IDisposable
    {
        private readonly string _variableName = variableName;
        private bool _disposed;

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            Environment.SetEnvironmentVariable(_variableName, null);
            _disposed = true;
        }
    }

    private OrbisGen2Result? RunAllInitializers(
        SelfImage mainImage,
        IReadOnlyList<PreparedModule> loadedModuleImages,
        Generation generation,
        IReadOnlyDictionary<ulong, string> activeImportStubs,
        IReadOnlyDictionary<string, ulong> activeRuntimeSymbols,
        string processImageName)
    {
        var moduleStartResult = RunPreloadedModuleInitializers(
            loadedModuleImages,
            generation,
            activeImportStubs,
            activeRuntimeSymbols);
        if (moduleStartResult is not null)
        {
            return moduleStartResult;
        }

        // On current PS5 dumps DT_INIT commonly resolves to imageBase+0x10, which is inside
        // the mapped ELF header rather than a callable guest routine. Startup must remain
        // guest-driven until the PS5 init/module ABI is identified precisely.
        return null;
    }

    private OrbisGen2Result? RunPreloadedModuleInitializers(
        IReadOnlyList<PreparedModule> loadedModuleImages,
        Generation generation,
        IReadOnlyDictionary<ulong, string> activeImportStubs,
        IReadOnlyDictionary<string, ulong> activeRuntimeSymbols)
    {
        for (var i = 0; i < loadedModuleImages.Count; i++)
        {
            var loadedModule = loadedModuleImages[i];
            if (!loadedModule.StartOnBoot)
            {
                continue;
            }

            var initializerEntryPoints = loadedModule.Image.InitializerFunctions;
            if (initializerEntryPoints.Count == 0)
            {
                continue;
            }

            var moduleName = Path.GetFileName(loadedModule.Path);
            if (string.IsNullOrWhiteSpace(moduleName))
            {
                moduleName = $"module#{i}";
            }

            Log.Info(
                $"Starting module {moduleName}: initializers={initializerEntryPoints.Count}, dt_init=0x{loadedModule.Image.InitFunctionEntryPoint:X16}");

            for (var initializerIndex = 0; initializerIndex < initializerEntryPoints.Count; initializerIndex++)
            {
                var initializerEntryPoint = initializerEntryPoints[initializerIndex];
                Log.Debug(
                    $"  Module initializer {moduleName}[{initializerIndex}] -> 0x{initializerEntryPoint:X16}");

                var result = _cpuDispatcher.DispatchModuleInitializer(
                    initializerEntryPoint,
                    generation,
                    activeImportStubs,
                    activeRuntimeSymbols,
                    moduleName,
                    _cpuExecutionOptions);
                _moduleInitializerExecutions.Add(new ModuleInitializerExecution(
                    loadedModule.Path,
                    initializerIndex,
                    initializerEntryPoint,
                    result));
                if (result != OrbisGen2Result.ORBIS_GEN2_OK)
                {
                    Log.Error(
                        $"Module start failed: {moduleName}[{initializerIndex}] -> {result}");
                    return result;
                }
            }
        }

        return null;
    }

    private OrbisGen2Result? RunImageInitializers(
        string label,
        SelfImage image,
        Generation generation,
        IReadOnlyDictionary<ulong, string> activeImportStubs,
        IReadOnlyDictionary<string, ulong> activeRuntimeSymbols,
        string processImageName)
    {
        if (image.PreInitializerFunctions.Count == 0 && image.InitializerFunctions.Count == 0)
        {
            return null;
        }

        Log.Info(
            $"Running initializers for {label}: preinit={image.PreInitializerFunctions.Count}, init={image.InitializerFunctions.Count}");

        var result = RunInitializerList(
            $"{label}:preinit",
            image.PreInitializerFunctions,
            generation,
            activeImportStubs,
            activeRuntimeSymbols,
            processImageName);
        if (result is not null)
        {
            return result;
        }

        return RunInitializerList(
            $"{label}:init",
            image.InitializerFunctions,
            generation,
            activeImportStubs,
            activeRuntimeSymbols,
            processImageName);
    }

    private OrbisGen2Result? RunInitializerList(
        string label,
        IReadOnlyList<ulong> initializerFunctions,
        Generation generation,
        IReadOnlyDictionary<ulong, string> activeImportStubs,
        IReadOnlyDictionary<string, ulong> activeRuntimeSymbols,
        string processImageName)
    {
        for (var i = 0; i < initializerFunctions.Count; i++)
        {
            var initializerAddress = initializerFunctions[i];
            if (initializerAddress < 0x10000)
            {
                continue;
            }

            Log.Debug(
                $"  Initializer {label}[{i}] -> 0x{initializerAddress:X16}");

            var result = _cpuDispatcher.DispatchEntry(
                initializerAddress,
                generation,
                activeImportStubs,
                activeRuntimeSymbols,
                processImageName,
                _cpuExecutionOptions);
            if (result != OrbisGen2Result.ORBIS_GEN2_OK)
            {
                return result;
            }
        }

        return null;
    }

    private List<PreparedModule> LoadAdjacentSceModules(
        string ebootPath,
        IDictionary<ulong, string> importStubs,
        IDictionary<string, ulong> runtimeSymbols,
        ICollection<SkippedModule>? skipped = null,
        ICollection<ModuleLoadFailure>? failures = null)
    {
        var loadedImages = new List<PreparedModule>();
        var ebootDirectory = Path.GetDirectoryName(ebootPath);
        if (string.IsNullOrWhiteSpace(ebootDirectory))
        {
            return loadedImages;
        }

        var pluginDirectory = Path.Combine(ebootDirectory, "Media", "Plugins");
        var moduleDirectories = new[]
        {
            Path.Combine(ebootDirectory, "sce_module"),
            Path.Combine(ebootDirectory, "sce_modules"),
            Path.Combine(ebootDirectory, "Media", "Modules"),
            pluginDirectory,
        }
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .Where(Directory.Exists)
        .ToArray();

        if (moduleDirectories.Length == 0)
        {
            return loadedImages;
        }

        var allModulePaths = moduleDirectories
            .SelectMany(directory => Directory
                .EnumerateFiles(directory)
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
            .Where(path =>
            {
                var extension = Path.GetExtension(path);
                return string.Equals(extension, ".prx", StringComparison.OrdinalIgnoreCase) ||
                       string.Equals(extension, ".sprx", StringComparison.OrdinalIgnoreCase);
            })
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var modulePaths = allModulePaths
            .Where(ShouldPreloadModule)
            .ToArray();
        var skippedModulePaths = allModulePaths
            .Where(path => !ShouldPreloadModule(path))
            .ToArray();
        foreach (var skippedPath in skippedModulePaths)
        {
            skipped?.Add(new SkippedModule(
                skippedPath,
                "Core module is provided by SharpEmu HLE."));
        }

        if (skippedModulePaths.Length > 0)
        {
            Log.Info(
                $"Skipping {skippedModulePaths.Length} core module(s): " +
                string.Join(", ", skippedModulePaths.Select(Path.GetFileName)));
        }

        if (modulePaths.Length == 0)
        {
            return loadedImages;
        }

        Log.Debug($"Module search directories: {string.Join(", ", moduleDirectories)}");
        Log.Info($"Loading {modulePaths.Length} module(s)...");
        var loadedModules = 0;
        var failedModules = 0;
        var mergedImportCount = 0;
        var mergedSymbolCount = 0;
        foreach (var modulePath in modulePaths)
        {
            try
            {
                var fileInfo = new FileInfo(modulePath);
                if (!fileInfo.Exists)
                {
                    failedModules++;
                    failures?.Add(new ModuleLoadFailure(
                        modulePath,
                        nameof(FileNotFoundException),
                        "Module file was not found."));
                    continue;
                }

                if (fileInfo.Length <= 0)
                {
                    failedModules++;
                    failures?.Add(new ModuleLoadFailure(
                        modulePath,
                        nameof(InvalidDataException),
                        "Module file is empty."));
                    continue;
                }

                if (fileInfo.Length > int.MaxValue)
                {
                    failedModules++;
                    failures?.Add(new ModuleLoadFailure(
                        modulePath,
                        nameof(NotSupportedException),
                        "Modules larger than 2 GB are not currently supported."));
                    continue;
                }

                var moduleBytes = GC.AllocateUninitializedArray<byte>((int)fileInfo.Length);
                using (var stream = File.OpenRead(modulePath))
                {
                    stream.ReadExactly(moduleBytes);
                }

                var moduleImage = _selfLoader.LoadAdditional(
                    moduleBytes.AsSpan(),
                    _virtualMemory,
                    _moduleManager,
                    _fileSystem,
                    Path.GetDirectoryName(modulePath));

                mergedImportCount += MergeImportStubs(importStubs, moduleImage.ImportStubs, modulePath);
                mergedSymbolCount += MergeRuntimeSymbols(runtimeSymbols, moduleImage.RuntimeSymbols);
                RegisterLoadedModule(modulePath, moduleImage, isMain: false, isSystemModule: false);
                var startOnBoot = !string.Equals(
                    Path.GetDirectoryName(modulePath),
                    pluginDirectory,
                    StringComparison.OrdinalIgnoreCase);
                loadedImages.Add(new PreparedModule(modulePath, moduleImage, startOnBoot));
                loadedModules++;

                Log.Info(
                    $"Loaded module {Path.GetFileName(modulePath)}: entry=0x{moduleImage.EntryPoint:X16}, imports={moduleImage.ImportStubs.Count}, symbols={moduleImage.RuntimeSymbols.Count}");
            }
            catch (Exception ex)
            {
                failedModules++;
                failures?.Add(new ModuleLoadFailure(modulePath, ex.GetType().Name, ex.Message));
                Log.Error($"Module load failed: {modulePath} ({ex.GetType().Name}: {ex.Message})", ex);
            }
        }

        Log.Info(
            $"Module preload summary: loaded={loadedModules}, failed={failedModules}, merged_imports={mergedImportCount}, merged_symbols={mergedSymbolCount}");
        return loadedImages;
    }

    private void RebindImportedDataSymbols(
        SelfImage mainImage,
        IReadOnlyList<PreparedModule> loadedModuleImages,
        IReadOnlyDictionary<string, ulong> runtimeSymbols)
    {
        var rebound = 0;
        var unresolved = 0;

        rebound += RebindImportedDataSymbols(mainImage, runtimeSymbols, ref unresolved);
        for (var i = 0; i < loadedModuleImages.Count; i++)
        {
            rebound += RebindImportedDataSymbols(loadedModuleImages[i].Image, runtimeSymbols, ref unresolved);
        }

        if (rebound != 0 || unresolved != 0)
        {
            Log.Info(
                $"Imported data rebind: rebound={rebound}, unresolved={unresolved}");
        }
    }

    private int RebindImportedDataSymbols(
        SelfImage image,
        IReadOnlyDictionary<string, ulong> runtimeSymbols,
        ref int unresolved)
    {
        if (image.ImportedRelocations.Count == 0)
        {
            return 0;
        }

        var rebound = 0;
        var logRebind = string.Equals(
            Environment.GetEnvironmentVariable("SHARPEMU_LOG_DATA_REBIND"),
            "1",
            StringComparison.Ordinal);
        for (var i = 0; i < image.ImportedRelocations.Count; i++)
        {
            var relocation = image.ImportedRelocations[i];
            if (!relocation.IsData)
            {
                continue;
            }

            if (!runtimeSymbols.TryGetValue(relocation.Nid, out var symbolAddress) ||
                !IsUsableRuntimeSymbolAddress(symbolAddress))
            {
                if (logRebind)
                {
                    Log.Warning(
                        $"Imported data unresolved: nid={relocation.Nid} target=0x{relocation.TargetAddress:X16} addend=0x{unchecked((ulong)relocation.Addend):X16}");
                }

                unresolved++;
                continue;
            }

            var reboundValue = AddSigned(symbolAddress, relocation.Addend);
            if (!TryWriteUInt64(_virtualMemory, relocation.TargetAddress, reboundValue))
            {
                if (logRebind)
                {
                    Log.Error(
                        $"Imported data write-failed: nid={relocation.Nid} target=0x{relocation.TargetAddress:X16} value=0x{reboundValue:X16}");
                }

                unresolved++;
                continue;
            }

            if (logRebind)
            {
                Log.Debug(
                    $"Imported data rebound: nid={relocation.Nid} target=0x{relocation.TargetAddress:X16} value=0x{reboundValue:X16}");
            }

            rebound++;
        }

        return rebound;
    }

    private static void MergeKnownHleDataSymbols(IDictionary<string, ulong> runtimeSymbols)
    {
        foreach (var nid in HleDataSymbols.EnumerateKnownNids())
        {
            if (runtimeSymbols.ContainsKey(nid) ||
                !HleDataSymbols.TryGetAddress(nid, out var symbolAddress) ||
                !IsUsableRuntimeSymbolAddress(symbolAddress))
            {
                continue;
            }

            runtimeSymbols[nid] = symbolAddress;
        }
    }

    private static int MergeImportStubs(
        IDictionary<ulong, string> destination,
        IReadOnlyDictionary<ulong, string> source,
        string modulePath)
    {
        var added = 0;
        foreach (var (address, nid) in source)
        {
            if (destination.TryGetValue(address, out var existingNid))
            {
                if (!string.Equals(existingNid, nid, StringComparison.Ordinal))
                {
                    Log.Warning(
                        $"Import stub conflict at 0x{address:X16}: keep={existingNid}, skip={nid} ({Path.GetFileName(modulePath)})");
                }

                continue;
            }

            destination[address] = nid;
            added++;
        }

        return added;
    }

    private static int MergeRuntimeSymbols(
        IDictionary<string, ulong> destination,
        IReadOnlyDictionary<string, ulong> source)
    {
        var added = 0;
        foreach (var (name, address) in source)
        {
            if (string.IsNullOrWhiteSpace(name) || !IsUsableRuntimeSymbolAddress(address))
            {
                continue;
            }

            if (destination.TryGetValue(name, out var existingAddress))
            {
                if (IsPreferredRuntimeSymbolAddress(existingAddress, address))
                {
                    destination[name] = address;
                    added++;
                }

                continue;
            }

            destination[name] = address;
            added++;
        }

        return added;
    }

    private static bool IsPreferredRuntimeSymbolAddress(ulong existingAddress, ulong candidateAddress)
    {
        return !IsUsableRuntimeSymbolAddress(existingAddress) && IsUsableRuntimeSymbolAddress(candidateAddress);
    }

    private static bool IsUsableRuntimeSymbolAddress(ulong address)
    {
        return address >= 0x10000 && !IsUnresolvedRuntimeSentinel(address);
    }

    private static bool TryWriteUInt64(IVirtualMemory virtualMemory, ulong address, ulong value)
    {
        Span<byte> bytes = stackalloc byte[sizeof(ulong)];
        BinaryPrimitives.WriteUInt64LittleEndian(bytes, value);
        return virtualMemory.TryWrite(address, bytes);
    }

    private static ulong AddSigned(ulong value, long addend)
    {
        if (addend >= 0)
        {
            return unchecked(value + (ulong)addend);
        }

        var magnitude = unchecked((ulong)(-(addend + 1))) + 1;
        return unchecked(value - magnitude);
    }

    private static bool IsUnresolvedRuntimeSentinel(ulong value)
    {
        return value == 0xFFFEUL ||
               value == 0xFFFFFFFEUL ||
               value == 0xFFFFFFFFFFFFFFFEUL;
    }

    private static bool ShouldPreloadModule(string modulePath)
    {
        if (string.Equals(Environment.GetEnvironmentVariable("SHARPEMU_PRELOAD_ALL_SCE_MODULES"), "1", StringComparison.Ordinal))
        {
            return true;
        }

        var fileName = Path.GetFileName(modulePath);
        if (string.IsNullOrWhiteSpace(fileName))
        {
            return false;
        }

        return !PreloadSkipModules.Contains(fileName);
    }

    private static void RegisterLoadedModule(string modulePath, SelfImage image, bool isMain, bool isSystemModule)
    {
        if (!TryComputeImageRange(image, out var baseAddress, out var size))
        {
            baseAddress = 0;
            size = 0;
        }

        var handle = KernelModuleRegistry.RegisterModule(
            modulePath,
            baseAddress,
            size,
            image.EntryPoint,
            image.InitFunctionEntryPoint,
            ehFrameHeaderAddress: 0,
            ehFrameAddress: 0,
            ehFrameSize: 0,
            isMain,
            isSystemModule);
        KernelModuleRegistry.RegisterModuleInitializers(
            handle,
            image.InitializerFunctions);
        KernelModuleRegistry.RegisterModuleSymbols(handle, image.RuntimeSymbols);
        Log.Info(
            $"Registered module handle={handle} name={Path.GetFileName(modulePath)} base=0x{baseAddress:X16} size=0x{size:X16}");
    }

    private static bool TryComputeImageRange(SelfImage image, out ulong baseAddress, out ulong size)
    {
        baseAddress = 0;
        size = 0;
        if (image.ProgramHeaders.Count == 0)
        {
            return false;
        }

        var imageBase = image.EntryPoint >= image.ElfHeader.EntryPoint
            ? image.EntryPoint - image.ElfHeader.EntryPoint
            : 0UL;
        var found = false;
        ulong minAddress = ulong.MaxValue;
        ulong maxAddress = 0;
        for (var i = 0; i < image.ProgramHeaders.Count; i++)
        {
            var header = image.ProgramHeaders[i];
            if (header.HeaderType != ProgramHeaderType.Load || header.MemorySize == 0)
            {
                continue;
            }

            var segmentStart = unchecked(imageBase + header.VirtualAddress);
            var segmentEnd = unchecked(segmentStart + header.MemorySize);
            if (!found || segmentStart < minAddress)
            {
                minAddress = segmentStart;
            }

            if (!found || segmentEnd > maxAddress)
            {
                maxAddress = segmentEnd;
            }

            found = true;
        }

        if (!found || maxAddress <= minAddress)
        {
            return false;
        }

        baseAddress = minAddress;
        size = maxAddress - minAddress;
        return true;
    }

    private static string BuildSessionSummary(CpuSessionSummary summary)
    {
        var resultText = summary.Result == OrbisGen2Result.ORBIS_GEN2_OK ? "OK" : summary.Result.ToString();
        var exitText = summary.ExitCode.HasValue ? summary.ExitCode.Value.ToString() : "?";
        return
            $"Summary: result={resultText} reason={summary.Reason} exit={exitText} last_guest_rip=0x{summary.LastGuestRip:X16} last_stub_rip=0x{summary.LastStubRip:X16} instr={summary.TotalInstructions} imports={summary.ImportsHit} unique_nids={summary.UniqueNidsHit}";
    }

    private string ReadOpcodePreview(ulong instructionPointer, int maxBytes)
    {
        if (maxBytes <= 0)
        {
            return "??";
        }

        Span<byte> oneByte = stackalloc byte[1];
        var parts = new string[maxBytes];
        var count = 0;
        for (var i = 0; i < maxBytes; i++)
        {
            if (!_virtualMemory.TryRead(instructionPointer + (ulong)i, oneByte))
            {
                break;
            }

            parts[count] = oneByte[0].ToString("X2");
            count++;
        }

        return count == 0 ? "??" : string.Join(' ', parts, 0, count);
    }

    private bool TryReadUInt64At(ulong address, out ulong value) =>
        TryReadUInt64At(_virtualMemory, address, out value);

    internal static IReadOnlyList<CpuStackFrame> CaptureTrapStackFrames(
        IVirtualMemory virtualMemory,
        CpuRegisterSnapshot? registers,
        Func<ulong, CpuCodeWindow?> captureCodeWindow,
        Func<ulong, IReadOnlyList<CpuDecodedInstruction>>? captureInstructions = null)
    {
        const int maxFrameCount = 16;
        const ulong maxStackDistance = 8UL * 1024 * 1024;

        if (registers is not { } snapshot || snapshot.Rbp == 0 || snapshot.Rbp < snapshot.Rsp)
        {
            return Array.Empty<CpuStackFrame>();
        }

        var executableRegions = virtualMemory.SnapshotRegions()
            .Where(static region =>
                (region.Protection & ProgramHeaderFlags.Execute) != 0)
            .ToArray();

        var stackLimit = snapshot.Rsp > ulong.MaxValue - maxStackDistance
            ? ulong.MaxValue
            : snapshot.Rsp + maxStackDistance;
        if (virtualMemory is IGuestStackMemory stackMemory &&
            stackMemory.TryGetStackRange(snapshot.Rsp, out var stackStart, out var stackEnd))
        {
            if (snapshot.Rbp < stackStart || snapshot.Rbp >= stackEnd)
            {
                return Array.Empty<CpuStackFrame>();
            }

            stackLimit = stackEnd;
        }

        var framePointer = snapshot.Rbp;
        var frames = new List<CpuStackFrame>(maxFrameCount);
        for (var i = 0; i < maxFrameCount; i++)
        {
            if ((framePointer & 7) != 0 ||
                stackLimit < 2 * sizeof(ulong) ||
                framePointer > stackLimit - (2 * sizeof(ulong)) ||
                !TryReadUInt64At(virtualMemory, framePointer, out var nextFramePointer) ||
                !TryReadUInt64At(virtualMemory, framePointer + sizeof(ulong), out var returnAddress) ||
                returnAddress == 0)
            {
                break;
            }

            var precedingCall = CaptureTrapStackCandidatePrecedingCall(
                virtualMemory,
                returnAddress);
            CpuCodePath? precedingCallTarget = null;
            if (precedingCall?.NearBranchTarget is { } targetAddress &&
                executableRegions.Any(region =>
                    targetAddress >= region.VirtualAddress &&
                    targetAddress - region.VirtualAddress < region.MemorySize) &&
                captureCodeWindow(targetAddress) is { } targetWindow)
            {
                precedingCallTarget = new CpuCodePath(
                    targetAddress,
                    targetWindow,
                    captureInstructions?.Invoke(targetAddress));
            }

            frames.Add(new CpuStackFrame(
                framePointer,
                nextFramePointer,
                returnAddress,
                captureCodeWindow(returnAddress),
                precedingCall,
                precedingCallTarget));
            if (nextFramePointer <= framePointer || nextFramePointer > stackLimit)
            {
                break;
            }

            framePointer = nextFramePointer;
        }

        return frames.ToArray();
    }

    internal static CpuMemoryWindow? CaptureTrapStackWindow(
        IVirtualMemory virtualMemory,
        CpuRegisterSnapshot? registers)
    {
        const int maximumBytesBeforeRsp = 128;
        const int maximumWindowBytes = 256;
        if (registers is not { Rsp: > 0 } snapshot)
        {
            return null;
        }

        Span<byte> oneByte = stackalloc byte[1];
        var startAddress = snapshot.Rsp;
        for (var index = 0; index < maximumBytesBeforeRsp && startAddress > 0; index++)
        {
            if (!virtualMemory.TryRead(startAddress - 1, oneByte))
            {
                break;
            }

            startAddress--;
        }

        var bytes = new byte[maximumWindowBytes];
        var count = 0;
        while (count < bytes.Length &&
               startAddress <= ulong.MaxValue - (ulong)count &&
               virtualMemory.TryRead(startAddress + (ulong)count, oneByte))
        {
            bytes[count++] = oneByte[0];
        }

        return count == 0
            ? null
            : new CpuMemoryWindow(
                startAddress,
                IcedDecoder.FormatBytes(bytes.AsSpan(0, count)),
                checked((int)(snapshot.Rsp - startAddress)));
    }

    internal static IReadOnlyList<CpuStackCodeCandidate> CaptureTrapStackCodeCandidates(
        IVirtualMemory virtualMemory,
        CpuRegisterSnapshot? registers,
        Func<ulong, CpuCodeWindow?> captureCodeWindow,
        Func<ulong, IReadOnlyList<CpuDecodedInstruction>>? captureInstructions = null)
    {
        const int maximumStackBytesEachSide = 128;
        if (registers is not { Rsp: > 0 } snapshot)
        {
            return Array.Empty<CpuStackCodeCandidate>();
        }

        var executableRegions = virtualMemory.SnapshotRegions()
            .Where(region =>
                region.MemorySize > 0 &&
                (region.Protection & ProgramHeaderFlags.Execute) != 0)
            .ToArray();
        if (executableRegions.Length == 0)
        {
            return Array.Empty<CpuStackCodeCandidate>();
        }

        var candidates = new List<CpuStackCodeCandidate>();
        for (var offset = -maximumStackBytesEachSide;
             offset <= maximumStackBytesEachSide - sizeof(ulong);
             offset += sizeof(ulong))
        {
            if (!TryAddSignedOffset(snapshot.Rsp, offset, out var slotAddress) ||
                !TryReadUInt64At(virtualMemory, slotAddress, out var address))
            {
                continue;
            }

            if (!executableRegions.Any(region =>
                    address >= region.VirtualAddress &&
                    address - region.VirtualAddress < region.MemorySize))
            {
                continue;
            }

            var precedingCall = CaptureTrapStackCandidatePrecedingCall(virtualMemory, address);
            CpuCodePath? precedingCallTarget = null;
            if (precedingCall?.NearBranchTarget is { } targetAddress &&
                executableRegions.Any(region =>
                    targetAddress >= region.VirtualAddress &&
                    targetAddress - region.VirtualAddress < region.MemorySize))
            {
                precedingCallTarget = new CpuCodePath(
                    targetAddress,
                    captureCodeWindow(targetAddress),
                    captureInstructions?.Invoke(targetAddress));
            }

            candidates.Add(new CpuStackCodeCandidate(
                offset,
                address,
                captureCodeWindow(address),
                captureInstructions?.Invoke(address),
                precedingCall,
                precedingCallTarget));
        }

        return candidates;
    }

    private static bool TryAddSignedOffset(ulong address, int offset, out ulong result)
    {
        if (offset >= 0)
        {
            var positiveOffset = (ulong)offset;
            if (address > ulong.MaxValue - positiveOffset)
            {
                result = 0;
                return false;
            }

            result = address + positiveOffset;
            return true;
        }

        var magnitude = (ulong)(-offset);
        if (address < magnitude)
        {
            result = 0;
            return false;
        }

        result = address - magnitude;
        return true;
    }

    private static bool TryReadUInt64At(
        IVirtualMemory virtualMemory,
        ulong address,
        out ulong value)
    {
        Span<byte> buffer = stackalloc byte[sizeof(ulong)];
        if (!virtualMemory.TryRead(address, buffer))
        {
            value = 0;
            return false;
        }

        value = BinaryPrimitives.ReadUInt64LittleEndian(buffer);
        return true;
    }

    internal static CpuCodeWindow? CaptureTrapStackCandidateCodeWindow(
        IVirtualMemory virtualMemory,
        ulong instructionPointer) =>
        CaptureCodeWindow(
            virtualMemory,
            instructionPointer,
            bytesBeforeInstruction: 16,
            maximumWindowBytes: 128);

    internal static IReadOnlyList<CpuDecodedInstruction> CaptureTrapStackCandidateInstructions(
        IVirtualMemory virtualMemory,
        ulong address)
    {
        const int maximumBytes = 112;
        const int maximumInstructions = 32;
        var instructions = new List<CpuDecodedInstruction>();
        var consumedBytes = 0;
        while (consumedBytes < maximumBytes && instructions.Count < maximumInstructions)
        {
            var instructionAddress = address + (ulong)consumedBytes;
            var maximumRead = Math.Min(15, maximumBytes - consumedBytes);
            if (!IcedDecoder.TryReadGuestBytes(
                    virtualMemory,
                    instructionAddress,
                    maximumRead,
                    out var bytes) ||
                !IcedDecoder.TryDecode(instructionAddress, bytes, out var instruction) ||
                instruction.Length > bytes.Length)
            {
                break;
            }

            instructions.Add(ToCpuDecodedInstruction(in instruction));
            consumedBytes += instruction.Length;
            if (instruction.FlowControl is
                FlowControl.Return or
                FlowControl.UnconditionalBranch or
                FlowControl.IndirectBranch or
                FlowControl.Interrupt or
                FlowControl.Exception)
            {
                break;
            }
        }

        return instructions;
    }

    internal static CpuDecodedInstruction? CaptureTrapStackCandidatePrecedingCall(
        IVirtualMemory virtualMemory,
        ulong address)
    {
        const int maximumInstructionBytes = 15;
        for (var length = maximumInstructionBytes; length >= 2; length--)
        {
            if (address < (ulong)length)
            {
                continue;
            }

            var instructionAddress = address - (ulong)length;
            if (!IcedDecoder.TryReadGuestBytes(virtualMemory, instructionAddress, length, out var bytes) ||
                bytes.Length != length ||
                !IcedDecoder.TryDecode(instructionAddress, bytes, out var instruction) ||
                instruction.Length != length ||
                instruction.FlowControl is not (FlowControl.Call or FlowControl.IndirectCall))
            {
                continue;
            }

            return ToCpuDecodedInstruction(in instruction);
        }

        return null;
    }

    private static CpuDecodedInstruction ToCpuDecodedInstruction(in DecodedInst instruction) =>
        new(
            instruction.Rip,
            instruction.Length,
            IcedDecoder.FormatBytes(instruction.Bytes),
            instruction.Mnemonic,
            instruction.Text,
            instruction.FlowControl.ToString(),
            instruction.NearBranchTarget,
            instruction.MemoryAddress);

    private CpuCodeWindow? CaptureTrapCodeWindow(ulong instructionPointer) =>
        CaptureCodeWindow(
            _virtualMemory,
            instructionPointer,
            bytesBeforeInstruction: 32,
            maximumWindowBytes: 64);

    private static CpuCodeWindow? CaptureCodeWindow(
        IVirtualMemory virtualMemory,
        ulong instructionPointer,
        int bytesBeforeInstruction,
        int maximumWindowBytes)
    {
        Span<byte> oneByte = stackalloc byte[1];
        var startAddress = instructionPointer;
        for (var i = 0; i < bytesBeforeInstruction && startAddress > 0; i++)
        {
            if (!virtualMemory.TryRead(startAddress - 1, oneByte))
            {
                break;
            }
            startAddress--;
        }

        var bytes = new byte[maximumWindowBytes];
        var count = 0;
        while (count < bytes.Length && startAddress <= ulong.MaxValue - (ulong)count)
        {
            if (!virtualMemory.TryRead(startAddress + (ulong)count, oneByte))
            {
                break;
            }
            bytes[count++] = oneByte[0];
        }

        return count == 0
            ? null
            : new CpuCodeWindow(
                startAddress,
                checked((int)(instructionPointer - startAddress)),
                IcedDecoder.FormatBytes(bytes.AsSpan(0, count)));
    }

    private bool TryDecodeInstructionAt(ulong instructionPointer, out DecodedInst decodedInstruction)
    {
        if (!IcedDecoder.TryReadGuestBytes(_virtualMemory, instructionPointer, maxLen: 15, out var instructionBytes))
        {
            decodedInstruction = default;
            return false;
        }

        return IcedDecoder.TryDecode(instructionPointer, instructionBytes, out decodedInstruction);
    }

    private static bool IsInvalidLongModeOpcode(byte opcode)
    {
        return opcode is
            0x06 or // PUSH ES
            0x07 or // POP ES
            0x0E or // PUSH CS
            0x16 or // PUSH SS
            0x17 or // POP SS
            0x1E or // PUSH DS
            0x1F or // POP DS
            0x27 or // DAA
            0x2F or // DAS
            0x37 or // AAA
            0x3F or // AAS
            0x60 or // PUSHA/PUSHAD
            0x61 or // POPA/POPAD
            0xD4 or // AAM
            0xD5;   // AAD
    }

    private static string BuildDecodedInstructionFields(in DecodedInst instruction, string fieldPrefix = "inst")
    {
        var text = $", {fieldPrefix}={instruction.Text}, {fieldPrefix}_len={instruction.Length}, {fieldPrefix}_mnemonic={instruction.Mnemonic}, {fieldPrefix}_flow={instruction.FlowControl}";
        if (instruction.NearBranchTarget is { } target)
        {
            text += $", {fieldPrefix}_target=0x{target:X16}";
        }

        if (instruction.MemoryAddress is { } memoryAddress)
        {
            text += $", {fieldPrefix}_mem=0x{memoryAddress:X16}";
        }

        if (instruction.Bytes.Length > 0)
        {
            text += $", {fieldPrefix}_bytes={IcedDecoder.FormatBytes(instruction.Bytes)}";
        }

        return text;
    }

    public OrbisGen2Result DispatchHleCall(string nid, CpuContext context)
    {
        return _moduleManager.Dispatch(nid, context);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        if (_cpuDispatcher is IDisposable disposableDispatcher)
        {
            disposableDispatcher.Dispose();
        }

        if (_virtualMemory is IDisposable disposableMemory)
        {
            disposableMemory.Dispose();
        }
    }

}
