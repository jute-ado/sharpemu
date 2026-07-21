// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Core;
using SharpEmu.Core.Cpu;
using SharpEmu.Core.Cpu.Native;
using SharpEmu.Core.Loader;
using SharpEmu.Core.Runtime;
using SharpEmu.GUI;
using SharpEmu.HLE;
using SharpEmu.Libs.VideoOut;
using SharpEmu.Logging;
using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace SharpEmu.CLI;

internal static partial class Program
{
    private static readonly SharpEmuLogger Log = SharpEmuLog.For("SharpEmu.CLI");
    private static readonly object ConsoleMirrorSync = new();
    private static StreamWriter? _consoleMirrorFile;
    private const int DefaultImportTraceLimit = 32;
    private const string MitigatedChildFlag = "--sharpemu-mitigated-child";
    private const uint EXTENDED_STARTUPINFO_PRESENT = 0x00080000;
    private const uint INFINITE = 0xFFFFFFFF;
    private const uint WAIT_TIMEOUT = 0x00000102;
    private const int ExecutionTimeoutExitCode = 7;
    private const int BundleFingerprintMismatchExitCode = 8;
    private const int MaxExecutionTimeoutSeconds = 4_294_967;
    private const int PROC_THREAD_ATTRIBUTE_MITIGATION_POLICY = 0x00020007;
    private const uint JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE = 0x00002000;
    private const int JobObjectExtendedLimitInformation = 9;
    private const int STARTF_USESTDHANDLES = 0x00000100;
    private const uint HANDLE_FLAG_INHERIT = 0x00000001;
    private const string MitigatedChildEnvironment = "SHARPEMU_MITIGATED_CHILD";
    private const string SupervisorReadyFileEnvironment = "SHARPEMU_SUPERVISOR_READY_FILE";
    private const string SupervisorReadyFilePrefix = "sharpemu-execution-ready-";
    private const string SupervisorReadyFileSuffix = ".signal";
    private const ulong PROCESS_CREATION_MITIGATION_POLICY_CONTROL_FLOW_GUARD_ALWAYS_OFF = 0x00000002UL << 40;
    private const ulong PROCESS_CREATION_MITIGATION_POLICY2_CET_USER_SHADOW_STACKS_ALWAYS_OFF = 0x00000002UL << 28;
    private const ulong PROCESS_CREATION_MITIGATION_POLICY2_USER_CET_SET_CONTEXT_IP_VALIDATION_ALWAYS_OFF = 0x00000002UL << 32;
    private const ulong PROCESS_CREATION_MITIGATION_POLICY2_XTENDED_CONTROL_FLOW_GUARD_ALWAYS_OFF = 0x00000002UL << 40;
    private const int ATTACH_PARENT_PROCESS = -1;
    private const int STD_INPUT_HANDLE = -10;
    private const int STD_OUTPUT_HANDLE = -11;
    private const int STD_ERROR_HANDLE = -12;
    private const uint GENERIC_READ = 0x80000000;
    private const uint GENERIC_WRITE = 0x40000000;
    private const uint FILE_SHARE_READ = 0x00000001;
    private const uint FILE_SHARE_WRITE = 0x00000002;
    private const uint OPEN_EXISTING = 3;

    [STAThread]
    private static int Main(string[] args)
    {
        try
        {
            return Run(args);
        }
        finally
        {
            DropConsoleFileMirror();
            SharpEmuLog.Shutdown();
        }
    }

    private static int Run(string[] args)
    {
        if (Updater.TryApply(args, out var updateExitCode))
        {
            return updateExitCode;
        }

        args = NormalizeInternalArguments(args, out var isMitigatedChild);
        if (args.Length == 0 && !isMitigatedChild)
        {
            // No arguments: open the desktop frontend. Any argument selects
            // the classic CLI behavior below.
            return GuiLauncher.Run();
        }

        var invocationStartedTimestamp = Stopwatch.GetTimestamp();

        // The executable uses the GUI subsystem, so CLI mode has to connect
        // itself to a console before the first write.
        EnsureCliConsole();
        UseUtf8ConsoleOutput();
        if (isMitigatedChild && TryGetLogFileArgument(args, out var earlyLogFilePath))
        {
            TryEnableConsoleFileMirror(earlyLogFilePath);
        }

        if (OperatingSystem.IsMacOS() || OperatingSystem.IsLinux())
        {
            if (OperatingSystem.IsMacOS())
            {
                PreloadMacVulkanLoader();
            }

            // GLFW requires window creation and event processing on the
            // process main thread: AppKit demands it on macOS, and X11 has a
            // single event queue that must be serviced from the main thread
            // (a window created and polled off it may never map, which showed
            // as a running game with no visible window on Linux). Emulation
            // moves to a worker thread and the main thread services the window
            // work the video presenter posts. Windows keeps a per-thread event
            // queue, so its window stays on the presenter's own thread.
            var exitCode = 0;
            HostMainThread.Enable();
            var emulation = new Thread(() =>
            {
                try
                {
                    exitCode = RunEmulator(args, isMitigatedChild, invocationStartedTimestamp);
                }
                finally
                {
                    HostMainThread.Shutdown();
                }
            }, 32 * 1024 * 1024)
            {
                Name = "SharpEmu Emulation",
            };
            emulation.Start();
            HostMainThread.Pump();
            emulation.Join();
            return exitCode;
        }

        return RunEmulator(args, isMitigatedChild, invocationStartedTimestamp);
    }

    /// <summary>
    /// The supported host execution model, checked before any emulation
    /// starts: the CPU backend executes guest x86-64 code natively, so the
    /// host process must be x86-64 — win-x64/linux-x64 on x64 hardware, or
    /// osx-x64 under Rosetta 2 on Apple Silicon (Rosetta translates the
    /// whole process, so it still reports as X64 here). An arm64 process
    /// (e.g. the osx-arm64 build) can browse the GUI but cannot run games;
    /// failing up front distinguishes that from MoltenVK, signal-handler,
    /// or guest-memory startup problems.
    /// </summary>
    private static bool CheckHostArchitecture()
    {
        if (RuntimeInformation.ProcessArchitecture == Architecture.X64)
        {
            return true;
        }

        Console.Error.WriteLine(
            $"[LOADER][ERROR] Unsupported process architecture " +
            $"{RuntimeInformation.ProcessArchitecture}: guest code executes " +
            "natively, so SharpEmu must run as an x86-64 process.");
        if (OperatingSystem.IsMacOS())
        {
            Console.Error.WriteLine(
                "[LOADER][ERROR] On Apple Silicon, use the osx-x64 build under " +
                "Rosetta 2 (install with: softwareupdate --install-rosetta).");
        }

        return false;
    }

    /// <summary>
    /// Makes a Vulkan loader visible to GLFW's dlopen("libvulkan.1.dylib").
    /// Homebrew's Vulkan libraries are arm64-only and cannot load into this
    /// x86-64 (Rosetta 2) process, so a universal libMoltenVK.dylib placed
    /// next to the executable (named libvulkan.1.dylib) is preloaded here;
    /// dyld then resolves GLFW's bare-name dlopen to the loaded image.
    /// </summary>
    private static void PreloadMacVulkanLoader()
    {
        var candidates = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "libvulkan.1.dylib"),
            Path.Combine(AppContext.BaseDirectory, "libMoltenVK.dylib"),
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".sharpemu", "x64lib", "libvulkan.1.dylib"),
        };
        foreach (var candidate in candidates)
        {
            if (File.Exists(candidate) && NativeLibrary.TryLoad(candidate, out _))
            {
                Console.Error.WriteLine($"[LOADER][INFO] Vulkan loader preloaded: {candidate}");
                return;
            }
        }

        if (NativeLibrary.TryLoad("libvulkan.1.dylib", out _))
        {
            return;
        }

        Console.Error.WriteLine(
            "[LOADER][WARN] No x86-64 Vulkan loader found; video output will be unavailable. " +
            "Place a universal libMoltenVK.dylib (from the MoltenVK releases) next to SharpEmu " +
            "as libvulkan.1.dylib.");
    }

    private static int RunEmulator(
        string[] args,
        bool isMitigatedChild,
        long invocationStartedTimestamp)
    {
        Console.Error.WriteLine($"[DEBUG] SharpEmu starting with {args.Length} args");

        if (!TryExtractHostSurfaceDescriptor(
                args,
                out var emulatorArgs,
                out var hostSurfaceDescriptor,
                out var hostSurfaceArgumentError))
        {
            Console.Error.WriteLine(
                $"[LOADER][ERROR] {hostSurfaceArgumentError}");
            return 1;
        }

        if (!TryParseArguments(
                emulatorArgs,
                out var ebootPath,
                out var runtimeOptions,
                out var logLevel,
                out var logFilePath,
                out var reportJsonPath,
                out var executionTimeoutSeconds,
                out var loadOnly,
                out var expectedBundleSha256))
        {
            PrintUsage();
            return 1;
        }

        if (!loadOnly &&
            !isMitigatedChild &&
            TryRunSupervisedChild(
                args,
                ebootPath,
                reportJsonPath,
                executionTimeoutSeconds,
                invocationStartedTimestamp,
                out var childExitCode))
        {
            return childExitCode;
        }

        if (!isMitigatedChild && !string.IsNullOrWhiteSpace(logFilePath))
        {
            TryEnableConsoleFileMirror(logFilePath);
        }

        SharpEmuLog.MinimumLevel = logLevel;

        Log.Info(BuildInfo.Banner);
        Log.Info(HostSystemInfo.Summary);

        ebootPath = Path.GetFullPath(ebootPath);
        Console.Error.WriteLine($"[DEBUG] Full path: {ebootPath}");

        if (!File.Exists(ebootPath))
        {
            Log.Error($"EBOOT file was not found: {ebootPath}");
            if (!TryWriteExecutionReport(
                    reportJsonPath,
                    ebootPath,
                    result: null,
                    runtime: null,
                    hostError: "EBOOT file was not found.",
                    invocationStartedTimestamp: invocationStartedTimestamp,
                    mode: loadOnly ? "load-only" : "execution"))
            {
                return 3;
            }

            return 2;
        }

        Console.Error.WriteLine("[DEBUG] Creating runtime...");

        if (!loadOnly && !CheckHostArchitecture())
        {
            return 5;
        }

        if (!HostSurfaceSession.TryCreate(
                hostSurfaceDescriptor,
                out var hostSurfaceSession,
                out var hostSurfaceError))
        {
            Console.Error.WriteLine($"[LOADER][ERROR] {hostSurfaceError}");
            return 3;
        }

        using var attachedHostSurface = hostSurfaceSession;
        using var runtime = loadOnly
            ? SharpEmuRuntime.CreateForInspection(runtimeOptions)
            : SharpEmuRuntime.CreateDefault(runtimeOptions);

        if (loadOnly)
        {
            PreparedApplication preparedApplication;
            try
            {
                Console.Error.WriteLine($"[DEBUG] Loading without execution: {ebootPath}");
                preparedApplication = runtime.PrepareApplication(ebootPath);
                var image = preparedApplication.MainImage;
                Log.Info(
                    $"SharpEmu image inspection completed. Format={(image.IsSelf ? "SELF" : "ELF")}, " +
                    $"entry=0x{image.EntryPoint:X16}, mappedRegions={image.MappedRegions.Count}, " +
                    $"imports={image.ImportStubs.Count}, relocations={image.ImportedRelocations.Count}, " +
                    $"modules={preparedApplication.Modules.Count}, " +
                    $"skippedModules={preparedApplication.SkippedModules.Count}, " +
                    $"moduleFailures={preparedApplication.ModuleLoadFailures.Count}");
                if (preparedApplication.ModuleLoadFailures.Count > 0)
                {
                    Log.Warn(
                        $"Image inspection encountered {preparedApplication.ModuleLoadFailures.Count} module load failure(s). " +
                        "See the JSON report for structured details.");
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[DEBUG] Exception: {ex}");
                Log.Error("SharpEmu failed to load the image.", ex);
                TryWriteExecutionReport(
                    reportJsonPath,
                    ebootPath,
                    result: null,
                    runtime,
                    hostError: ex.ToString(),
                    invocationStartedTimestamp: invocationStartedTimestamp,
                    mode: "load-only");
                return 3;
            }

            var reportFingerprint = BuildReportFingerprint(ebootPath, preparedApplication);
            var fingerprintMatches = expectedBundleSha256 is null || string.Equals(
                expectedBundleSha256,
                reportFingerprint.Bundle.Sha256,
                StringComparison.OrdinalIgnoreCase);
            var fingerprintError = fingerprintMatches
                ? null
                : $"Bundle fingerprint mismatch: expected {expectedBundleSha256}, " +
                  $"actual {reportFingerprint.Bundle.Sha256 ?? "unavailable"}.";
            if (fingerprintError is not null)
            {
                Log.Error(fingerprintError);
            }

            if (!TryWriteExecutionReport(
                    reportJsonPath,
                    ebootPath,
                    result: null,
                    runtime,
                    hostError: fingerprintError,
                    invocationStartedTimestamp: invocationStartedTimestamp,
                    mode: "load-only",
                    resultOverride: fingerprintMatches
                        ? new CliExecutionResult("IMAGE_LOADED", 0, true)
                        : new CliExecutionResult("BUNDLE_FINGERPRINT_MISMATCH", null, false),
                    fingerprintOverride: reportFingerprint))
            {
                return 3;
            }

            return fingerprintMatches ? 0 : BundleFingerprintMismatchExitCode;
        }

        OrbisGen2Result result;
        ConsoleCancelEventHandler? cancelHandler = null;
        try
        {
            cancelHandler = (_, eventArgs) =>
            {
                eventArgs.Cancel = true;
                VideoOutExports.NotifyHostInterrupt();
            };
            Console.CancelKeyPress += cancelHandler;

            Console.Error.WriteLine($"[DEBUG] Running: {ebootPath}");
            result = runtime.Run(
                ebootPath,
                onExecutionStarting: null,
                preparedApplication =>
                {
                    if (expectedBundleSha256 is not null)
                    {
                        var fingerprint = BuildReportFingerprint(
                            ebootPath,
                            preparedApplication);
                        if (!string.Equals(
                            expectedBundleSha256,
                            fingerprint.Bundle.Sha256,
                            StringComparison.OrdinalIgnoreCase))
                        {
                            throw new BundleFingerprintMismatchException(
                                expectedBundleSha256,
                                fingerprint);
                        }
                    }

                    _ = TryWriteExecutionReport(
                        reportJsonPath,
                        ebootPath,
                        result: null,
                        runtime,
                        hostError: null,
                        invocationStartedTimestamp: invocationStartedTimestamp,
                        incompleteResultName: "EXECUTION_RUNNING");
                    SignalSupervisedExecutionReady();
                });
            Console.Error.WriteLine($"[DEBUG] Result: {result}");
        }
        catch (BundleFingerprintMismatchException ex)
        {
            Log.Error(ex.Message);
            if (!TryWriteExecutionReport(
                    reportJsonPath,
                    ebootPath,
                    result: null,
                    runtime,
                    hostError: ex.Message,
                    invocationStartedTimestamp: invocationStartedTimestamp,
                    resultOverride: new CliExecutionResult(
                        "BUNDLE_FINGERPRINT_MISMATCH",
                        null,
                        false),
                    fingerprintOverride: ex.Fingerprint))
            {
                return 3;
            }

            return BundleFingerprintMismatchExitCode;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[DEBUG] Exception: {ex}");
            Log.Error("SharpEmu failed to run.", ex);
            TryWriteExecutionReport(
                reportJsonPath,
                ebootPath,
                result: null,
                runtime,
                hostError: ex.ToString(),
                invocationStartedTimestamp: invocationStartedTimestamp);
            return 3;
        }
        finally
        {
            if (cancelHandler is not null)
            {
                Console.CancelKeyPress -= cancelHandler;
            }
        }

        Log.Info($"SharpEmu execution completed. Result={result} (0x{(int)result:X8})");
        if (!string.IsNullOrWhiteSpace(runtime.LastSessionSummary))
        {
            Log.Info(runtime.LastSessionSummary);
        }

        if (!string.IsNullOrWhiteSpace(runtime.LastBasicBlockTrace))
        {
            Log.Info("BB trace:");
            Log.Info(runtime.LastBasicBlockTrace);
        }

        if (!string.IsNullOrWhiteSpace(runtime.LastMilestoneLog))
        {
            Log.Info(runtime.LastMilestoneLog);
        }

        if (result != OrbisGen2Result.ORBIS_GEN2_OK && !string.IsNullOrWhiteSpace(runtime.LastExecutionDiagnostics))
        {
            Log.Warn(runtime.LastExecutionDiagnostics);
        }

        if (runtimeOptions.ImportTraceLimit > 0 && !string.IsNullOrWhiteSpace(runtime.LastExecutionTrace))
        {
            Log.Info("Import trace:");
            Log.Info(runtime.LastExecutionTrace);
        }

        if (!TryWriteExecutionReport(
                reportJsonPath,
                ebootPath,
                result,
                runtime,
                hostError: null,
                invocationStartedTimestamp: invocationStartedTimestamp))
        {
            return 3;
        }

        return result == OrbisGen2Result.ORBIS_GEN2_OK ? 0 : 4;
    }

    private static bool TryExtractHostSurfaceDescriptor(
        IReadOnlyList<string> args,
        out string[] emulatorArgs,
        out string? descriptor,
        out string? error)
    {
        const string hostSurfacePrefix = "--host-surface=";
        var remaining = new List<string>(args.Count);
        descriptor = null;
        error = null;
        foreach (var argument in args)
        {
            if (!argument.StartsWith(
                    hostSurfacePrefix,
                    StringComparison.OrdinalIgnoreCase))
            {
                remaining.Add(argument);
                continue;
            }

            if (descriptor is not null)
            {
                emulatorArgs = [];
                error = "more than one GUI host surface was specified";
                return false;
            }

            descriptor = argument[hostSurfacePrefix.Length..];
            if (string.IsNullOrWhiteSpace(descriptor))
            {
                emulatorArgs = [];
                error = "the GUI host surface descriptor is empty";
                return false;
            }
        }

        emulatorArgs = remaining.ToArray();
        return true;
    }

    private sealed class HostSurfaceSession : IDisposable
    {
        private VulkanHostSurface? _surface;

        private HostSurfaceSession(VulkanHostSurface surface)
        {
            _surface = surface;
        }

        public static bool TryCreate(
            string? descriptor,
            out HostSurfaceSession? session,
            out string? error)
        {
            session = null;
            error = null;
            if (descriptor is null)
            {
                return true;
            }

            if (!VulkanHostSurface.TryCreateChildProcessSurface(
                    descriptor,
                    out var surface,
                    out error) ||
                surface is null)
            {
                return false;
            }

            if (!VulkanVideoHost.TryAttachSurface(surface))
            {
                surface.Dispose();
                error = "the requested GUI host surface is already active";
                return false;
            }

            HostSessionControl.SetEmbeddedHostSurface(
                surface.WindowHandle,
                surface.DisplayHandle);
            session = new HostSurfaceSession(surface);
            return true;
        }

        public void Dispose()
        {
            var surface = Interlocked.Exchange(ref _surface, null);
            if (surface is null)
            {
                return;
            }

            HostSessionControl.SetEmbeddedHostSurface(0);
            VulkanVideoHost.RequestClose();
            VulkanVideoHost.DetachSurface(surface);
            surface.Dispose();
        }
    }

    private static void EnsureCliConsole()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        // Standard handles already provided (pipes or file redirection, e.g.
        // when the GUI or a script launches us): use them as-is.
        if (IsHandleValid(GetStdHandle(STD_OUTPUT_HANDLE)) && IsHandleValid(GetStdHandle(STD_ERROR_HANDLE)))
        {
            return;
        }

        // Prefer the console of the parent process (interactive terminal);
        // create one only when started with arguments but no terminal at all
        // (e.g. a shortcut), so usage and errors remain visible.
        if (!AttachConsole(ATTACH_PARENT_PROCESS) && GetConsoleWindow() == 0)
        {
            _ = AllocConsole();
        }

        RebindStdHandleToConsole(STD_OUTPUT_HANDLE);
        RebindStdHandleToConsole(STD_ERROR_HANDLE);
    }

    /// <summary>
    /// Makes console writes UTF-8 so the GUI's pipe reader (and any modern
    /// terminal) decodes non-ASCII text correctly. Without this, redirected
    /// output falls back to the OS ANSI code page and characters like "—"
    /// arrive mangled.
    /// </summary>
    private static void UseUtf8ConsoleOutput()
    {
        try
        {
            // Also recreates the redirected Console.Out/Error writers with
            // the new encoding.
            Console.OutputEncoding = Encoding.UTF8;
            return;
        }
        catch (Exception)
        {
            // No attached console (GUI-subsystem child with piped output):
            // wrap the raw handles instead.
        }

        if (Console.IsOutputRedirected)
        {
            Console.SetOut(new StreamWriter(
                Console.OpenStandardOutput(),
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false))
            {
                AutoFlush = true,
            });
        }

        if (Console.IsErrorRedirected)
        {
            Console.SetError(new StreamWriter(
                Console.OpenStandardError(),
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false))
            {
                AutoFlush = true,
            });
        }
    }

    private static void RebindStdHandleToConsole(int stdHandle)
    {
        if (IsHandleValid(GetStdHandle(stdHandle)) || GetConsoleWindow() == 0)
        {
            return;
        }

        var conOut = CreateFileW(
            "CONOUT$",
            GENERIC_READ | GENERIC_WRITE,
            FILE_SHARE_READ | FILE_SHARE_WRITE,
            0,
            OPEN_EXISTING,
            0,
            0);
        if (IsHandleValid(conOut))
        {
            _ = SetStdHandle(stdHandle, conOut);
        }
    }

    private static bool IsHandleValid(nint handle)
    {
        return handle != 0 && handle != -1;
    }

    private static string[] NormalizeInternalArguments(string[] args, out bool isMitigatedChild)
    {
        isMitigatedChild = false;
        var trustedMitigatedChild = string.Equals(
            Environment.GetEnvironmentVariable(MitigatedChildEnvironment),
            "1",
            StringComparison.Ordinal);
        if (args.Length == 0)
        {
            return args;
        }

        var list = new List<string>(args.Length);
        foreach (var arg in args)
        {
            if (string.Equals(arg, MitigatedChildFlag, StringComparison.Ordinal))
            {
                isMitigatedChild = trustedMitigatedChild;
                continue;
            }

            list.Add(arg);
        }

        return list.ToArray();
    }

    private static bool TryRunSupervisedChild(
        string[] args,
        string ebootPath,
        string? reportJsonPath,
        int? executionTimeoutSeconds,
        long invocationStartedTimestamp,
        out int childExitCode)
    {
        childExitCode = 0;
        if (OperatingSystem.IsWindows() &&
            string.Equals(Environment.GetEnvironmentVariable("SHARPEMU_DISABLE_MITIGATION_RELAUNCH"), "1", StringComparison.Ordinal))
        {
            return false;
        }

        var processPath = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(processPath))
        {
            return false;
        }

        var hostedByDotnet = string.Equals(
            Path.GetFileNameWithoutExtension(processPath),
            "dotnet",
            StringComparison.OrdinalIgnoreCase);
        // Assembly.Location is unavailable in a single-file app, but this branch is
        // reached only when dotnet.exe is hosting a loose managed assembly.
#pragma warning disable IL3000
        var entryAssemblyPath = hostedByDotnet ? Assembly.GetEntryAssembly()?.Location : null;
#pragma warning restore IL3000
        if (hostedByDotnet && string.IsNullOrWhiteSpace(entryAssemblyPath))
        {
            return false;
        }

        var hostArgumentCount = hostedByDotnet ? 1 : 0;
        var childArgs = new string[args.Length + hostArgumentCount + 1];
        if (entryAssemblyPath is not null)
        {
            childArgs[0] = entryAssemblyPath;
        }
        childArgs[hostArgumentCount] = MitigatedChildFlag;
        for (var i = 0; i < args.Length; i++)
        {
            childArgs[i + hostArgumentCount + 1] = args[i];
        }

        var executionReadyPath = executionTimeoutSeconds is null
            ? null
            : Path.Combine(
                Path.GetTempPath(),
                $"{SupervisorReadyFilePrefix}{Guid.NewGuid():N}{SupervisorReadyFileSuffix}");

        if (!OperatingSystem.IsWindows())
        {
            return RunPortableSupervisedChild(
                processPath,
                childArgs,
                ebootPath,
                reportJsonPath,
                executionTimeoutSeconds,
                executionReadyPath,
                invocationStartedTimestamp,
                out childExitCode);
        }

        var commandLine = BuildCommandLine(processPath, childArgs);
        var startupInfoEx = new STARTUPINFOEX();
        startupInfoEx.StartupInfo.cb = Marshal.SizeOf<STARTUPINFOEX>();
        ConfigureInheritedStdHandles(ref startupInfoEx.StartupInfo);

        nint attributeList = 0;
        nint mitigationPolicies = 0;
        var previousChildEnvironment = Environment.GetEnvironmentVariable(MitigatedChildEnvironment);
        var previousReadyFileEnvironment = Environment.GetEnvironmentVariable(SupervisorReadyFileEnvironment);
        try
        {
            nuint attributeListSize = 0;
            _ = InitializeProcThreadAttributeList(0, 1, 0, ref attributeListSize);
            attributeList = Marshal.AllocHGlobal((nint)attributeListSize);
            if (!InitializeProcThreadAttributeList(attributeList, 1, 0, ref attributeListSize))
            {
                childExitCode = 5;
                Console.Error.WriteLine($"[ERROR] Failed to initialize mitigation attributes: {Marshal.GetLastWin32Error()}");
                return true;
            }

            startupInfoEx.lpAttributeList = attributeList;

            var policy1 = PROCESS_CREATION_MITIGATION_POLICY_CONTROL_FLOW_GUARD_ALWAYS_OFF;
            var policy2 =
                PROCESS_CREATION_MITIGATION_POLICY2_CET_USER_SHADOW_STACKS_ALWAYS_OFF |
                PROCESS_CREATION_MITIGATION_POLICY2_USER_CET_SET_CONTEXT_IP_VALIDATION_ALWAYS_OFF;

            mitigationPolicies = Marshal.AllocHGlobal(sizeof(ulong) * 2);
            Marshal.WriteInt64(mitigationPolicies, unchecked((long)policy1));
            Marshal.WriteInt64(nint.Add(mitigationPolicies, sizeof(long)), unchecked((long)policy2));

            if (!UpdateProcThreadAttribute(
                attributeList,
                0,
                (nint)PROC_THREAD_ATTRIBUTE_MITIGATION_POLICY,
                mitigationPolicies,
                (nuint)(sizeof(ulong) * 2),
                0,
                0))
            {
                childExitCode = 5;
                Console.Error.WriteLine($"[ERROR] Failed to apply mitigation attributes: {Marshal.GetLastWin32Error()}");
                return true;
            }

            var cmdLineBuilder = new StringBuilder(commandLine);
            nint jobHandle = 0;
            Environment.SetEnvironmentVariable(MitigatedChildEnvironment, "1");
            Environment.SetEnvironmentVariable(SupervisorReadyFileEnvironment, executionReadyPath);
            var created = CreateProcessW(
                null,
                cmdLineBuilder,
                0,
                0,
                true,
                EXTENDED_STARTUPINFO_PRESENT,
                0,
                Environment.CurrentDirectory,
                ref startupInfoEx,
                out var processInfo);
            Environment.SetEnvironmentVariable(MitigatedChildEnvironment, previousChildEnvironment);
            Environment.SetEnvironmentVariable(SupervisorReadyFileEnvironment, previousReadyFileEnvironment);
            if (!created)
            {
                childExitCode = 5;
                Console.Error.WriteLine($"[ERROR] Failed to launch mitigated child process: {Marshal.GetLastWin32Error()}");
                return true;
            }

            try
            {
                jobHandle = CreateJobObjectW(0, null);
                if (jobHandle != 0 &&
                    TryEnableKillOnJobClose(jobHandle) &&
                    !AssignProcessToJobObject(jobHandle, processInfo.hProcess))
                {
                    CloseHandle(jobHandle);
                    jobHandle = 0;
                }

                ConsoleCancelEventHandler? cancelHandler = null;
                EventHandler? processExitHandler = null;
                cancelHandler = (_, eventArgs) =>
                {
                    _ = TerminateProcess(processInfo.hProcess, 1);
                    eventArgs.Cancel = true;
                };
                processExitHandler = (_, _) =>
                {
                    _ = TerminateProcess(processInfo.hProcess, 1);
                };
                Console.CancelKeyPress += cancelHandler;
                AppDomain.CurrentDomain.ProcessExit += processExitHandler;

                var waitResult = WaitForSupervisedChild(
                    processInfo.hProcess,
                    executionTimeoutSeconds,
                    executionReadyPath);
                Console.CancelKeyPress -= cancelHandler;
                AppDomain.CurrentDomain.ProcessExit -= processExitHandler;

                if (waitResult == WAIT_TIMEOUT)
                {
                    _ = TerminateProcess(processInfo.hProcess, ExecutionTimeoutExitCode);
                    _ = WaitForSingleObject(processInfo.hProcess, INFINITE);
                    childExitCode = ReportExecutionTimeout(
                        reportJsonPath,
                        ebootPath,
                        executionTimeoutSeconds!.Value,
                        invocationStartedTimestamp);
                    return true;
                }

                if (!GetExitCodeProcess(processInfo.hProcess, out var exitCode))
                {
                    return false;
                }

                childExitCode = unchecked((int)exitCode);
                ReportStallIfNeeded(
                    childExitCode,
                    reportJsonPath,
                    ebootPath,
                    invocationStartedTimestamp);

                Console.Error.WriteLine("[DEBUG] Running in mitigated child process (CET/CFG disabled).");
                return true;
            }
            finally
            {
                if (jobHandle != 0)
                {
                    CloseHandle(jobHandle);
                }

                CloseHandle(processInfo.hThread);
                CloseHandle(processInfo.hProcess);
            }
        }
        finally
        {
            Environment.SetEnvironmentVariable(MitigatedChildEnvironment, previousChildEnvironment);
            Environment.SetEnvironmentVariable(SupervisorReadyFileEnvironment, previousReadyFileEnvironment);
            TryDeleteSupervisorReadyFile(executionReadyPath);

            if (attributeList != 0)
            {
                DeleteProcThreadAttributeList(attributeList);
                Marshal.FreeHGlobal(attributeList);
            }

            if (mitigationPolicies != 0)
            {
                Marshal.FreeHGlobal(mitigationPolicies);
            }
        }
    }

    private static bool RunPortableSupervisedChild(
        string processPath,
        IReadOnlyList<string> childArgs,
        string ebootPath,
        string? reportJsonPath,
        int? executionTimeoutSeconds,
        string? executionReadyPath,
        long invocationStartedTimestamp,
        out int childExitCode)
    {
        childExitCode = 0;
        var startInfo = new ProcessStartInfo(processPath)
        {
            UseShellExecute = false,
        };
        foreach (var argument in childArgs)
        {
            startInfo.ArgumentList.Add(argument);
        }
        startInfo.Environment[MitigatedChildEnvironment] = "1";
        if (executionReadyPath is not null)
        {
            startInfo.Environment[SupervisorReadyFileEnvironment] = executionReadyPath;
        }

        using var process = Process.Start(startInfo);
        if (process is null)
        {
            return false;
        }

        ConsoleCancelEventHandler cancelHandler = (_, eventArgs) =>
        {
            TryKillPortableChild(process);
            eventArgs.Cancel = true;
        };
        EventHandler processExitHandler = (_, _) => TryKillPortableChild(process);
        Console.CancelKeyPress += cancelHandler;
        AppDomain.CurrentDomain.ProcessExit += processExitHandler;
        try
        {
            var exited = WaitForPortableSupervisedChild(
                process,
                executionTimeoutSeconds,
                executionReadyPath);
            if (!exited)
            {
                TryKillPortableChild(process);
                process.WaitForExit();
                childExitCode = ReportExecutionTimeout(
                    reportJsonPath,
                    ebootPath,
                    executionTimeoutSeconds!.Value,
                    invocationStartedTimestamp);
                return true;
            }

            childExitCode = process.ExitCode;
            ReportStallIfNeeded(
                childExitCode,
                reportJsonPath,
                ebootPath,
                invocationStartedTimestamp);

            return true;
        }
        finally
        {
            Console.CancelKeyPress -= cancelHandler;
            AppDomain.CurrentDomain.ProcessExit -= processExitHandler;
            TryDeleteSupervisorReadyFile(executionReadyPath);
        }
    }

    private static uint WaitForSupervisedChild(
        nint processHandle,
        int? executionTimeoutSeconds,
        string? executionReadyPath)
    {
        if (executionTimeoutSeconds is not { } timeoutSeconds)
        {
            return WaitForSingleObject(processHandle, INFINITE);
        }

        while (!IsSupervisedExecutionReady(executionReadyPath))
        {
            var waitResult = WaitForSingleObject(processHandle, 100);
            if (waitResult != WAIT_TIMEOUT)
            {
                return waitResult;
            }
        }

        return WaitForSingleObject(processHandle, checked((uint)timeoutSeconds * 1000u));
    }

    private static bool WaitForPortableSupervisedChild(
        Process process,
        int? executionTimeoutSeconds,
        string? executionReadyPath)
    {
        if (executionTimeoutSeconds is not { } timeoutSeconds)
        {
            process.WaitForExit();
            return true;
        }

        while (!IsSupervisedExecutionReady(executionReadyPath))
        {
            if (process.WaitForExit(100))
            {
                return true;
            }
        }

        return process.WaitForExit(checked(timeoutSeconds * 1000));
    }

    private static bool IsSupervisedExecutionReady(string? executionReadyPath) =>
        executionReadyPath is not null && File.Exists(executionReadyPath);

    private static void SignalSupervisedExecutionReady()
    {
        var executionReadyPath = Environment.GetEnvironmentVariable(SupervisorReadyFileEnvironment);
        if (string.IsNullOrWhiteSpace(executionReadyPath))
        {
            return;
        }

        if (!TryValidateSupervisorReadyPath(executionReadyPath, out var validatedPath))
        {
            throw new InvalidOperationException("The supervisor readiness path is invalid.");
        }

        try
        {
            using var marker = new FileStream(
                validatedPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.Read);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                "Could not signal supervised execution readiness.",
                ex);
        }
    }

    private static bool TryValidateSupervisorReadyPath(
        string executionReadyPath,
        out string validatedPath)
    {
        validatedPath = string.Empty;
        try
        {
            var fullPath = Path.GetFullPath(executionReadyPath);
            var tempPath = Path.GetFullPath(Path.GetTempPath())
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var comparison = OperatingSystem.IsWindows()
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal;
            var fileName = Path.GetFileName(fullPath);
            if (!string.Equals(Path.GetDirectoryName(fullPath), tempPath, comparison) ||
                !fileName.StartsWith(SupervisorReadyFilePrefix, StringComparison.Ordinal) ||
                !fileName.EndsWith(SupervisorReadyFileSuffix, StringComparison.Ordinal))
            {
                return false;
            }

            var token = fileName[
                SupervisorReadyFilePrefix.Length..
                ^SupervisorReadyFileSuffix.Length];
            if (!Guid.TryParseExact(token, "N", out _))
            {
                return false;
            }

            validatedPath = fullPath;
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static void TryDeleteSupervisorReadyFile(string? executionReadyPath)
    {
        if (executionReadyPath is null)
        {
            return;
        }

        try
        {
            File.Delete(executionReadyPath);
        }
        catch (Exception)
        {
        }
    }

    private static int ReportExecutionTimeout(
        string? reportJsonPath,
        string ebootPath,
        int executionTimeoutSeconds,
        long invocationStartedTimestamp)
    {
        var unit = executionTimeoutSeconds == 1 ? "second" : "seconds";
        var timeoutMessage = $"Execution timed out after {executionTimeoutSeconds} {unit}.";
        Console.Error.WriteLine($"[ERROR] {timeoutMessage}");
        var fullEbootPath = Path.GetFullPath(ebootPath);
        if (!TryFinalizeRunningExecutionReport(
                reportJsonPath,
                fullEbootPath,
                "EXECUTION_TIMED_OUT",
                timeoutMessage,
                invocationStartedTimestamp))
        {
            TryWriteExecutionReport(
                reportJsonPath,
                fullEbootPath,
                result: null,
                runtime: null,
                hostError: timeoutMessage,
                invocationStartedTimestamp: invocationStartedTimestamp,
                incompleteResultName: "EXECUTION_TIMED_OUT");
        }
        return ExecutionTimeoutExitCode;
    }

    private static void ReportStallIfNeeded(
        int childExitCode,
        string? reportJsonPath,
        string ebootPath,
        long invocationStartedTimestamp)
    {
        if (childExitCode != DirectExecutionBackend.StallWatchdogExitCode)
        {
            return;
        }

        const string stallMessage =
            "Execution stalled and was terminated by the native watchdog.";
        var fullEbootPath = Path.GetFullPath(ebootPath);
        if (!TryFinalizeRunningExecutionReport(
                reportJsonPath,
                fullEbootPath,
                "EXECUTION_STALLED",
                stallMessage,
                invocationStartedTimestamp))
        {
            TryWriteExecutionReport(
                reportJsonPath,
                fullEbootPath,
                result: null,
                runtime: null,
                hostError: stallMessage,
                invocationStartedTimestamp: invocationStartedTimestamp,
                incompleteResultName: "EXECUTION_STALLED");
        }
    }

    private static void TryKillPortableChild(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch
        {
        }
    }

    private static bool TryGetLogFileArgument(IReadOnlyList<string> args, out string path)
    {
        for (var i = 0; i < args.Count; i++)
        {
            var argument = args[i];
            if (string.Equals(argument, "--log-file", StringComparison.OrdinalIgnoreCase))
            {
                if (i + 1 < args.Count &&
                    !string.IsNullOrWhiteSpace(args[i + 1]) &&
                    !args[i + 1].StartsWith("--", StringComparison.Ordinal) &&
                    ShouldConsumeLogFilePath(args, i + 1))
                {
                    path = args[i + 1];
                    return true;
                }

                path = BuildDefaultLogFilePath(TryFindEbootPathToken(args));
                return true;
            }

            const string logFilePrefix = "--log-file=";
            if (argument.StartsWith(logFilePrefix, StringComparison.OrdinalIgnoreCase) &&
                !string.IsNullOrWhiteSpace(argument[logFilePrefix.Length..]))
            {
                path = argument[logFilePrefix.Length..];
                return true;
            }
        }

        path = string.Empty;
        return false;
    }

    private static string BuildDefaultLogFilePath(string? ebootPath)
    {
        var baseDirectory = AppContext.BaseDirectory;
        var logsDirectory = Path.Combine(baseDirectory, "user", "logs");
        var name = TryReadTitleId(ebootPath) ?? "UNKNOWN";

        foreach (var invalid in Path.GetInvalidFileNameChars())
        {
            name = name.Replace(invalid, '_');
        }

        return Path.Combine(logsDirectory, $"{name}-{DateTime.Now:yyyyMMdd-HHmmss}.log");
    }

    private static string? TryReadTitleId(string? ebootPath)
    {
        if (string.IsNullOrWhiteSpace(ebootPath))
        {
            return null;
        }

        try
        {
            var directory = Path.GetDirectoryName(Path.GetFullPath(ebootPath));
            if (string.IsNullOrEmpty(directory))
            {
                return null;
            }

            foreach (var paramPath in new[]
            {
                Path.Combine(directory, "sce_sys", "param.json"),
                Path.Combine(directory, "param.json"),
            })
            {
                if (!File.Exists(paramPath))
                {
                    continue;
                }

                using var stream = File.OpenRead(paramPath);
                using var document = JsonDocument.Parse(stream);
                if (document.RootElement.TryGetProperty("titleId", out var titleIdElement) &&
                    titleIdElement.ValueKind == JsonValueKind.String)
                {
                    var titleId = titleIdElement.GetString();
                    if (!string.IsNullOrWhiteSpace(titleId))
                    {
                        return titleId.Trim();
                    }
                }
            }
        }
        catch
        {
            // Logging should never block launch; unknown title ids use a stable fallback.
        }

        return null;
    }

    private static string? TryFindEbootPathToken(IReadOnlyList<string> args)
    {
        for (var i = args.Count - 1; i >= 0; i--)
        {
            var argument = args[i];
            if (string.IsNullOrWhiteSpace(argument) ||
                argument.StartsWith("--", StringComparison.Ordinal))
            {
                continue;
            }

            return argument;
        }

        return null;
    }

    private static bool ShouldConsumeLogFilePath(IReadOnlyList<string> args, int candidateIndex)
    {
        var candidate = args[candidateIndex];
        if (LooksLikeLogFilePath(candidate))
        {
            return true;
        }

        for (var i = candidateIndex + 1; i < args.Count; i++)
        {
            var argument = args[i];
            if (!string.IsNullOrWhiteSpace(argument) &&
                !argument.StartsWith("--", StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static bool LooksLikeLogFilePath(string path)
    {
        var extension = Path.GetExtension(path);
        return string.Equals(extension, ".log", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(extension, ".txt", StringComparison.OrdinalIgnoreCase);
    }

    private static void TryEnableConsoleFileMirror(string path)
    {
        lock (ConsoleMirrorSync)
        {
            if (_consoleMirrorFile is not null)
            {
                return;
            }

            try
            {
                var directory = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                var stream = new FileStream(
                    path,
                    FileMode.Create,
                    FileAccess.Write,
                    FileShare.ReadWrite,
                    bufferSize: 4096,
                    FileOptions.SequentialScan);
                _consoleMirrorFile = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false))
                {
                    AutoFlush = true,
                };

                Console.SetOut(new TeeTextWriter(Console.Out, _consoleMirrorFile));
                Console.SetError(new TeeTextWriter(Console.Error, _consoleMirrorFile));
                Console.Error.WriteLine($"[DEBUG] Log file: {Path.GetFullPath(path)}");
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[WARN] Could not open log file '{path}': {ex.Message}");
            }
        }
    }

    private static void DropConsoleFileMirror()
    {
        lock (ConsoleMirrorSync)
        {
            try
            {
                _consoleMirrorFile?.Flush();
                _consoleMirrorFile?.Dispose();
            }
            catch
            {
            }

            _consoleMirrorFile = null;
        }
    }

    private static void ConfigureInheritedStdHandles(ref STARTUPINFO startupInfo)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var input = GetStdHandle(STD_INPUT_HANDLE);
        var output = GetStdHandle(STD_OUTPUT_HANDLE);
        var error = GetStdHandle(STD_ERROR_HANDLE);
        if (!IsHandleValid(output) && !IsHandleValid(error))
        {
            return;
        }

        if (IsHandleValid(input))
        {
            _ = SetHandleInformation(input, HANDLE_FLAG_INHERIT, HANDLE_FLAG_INHERIT);
            startupInfo.hStdInput = input;
        }

        if (IsHandleValid(output))
        {
            _ = SetHandleInformation(output, HANDLE_FLAG_INHERIT, HANDLE_FLAG_INHERIT);
            startupInfo.hStdOutput = output;
        }

        if (IsHandleValid(error))
        {
            _ = SetHandleInformation(error, HANDLE_FLAG_INHERIT, HANDLE_FLAG_INHERIT);
            startupInfo.hStdError = error;
        }

        startupInfo.dwFlags |= STARTF_USESTDHANDLES;
    }

    private static string BuildCommandLine(string processPath, IReadOnlyList<string> args)
    {
        var builder = new StringBuilder();
        builder.Append(QuoteArgument(processPath));
        for (var i = 0; i < args.Count; i++)
        {
            builder.Append(' ');
            builder.Append(QuoteArgument(args[i]));
        }

        return builder.ToString();
    }

    private static bool TryEnableKillOnJobClose(nint jobHandle)
    {
        var extendedLimitInfo = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION
        {
            BasicLimitInformation = new JOBOBJECT_BASIC_LIMIT_INFORMATION
            {
                LimitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE,
            },
        };

        var size = Marshal.SizeOf<JOBOBJECT_EXTENDED_LIMIT_INFORMATION>();
        var memory = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.StructureToPtr(extendedLimitInfo, memory, false);
            return SetInformationJobObject(
                jobHandle,
                JobObjectExtendedLimitInformation,
                memory,
                unchecked((uint)size));
        }
        finally
        {
            Marshal.FreeHGlobal(memory);
        }
    }

    private static string QuoteArgument(string argument)
    {
        if (argument.Length == 0)
        {
            return "\"\"";
        }

        var needsQuotes = false;
        foreach (var c in argument)
        {
            if (char.IsWhiteSpace(c) || c == '"')
            {
                needsQuotes = true;
                break;
            }
        }

        if (!needsQuotes)
        {
            return argument;
        }

        var builder = new StringBuilder(argument.Length + 2);
        builder.Append('"');

        var backslashCount = 0;
        foreach (var c in argument)
        {
            if (c == '\\')
            {
                backslashCount++;
                continue;
            }

            if (c == '"')
            {
                builder.Append('\\', (backslashCount * 2) + 1);
                builder.Append('"');
                backslashCount = 0;
                continue;
            }

            if (backslashCount > 0)
            {
                builder.Append('\\', backslashCount);
                backslashCount = 0;
            }

            builder.Append(c);
        }

        if (backslashCount > 0)
        {
            builder.Append('\\', backslashCount * 2);
        }

        builder.Append('"');
        return builder.ToString();
    }

    private static bool TryWriteExecutionReport(
        string? reportJsonPath,
        string executablePath,
        OrbisGen2Result? result,
        ISharpEmuRuntime? runtime,
        string? hostError,
        long invocationStartedTimestamp,
        string? incompleteResultName = null,
        string mode = "execution",
        CliExecutionResult? resultOverride = null,
        CliReportFingerprint? fingerprintOverride = null)
    {
        if (string.IsNullOrWhiteSpace(reportJsonPath))
        {
            return true;
        }

        try
        {
            var fullPath = Path.GetFullPath(reportJsonPath);
            var directory = Path.GetDirectoryName(fullPath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var executionResult = resultOverride ?? (result is { } completedResult
                ? BuildExecutionResult(completedResult)
                : new CliExecutionResult(incompleteResultName ?? "HOST_ERROR", null, false));
            var fingerprint = fingerprintOverride ?? BuildReportFingerprint(
                executablePath,
                runtime?.LastPreparedApplication);
            var report = new CliExecutionReport(
                SchemaVersion: 3,
                Mode: mode,
                GeneratedAtUtc: DateTimeOffset.UtcNow,
                ExecutablePath: executablePath,
                ExecutableSizeBytes: fingerprint.ExecutableSizeBytes,
                ExecutableSha256: fingerprint.ExecutableSha256,
                Bundle: fingerprint.Bundle,
                DurationMilliseconds: GetElapsedMilliseconds(invocationStartedTimestamp),
                Build: BuildExecutionReportBuild(),
                Host: BuildExecutionReportHost(),
                Application: BuildApplicationReport(
                    runtime?.LastApplicationMetadata ?? TryReadApplicationMetadataForReport(executablePath)),
                Image: BuildImageReport(runtime?.LastLoadedImage),
                Modules: BuildModuleReports(runtime?.LastPreparedApplication, executablePath),
                ModuleInitializers: BuildModuleInitializerReports(runtime, executablePath),
                SkippedModules: BuildSkippedModuleReports(runtime?.LastPreparedApplication, executablePath),
                ModuleLoadFailures: BuildModuleFailureReports(runtime?.LastPreparedApplication, executablePath),
                Result: executionResult,
                CpuSession: BuildCpuSessionReport(runtime?.LastCpuSessionSummary),
                CpuTrap: BuildCpuTrapReport(
                    runtime?.LastCpuTrapInfo,
                    runtime?.LastPreparedApplication,
                    executablePath),
                CpuMemoryFault: BuildCpuMemoryFaultReport(runtime?.LastCpuMemoryFaultInfo),
                CpuNotImplemented: BuildCpuNotImplementedReport(runtime?.LastCpuNotImplementedInfo),
                SessionSummary: runtime?.LastSessionSummary,
                Diagnostics: runtime?.LastExecutionDiagnostics,
                ImportTrace: runtime?.LastExecutionTrace,
                MilestoneLog: runtime?.LastMilestoneLog,
                BasicBlockTrace: runtime?.LastBasicBlockTrace,
                HostError: hostError);
            WriteExecutionReport(fullPath, report);
            return true;
        }
        catch (Exception ex)
        {
            Log.Error($"Failed to write execution report '{reportJsonPath}'.", ex);
            return false;
        }
    }

    private static bool TryFinalizeRunningExecutionReport(
        string? reportJsonPath,
        string executablePath,
        string resultName,
        string hostError,
        long invocationStartedTimestamp)
    {
        if (string.IsNullOrWhiteSpace(reportJsonPath))
        {
            return false;
        }

        try
        {
            var fullPath = Path.GetFullPath(reportJsonPath);
            if (!File.Exists(fullPath))
            {
                return false;
            }

            var report = JsonSerializer.Deserialize<CliExecutionReport>(
                File.ReadAllText(fullPath),
                new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true,
                });
            if (report is null ||
                !string.Equals(
                    report.Result.Name,
                    "EXECUTION_RUNNING",
                    StringComparison.Ordinal) ||
                !string.Equals(
                    Path.GetFullPath(report.ExecutablePath),
                    executablePath,
                    StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            var finalizedReport = report with
            {
                GeneratedAtUtc = DateTimeOffset.UtcNow,
                DurationMilliseconds =
                    GetElapsedMilliseconds(invocationStartedTimestamp),
                Result = new CliExecutionResult(
                    resultName,
                    Code: null,
                    Succeeded: false),
                HostError = hostError,
            };
            WriteExecutionReport(fullPath, finalizedReport);
            return true;
        }
        catch (Exception ex)
        {
            Log.Warn(
                $"Could not preserve running execution report " +
                $"'{reportJsonPath}': {ex.Message}");
            return false;
        }
    }

    private static void WriteExecutionReport(
        string fullPath,
        CliExecutionReport report)
    {
        var json = JsonSerializer.Serialize(report, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = true,
        });
        var temporaryPath = $"{fullPath}.{Guid.NewGuid():N}.tmp";
        try
        {
            File.WriteAllText(
                temporaryPath,
                json,
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            File.Move(temporaryPath, fullPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                try
                {
                    File.Delete(temporaryPath);
                }
                catch (Exception)
                {
                    // Preserve the original report-writing error.
                }
            }
        }
    }

    private static long? TryGetFileSize(string path)
    {
        try
        {
            return File.Exists(path) ? new FileInfo(path).Length : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static string? TryComputeFileSha256(string path)
    {
        try
        {
            using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 1024 * 1024,
                FileOptions.SequentialScan);
            return Convert.ToHexStringLower(SHA256.HashData(stream));
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static long GetElapsedMilliseconds(long startedTimestamp)
    {
        return Math.Max(0, (long)Math.Round(Stopwatch.GetElapsedTime(startedTimestamp).TotalMilliseconds));
    }

    private static CliBuildReport BuildExecutionReportBuild()
    {
        return new CliBuildReport(
            BuildInfo.CommitSha,
            BuildInfo.Branch,
            BuildInfo.Repository,
            BuildInfo.Configuration,
            BuildInfo.IsOfficialRelease,
            BuildInfo.WorkflowRunUrl);
    }

    private static CliHostReport BuildExecutionReportHost()
    {
        return new CliHostReport(
            RuntimeInformation.OSDescription,
            RuntimeInformation.ProcessArchitecture.ToString(),
            RuntimeInformation.FrameworkDescription,
            HostSystemInfo.CpuName,
            HostSystemInfo.GpuName);
    }

    private static CliApplicationReport? BuildApplicationReport(Ps5ApplicationMetadata? metadata)
    {
        return metadata is null
            ? null
            : new CliApplicationReport(
                metadata.Title,
                metadata.TitleId,
                metadata.ContentId,
                metadata.Version);
    }

    private static CliImageReport? BuildImageReport(SelfImage? image)
    {
        if (image is null)
        {
            return null;
        }

        return new CliImageReport(
            Format: image.IsSelf ? "SELF" : "ELF",
            Generation: image.ElfHeader.AbiVersion == 2 ? "Gen5" : "Gen4",
            ElfType: image.ElfHeader.Type,
            ElfMachine: image.ElfHeader.Machine,
            ElfAbi: image.ElfHeader.Abi,
            ElfAbiVersion: image.ElfHeader.AbiVersion,
            EntryPoint: FormatAddress(image.EntryPoint),
            ImageBase: FormatAddress(image.ImageBase),
            ProcParamAddress: FormatAddress(image.ProcParamAddress),
            ProgramHeaderCount: image.ProgramHeaders.Count,
            MappedRegionCount: image.MappedRegions.Count,
            ImportStubCount: image.ImportStubs.Count,
            RuntimeSymbolCount: image.RuntimeSymbols.Count,
            ImportedRelocationCount: image.ImportedRelocations.Count,
            UnsupportedRelocationTypes: image.UnsupportedRelocationTypes,
            PreInitializerCount: image.PreInitializerFunctions.Count,
            InitializerCount: image.InitializerFunctions.Count,
            InitFunctionEntryPoint: image.InitFunctionEntryPoint == 0
                ? null
                : FormatAddress(image.InitFunctionEntryPoint));
    }

    private static IReadOnlyList<CliModuleReport>? BuildModuleReports(
        PreparedApplication? application,
        string executablePath)
    {
        return application?.Modules
            .Select(module => new CliModuleReport(
                BuildBundleRelativePath(executablePath, module.Path),
                BuildImageReport(module.Image)!))
            .ToArray();
    }

    private static IReadOnlyList<CliModuleInitializerReport>? BuildModuleInitializerReports(
        ISharpEmuRuntime? runtime,
        string executablePath)
    {
        return runtime?.LastModuleInitializerExecutions
            .Select(execution => new CliModuleInitializerReport(
                BuildBundleRelativePath(executablePath, execution.ModulePath),
                execution.InitializerIndex,
                FormatAddress(execution.EntryPoint),
                BuildExecutionResult(execution.Result)))
            .ToArray();
    }

    private static IReadOnlyList<CliModuleLoadFailureReport>? BuildModuleFailureReports(
        PreparedApplication? application,
        string executablePath)
    {
        return application?.ModuleLoadFailures
            .Select(failure => new CliModuleLoadFailureReport(
                BuildBundleRelativePath(executablePath, failure.Path),
                failure.ErrorType,
                failure.Message))
            .ToArray();
    }

    private static IReadOnlyList<CliSkippedModuleReport>? BuildSkippedModuleReports(
        PreparedApplication? application,
        string executablePath)
    {
        return application?.SkippedModules
            .Select(module => new CliSkippedModuleReport(
                BuildBundleRelativePath(executablePath, module.Path),
                module.Reason))
            .ToArray();
    }

    private static string BuildBundleRelativePath(string executablePath, string path)
    {
        try
        {
            var bundleRoot = Path.GetDirectoryName(Path.GetFullPath(executablePath));
            if (!string.IsNullOrWhiteSpace(bundleRoot))
            {
                return Path.GetRelativePath(bundleRoot, path).Replace('\\', '/');
            }
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            // Fall back to a stable filename below.
        }

        return Path.GetFileName(path);
    }

    private static CliBundleReport BuildBundleReport(
        string executablePath,
        long? executableSizeBytes,
        string? executableSha256,
        PreparedApplication? application)
    {
        var paths = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [BuildBundleRelativePath(executablePath, executablePath)] = executablePath,
        };
        if (application is not null)
        {
            foreach (var module in application.Modules)
            {
                paths[BuildBundleRelativePath(executablePath, module.Path)] = module.Path;
            }

            foreach (var failure in application.ModuleLoadFailures)
            {
                paths[BuildBundleRelativePath(executablePath, failure.Path)] = failure.Path;
            }

            foreach (var skippedModule in application.SkippedModules)
            {
                paths[BuildBundleRelativePath(executablePath, skippedModule.Path)] = skippedModule.Path;
            }
        }

        var bundleRoot = Path.GetDirectoryName(executablePath);
        if (!string.IsNullOrWhiteSpace(bundleRoot))
        {
            foreach (var metadataPath in new[]
            {
                Path.Combine(bundleRoot, "sce_sys", "param.json"),
                Path.Combine(bundleRoot, "param.json"),
            })
            {
                if (File.Exists(metadataPath))
                {
                    paths[BuildBundleRelativePath(executablePath, metadataPath)] = metadataPath;
                }
            }
        }

        var mainRelativePath = BuildBundleRelativePath(executablePath, executablePath);
        var files = paths
            .OrderBy(pair => pair.Key, StringComparer.Ordinal)
            .Select(pair => string.Equals(pair.Key, mainRelativePath, StringComparison.OrdinalIgnoreCase)
                ? new CliFileFingerprint(pair.Key, executableSizeBytes, executableSha256)
                : new CliFileFingerprint(
                    pair.Key,
                    TryGetFileSize(pair.Value),
                    TryComputeFileSha256(pair.Value)))
            .ToArray();

        return new CliBundleReport(
            ManifestVersion: 1,
            Algorithm: "SHA-256",
            Sha256: TryComputeBundleSha256(files),
            Files: files);
    }

    private static CliReportFingerprint BuildReportFingerprint(
        string executablePath,
        PreparedApplication? application)
    {
        var executableSizeBytes = TryGetFileSize(executablePath);
        var executableSha256 = TryComputeFileSha256(executablePath);
        return new CliReportFingerprint(
            executableSizeBytes,
            executableSha256,
            BuildBundleReport(
                executablePath,
                executableSizeBytes,
                executableSha256,
                application));
    }

    private static string? TryComputeBundleSha256(IReadOnlyList<CliFileFingerprint> files)
    {
        if (files.Count == 0 || files.Any(file => file.SizeBytes is null || file.Sha256 is null))
        {
            return null;
        }

        var canonicalManifest = new StringBuilder();
        foreach (var file in files)
        {
            canonicalManifest
                .Append(file.Path)
                .Append('\0')
                .Append(file.SizeBytes!.Value.ToString(CultureInfo.InvariantCulture))
                .Append('\0')
                .Append(file.Sha256)
                .Append('\n');
        }

        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonicalManifest.ToString())));
    }

    private static Ps5ApplicationMetadata? TryReadApplicationMetadataForReport(string executablePath)
    {
        string? bundleRoot;
        try
        {
            bundleRoot = Path.GetDirectoryName(Path.GetFullPath(executablePath));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(bundleRoot))
        {
            return null;
        }

        var fileSystem = new PhysicalFileSystem();
        foreach (var candidate in new[]
        {
            Path.Combine(bundleRoot, "sce_sys", "param.json"),
            Path.Combine(bundleRoot, "param.json"),
        })
        {
            if (fileSystem.Exists(candidate))
            {
                return Ps5ParamJsonReader.TryReadApplicationMetadata(fileSystem, candidate);
            }
        }

        return null;
    }

    private static CliCpuSessionReport? BuildCpuSessionReport(CpuSessionSummary? summary)
    {
        if (summary is not { } value)
        {
            return null;
        }

        return new CliCpuSessionReport(
            BuildExecutionResult(value.Result),
            value.Reason.ToString(),
            value.ExitCode,
            FormatAddress(value.LastGuestRip),
            FormatAddress(value.LastStubRip),
            value.TotalInstructions,
            value.ImportsHit,
            value.UniqueNidsHit);
    }

    private static CliCpuTrapReport? BuildCpuTrapReport(
        CpuTrapInfo? trap,
        PreparedApplication? application,
        string executablePath)
    {
        if (trap is not { } value)
        {
            return null;
        }

        return new CliCpuTrapReport(
            FormatAddress(value.InstructionPointer),
            $"0x{value.Opcode:X2}",
            value.ExceptionCode is { } exceptionCode ? $"0x{exceptionCode:X8}" : null,
            value.AccessAddress is { } accessAddress ? FormatAddress(accessAddress) : null,
            value.AccessKind?.ToString().ToLowerInvariant(),
            value.InstructionBytes,
            value.InstructionLength,
            value.InstructionMnemonic,
            value.InstructionText,
            value.InstructionFlowControl,
            BuildCpuRegisterReport(value.Registers),
            FormatAddress(value.GuestThreadHandle),
            BuildCpuCodeWindowReport(value.CodeWindow),
            BuildCpuMemoryWindowReport(value.StackWindow),
            BuildCpuStackCodeCandidateReports(value.StackCodeCandidates, application, executablePath),
            BuildCodeLocationReport(value.InstructionPointer, application, executablePath),
            BuildCpuStackFrameReports(value.StackFrames, application, executablePath));
    }

    private static IReadOnlyList<CliCpuStackFrameReport>? BuildCpuStackFrameReports(
        IReadOnlyList<CpuStackFrame>? stackFrames,
        PreparedApplication? application,
        string executablePath) =>
        stackFrames?.Select(frame => new CliCpuStackFrameReport(
            FormatAddress(frame.FramePointer),
            FormatAddress(frame.NextFramePointer),
            FormatAddress(frame.ReturnAddress),
            BuildCodeLocationReport(frame.ReturnAddress, application, executablePath),
            BuildCpuCodeWindowReport(frame.ReturnCodeWindow))).ToArray();

    private static CliCpuCodeWindowReport? BuildCpuCodeWindowReport(CpuCodeWindow? codeWindow) =>
        codeWindow is not { } value
            ? null
            : new CliCpuCodeWindowReport(
                FormatAddress(value.StartAddress),
                value.InstructionOffset,
                value.Bytes);

    private static CliCpuMemoryWindowReport? BuildCpuMemoryWindowReport(CpuMemoryWindow? memoryWindow) =>
        memoryWindow is not { } value
            ? null
            : new CliCpuMemoryWindowReport(
                FormatAddress(value.StartAddress),
                value.Bytes,
                value.ReferenceOffset);

    private static IReadOnlyList<CliCpuStackCodeCandidateReport>? BuildCpuStackCodeCandidateReports(
        IReadOnlyList<CpuStackCodeCandidate>? candidates,
        PreparedApplication? application,
        string executablePath) =>
        candidates?.Select(candidate => new CliCpuStackCodeCandidateReport(
            candidate.StackOffset,
            FormatAddress(candidate.Address),
            BuildCodeLocationReport(candidate.Address, application, executablePath),
            BuildCpuCodeWindowReport(candidate.CodeWindow),
            candidate.Instructions?.Select(instruction => new CliCpuDecodedInstructionReport(
                FormatAddress(instruction.Address),
                instruction.Length,
                instruction.Bytes,
                instruction.Mnemonic,
                instruction.Text,
                instruction.FlowControl,
                instruction.NearBranchTarget is { } branchTarget ? FormatAddress(branchTarget) : null,
                instruction.MemoryAddress is { } memoryAddress ? FormatAddress(memoryAddress) : null)).ToArray())).ToArray();

    private static CliCodeLocationReport? BuildCodeLocationReport(
        ulong address,
        PreparedApplication? application,
        string executablePath)
    {
        if (application is null)
        {
            return null;
        }

        if (ImageContainsAddress(application.MainImage, address))
        {
            return new CliCodeLocationReport(
                BuildBundleRelativePath(executablePath, executablePath),
                FormatAddress(address - application.MainImage.ImageBase));
        }

        foreach (var module in application.Modules)
        {
            if (ImageContainsAddress(module.Image, address))
            {
                return new CliCodeLocationReport(
                    BuildBundleRelativePath(executablePath, module.Path),
                    FormatAddress(address - module.Image.ImageBase));
            }
        }

        return null;
    }

    private static bool ImageContainsAddress(SelfImage image, ulong address) =>
        image.MappedRegions.Any(region =>
            address >= region.VirtualAddress &&
            address - region.VirtualAddress < region.MemorySize);

    private static CliCpuRegisterReport? BuildCpuRegisterReport(CpuRegisterSnapshot? registers) =>
        registers is not { } value
            ? null
            : new CliCpuRegisterReport(
                FormatAddress(value.Rax),
                FormatAddress(value.Rbx),
                FormatAddress(value.Rcx),
                FormatAddress(value.Rdx),
                FormatAddress(value.Rsi),
                FormatAddress(value.Rdi),
                FormatAddress(value.Rbp),
                FormatAddress(value.Rsp),
                FormatAddress(value.R8),
                FormatAddress(value.R9),
                FormatAddress(value.R10),
                FormatAddress(value.R11),
                FormatAddress(value.R12),
                FormatAddress(value.R13),
                FormatAddress(value.R14),
                FormatAddress(value.R15));

    private static CliCpuMemoryFaultReport? BuildCpuMemoryFaultReport(CpuMemoryFaultInfo? fault)
    {
        if (fault is not { } value)
        {
            return null;
        }

        return new CliCpuMemoryFaultReport(
            FormatAddress(value.InstructionPointer),
            value.Opcode is { } opcode ? $"0x{opcode:X2}" : null,
            new CliCpuMemoryAccessReport(
                FormatAddress(value.Access.Address),
                value.Access.Size,
                value.Access.IsWrite ? "write" : "read"));
    }

    private static CliCpuNotImplementedReport? BuildCpuNotImplementedReport(CpuNotImplementedInfo? notImplemented)
    {
        if (notImplemented is not { } value)
        {
            return null;
        }

        return new CliCpuNotImplementedReport(
            value.Source.ToString(),
            FormatAddress(value.InstructionPointer),
            value.Nid,
            value.ExportName,
            value.LibraryName,
            value.Detail);
    }

    private static CliExecutionResult BuildExecutionResult(OrbisGen2Result result)
    {
        return new CliExecutionResult(
            result.ToString(),
            (int)result,
            result == OrbisGen2Result.ORBIS_GEN2_OK);
    }

    private static string FormatAddress(ulong address) => $"0x{address:X16}";

    private static void PrintUsage()
    {
        Log.Info("Usage: SharpEmu.CLI [--load-only] [--expect-bundle-sha256=<hash>] [--strict] [--trace-imports[=N]] [--cpu-engine=<native>] [--log-level=<level>] [--log-file[=<path>]] [--report-json=<path>] [--timeout-seconds=N] <path-to-eboot.bin>");
        Log.Info(@"Example: SharpEmu.CLI --cpu-engine=native --trace-imports=64 --timeout-seconds=300 --report-json execution.json ""E:\Games\...\eboot.bin""");
        Log.Info(@"Inspect example: SharpEmu.CLI --load-only --expect-bundle-sha256 <hash> --report-json image.json ""E:\Games\...\eboot.bin""");
    }

    private static bool TryParseArguments(
        string[] args,
        out string ebootPath,
        out SharpEmuRuntimeOptions runtimeOptions,
        out LogLevel logLevel,
        out string? logFilePath,
        out string? reportJsonPath,
        out int? executionTimeoutSeconds,
        out bool loadOnly,
        out string? expectedBundleSha256)
    {
        reportJsonPath = null;
        executionTimeoutSeconds = null;
        loadOnly = false;
        expectedBundleSha256 = null;
        if (args.Length == 0)
        {
            ebootPath = string.Empty;
            runtimeOptions = default;
            logLevel = SharpEmuLog.MinimumLevel;
            logFilePath = null;
            return false;
        }

        var strictDynlibResolution = false;
        var importTraceLimit = 0;
        var cpuEngine = CpuExecutionEngine.NativeOnly;
        logFilePath = null;
        logLevel = SharpEmuLog.MinimumLevel;
        var pathTokens = new List<string>(args.Length);
        for (var i = 0; i < args.Length; i++)
        {
            var argument = args[i];
            if (string.Equals(argument, "--load-only", StringComparison.OrdinalIgnoreCase))
            {
                loadOnly = true;
                continue;
            }

            if (string.Equals(argument, "--strict", StringComparison.OrdinalIgnoreCase))
            {
                strictDynlibResolution = true;
                continue;
            }

            if (string.Equals(argument, "--trace-imports", StringComparison.OrdinalIgnoreCase))
            {
                importTraceLimit = DefaultImportTraceLimit;
                if (i + 1 < args.Length && int.TryParse(args[i + 1], out var explicitLimit))
                {
                    importTraceLimit = Math.Max(0, explicitLimit);
                    i++;
                }

                continue;
            }

            if (string.Equals(argument, "--log-level", StringComparison.OrdinalIgnoreCase))
            {
                if (i + 1 >= args.Length || !SharpEmuLog.TryParseLevel(args[i + 1], out logLevel))
                {
                    ebootPath = string.Empty;
                    runtimeOptions = default;
                    logFilePath = null;
                    return false;
                }

                i++;
                continue;
            }

            if (string.Equals(argument, "--cpu-engine", StringComparison.OrdinalIgnoreCase))
            {
                if (i + 1 >= args.Length || !TryParseCpuEngine(args[i + 1], out cpuEngine))
                {
                    ebootPath = string.Empty;
                    runtimeOptions = default;
                    logFilePath = null;
                    return false;
                }

                i++;
                continue;
            }

            if (string.Equals(argument, "--log-file", StringComparison.OrdinalIgnoreCase))
            {
                if (i + 1 < args.Length &&
                    !string.IsNullOrWhiteSpace(args[i + 1]) &&
                    !args[i + 1].StartsWith("--", StringComparison.Ordinal) &&
                    ShouldConsumeLogFilePath(args, i + 1))
                {
                    logFilePath = args[++i];
                }
                else
                {
                    logFilePath = BuildDefaultLogFilePath(TryFindEbootPathToken(args));
                }

                continue;
            }

            if (string.Equals(argument, "--report-json", StringComparison.OrdinalIgnoreCase))
            {
                if (i + 1 >= args.Length ||
                    string.IsNullOrWhiteSpace(args[i + 1]) ||
                    args[i + 1].StartsWith("--", StringComparison.Ordinal))
                {
                    ebootPath = string.Empty;
                    runtimeOptions = default;
                    logLevel = SharpEmuLog.MinimumLevel;
                    logFilePath = null;
                    return false;
                }

                reportJsonPath = args[++i];
                continue;
            }

            if (string.Equals(argument, "--expect-bundle-sha256", StringComparison.OrdinalIgnoreCase))
            {
                if (i + 1 >= args.Length ||
                    !TryNormalizeSha256(args[i + 1], out expectedBundleSha256))
                {
                    ebootPath = string.Empty;
                    runtimeOptions = default;
                    logLevel = SharpEmuLog.MinimumLevel;
                    logFilePath = null;
                    return false;
                }

                i++;
                continue;
            }

            if (string.Equals(argument, "--timeout-seconds", StringComparison.OrdinalIgnoreCase))
            {
                if (i + 1 >= args.Length ||
                    !TryParseExecutionTimeoutSeconds(args[i + 1], out var timeoutSeconds))
                {
                    ebootPath = string.Empty;
                    runtimeOptions = default;
                    logLevel = SharpEmuLog.MinimumLevel;
                    logFilePath = null;
                    return false;
                }

                executionTimeoutSeconds = timeoutSeconds;
                i++;
                continue;
            }

            const string logLevelPrefix = "--log-level=";
            if (argument.StartsWith(logLevelPrefix, StringComparison.OrdinalIgnoreCase))
            {
                var valueText = argument[logLevelPrefix.Length..];
                if (!SharpEmuLog.TryParseLevel(valueText, out logLevel))
                {
                    ebootPath = string.Empty;
                    runtimeOptions = default;
                    return false;
                }

                continue;
            }

            const string cpuEnginePrefix = "--cpu-engine=";
            if (argument.StartsWith(cpuEnginePrefix, StringComparison.OrdinalIgnoreCase))
            {
                var valueText = argument[cpuEnginePrefix.Length..];
                if (!TryParseCpuEngine(valueText, out cpuEngine))
                {
                    ebootPath = string.Empty;
                    runtimeOptions = default;
                    logLevel = SharpEmuLog.MinimumLevel;
                    logFilePath = null;
                    return false;
                }

                continue;
            }

            const string tracePrefix = "--trace-imports=";
            if (argument.StartsWith(tracePrefix, StringComparison.OrdinalIgnoreCase))
            {
                var valueText = argument[tracePrefix.Length..];
                if (!int.TryParse(valueText, out importTraceLimit))
                {
                    ebootPath = string.Empty;
                    runtimeOptions = default;
                    logLevel = SharpEmuLog.MinimumLevel;
                    return false;
                }

                importTraceLimit = Math.Max(0, importTraceLimit);
                continue;
            }

            const string logFilePrefix = "--log-file=";
            if (argument.StartsWith(logFilePrefix, StringComparison.OrdinalIgnoreCase))
            {
                logFilePath = argument[logFilePrefix.Length..];
                if (string.IsNullOrWhiteSpace(logFilePath))
                {
                    ebootPath = string.Empty;
                    runtimeOptions = default;
                    logLevel = SharpEmuLog.MinimumLevel;
                    return false;
                }

                continue;
            }

            const string reportJsonPrefix = "--report-json=";
            if (argument.StartsWith(reportJsonPrefix, StringComparison.OrdinalIgnoreCase))
            {
                reportJsonPath = argument[reportJsonPrefix.Length..];
                if (string.IsNullOrWhiteSpace(reportJsonPath))
                {
                    ebootPath = string.Empty;
                    runtimeOptions = default;
                    logLevel = SharpEmuLog.MinimumLevel;
                    logFilePath = null;
                    return false;
                }

                continue;
            }

            const string expectedBundleSha256Prefix = "--expect-bundle-sha256=";
            if (argument.StartsWith(expectedBundleSha256Prefix, StringComparison.OrdinalIgnoreCase))
            {
                if (!TryNormalizeSha256(
                        argument[expectedBundleSha256Prefix.Length..],
                        out expectedBundleSha256))
                {
                    ebootPath = string.Empty;
                    runtimeOptions = default;
                    logLevel = SharpEmuLog.MinimumLevel;
                    logFilePath = null;
                    return false;
                }

                continue;
            }

            const string timeoutSecondsPrefix = "--timeout-seconds=";
            if (argument.StartsWith(timeoutSecondsPrefix, StringComparison.OrdinalIgnoreCase))
            {
                if (!TryParseExecutionTimeoutSeconds(
                        argument[timeoutSecondsPrefix.Length..],
                        out var timeoutSeconds))
                {
                    ebootPath = string.Empty;
                    runtimeOptions = default;
                    logLevel = SharpEmuLog.MinimumLevel;
                    logFilePath = null;
                    return false;
                }

                executionTimeoutSeconds = timeoutSeconds;
                continue;
            }

            if (argument.StartsWith("--", StringComparison.Ordinal))
            {
                ebootPath = string.Empty;
                runtimeOptions = default;
                logLevel = SharpEmuLog.MinimumLevel;
                logFilePath = null;
                return false;
            }

            pathTokens.Add(argument);
        }

        if (pathTokens.Count == 0)
        {
            ebootPath = string.Empty;
            runtimeOptions = default;
            logLevel = SharpEmuLog.MinimumLevel;
            logFilePath = null;
            return false;
        }

        if (loadOnly && executionTimeoutSeconds is not null)
        {
            ebootPath = string.Empty;
            runtimeOptions = default;
            logLevel = SharpEmuLog.MinimumLevel;
            logFilePath = null;
            return false;
        }

        ebootPath = string.Join(' ', pathTokens);
        runtimeOptions = new SharpEmuRuntimeOptions
        {
            CpuEngine = cpuEngine,
            StrictDynlibResolution = strictDynlibResolution,
            ImportTraceLimit = importTraceLimit,
        };
        return true;
    }

    private static bool TryParseExecutionTimeoutSeconds(string valueText, out int timeoutSeconds)
    {
        return int.TryParse(valueText, out timeoutSeconds) &&
            timeoutSeconds is > 0 and <= MaxExecutionTimeoutSeconds;
    }

    private static bool TryNormalizeSha256(string value, out string? normalized)
    {
        if (value.Length == 64 && value.All(Uri.IsHexDigit))
        {
            normalized = value.ToLowerInvariant();
            return true;
        }

        normalized = null;
        return false;
    }

    private static bool TryParseCpuEngine(string valueText, out CpuExecutionEngine engine)
    {
        if (string.Equals(valueText, "native", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(valueText, "native-only", StringComparison.OrdinalIgnoreCase))
        {
            engine = CpuExecutionEngine.NativeOnly;
            return true;
        }

        engine = CpuExecutionEngine.NativeOnly;
        return false;
    }

    private sealed record CliExecutionReport(
        int SchemaVersion,
        string Mode,
        DateTimeOffset GeneratedAtUtc,
        string ExecutablePath,
        long? ExecutableSizeBytes,
        string? ExecutableSha256,
        CliBundleReport Bundle,
        long DurationMilliseconds,
        CliBuildReport Build,
        CliHostReport Host,
        CliApplicationReport? Application,
        CliImageReport? Image,
        IReadOnlyList<CliModuleReport>? Modules,
        IReadOnlyList<CliModuleInitializerReport>? ModuleInitializers,
        IReadOnlyList<CliSkippedModuleReport>? SkippedModules,
        IReadOnlyList<CliModuleLoadFailureReport>? ModuleLoadFailures,
        CliExecutionResult Result,
        CliCpuSessionReport? CpuSession,
        CliCpuTrapReport? CpuTrap,
        CliCpuMemoryFaultReport? CpuMemoryFault,
        CliCpuNotImplementedReport? CpuNotImplemented,
        string? SessionSummary,
        string? Diagnostics,
        string? ImportTrace,
        string? MilestoneLog,
        string? BasicBlockTrace,
        string? HostError);

    private sealed record CliExecutionResult(string Name, int? Code, bool Succeeded);

    private sealed class BundleFingerprintMismatchException(
        string expectedSha256,
        CliReportFingerprint fingerprint)
        : Exception(
            $"Bundle fingerprint mismatch: expected {expectedSha256}, " +
            $"actual {fingerprint.Bundle.Sha256 ?? "unavailable"}.")
    {
        public CliReportFingerprint Fingerprint { get; } = fingerprint;
    }

    private sealed record CliReportFingerprint(
        long? ExecutableSizeBytes,
        string? ExecutableSha256,
        CliBundleReport Bundle);

    private sealed record CliBundleReport(
        int ManifestVersion,
        string Algorithm,
        string? Sha256,
        IReadOnlyList<CliFileFingerprint> Files);

    private sealed record CliFileFingerprint(string Path, long? SizeBytes, string? Sha256);

    private sealed record CliBuildReport(
        string? CommitSha,
        string? Branch,
        string? Repository,
        string? Configuration,
        bool IsOfficialRelease,
        string? WorkflowRunUrl);

    private sealed record CliHostReport(
        string OsDescription,
        string ProcessArchitecture,
        string FrameworkDescription,
        string CpuName,
        string GpuName);

    private sealed record CliApplicationReport(
        string? Title,
        string? TitleId,
        string? ContentId,
        string? Version);

    private sealed record CliImageReport(
        string Format,
        string Generation,
        ushort ElfType,
        ushort ElfMachine,
        byte ElfAbi,
        byte ElfAbiVersion,
        string EntryPoint,
        string ImageBase,
        string ProcParamAddress,
        int ProgramHeaderCount,
        int MappedRegionCount,
        int ImportStubCount,
        int RuntimeSymbolCount,
        int ImportedRelocationCount,
        IReadOnlyList<uint> UnsupportedRelocationTypes,
        int PreInitializerCount,
        int InitializerCount,
        string? InitFunctionEntryPoint);

    private sealed record CliModuleReport(string Path, CliImageReport Image);

    private sealed record CliModuleInitializerReport(
        string Path,
        int Index,
        string EntryPoint,
        CliExecutionResult Result);

    private sealed record CliSkippedModuleReport(string Path, string Reason);

    private sealed record CliModuleLoadFailureReport(string Path, string ErrorType, string Message);

    private sealed record CliCpuSessionReport(
        CliExecutionResult Result,
        string Reason,
        int? ExitCode,
        string LastGuestRip,
        string LastStubRip,
        int TotalInstructions,
        int ImportsHit,
        int UniqueNidsHit);

    private sealed record CliCpuTrapReport(
        string InstructionPointer,
        string Opcode,
        string? ExceptionCode,
        string? AccessAddress,
        string? AccessKind,
        string? InstructionBytes,
        int? InstructionLength,
        string? InstructionMnemonic,
        string? InstructionText,
        string? InstructionFlowControl,
        CliCpuRegisterReport? Registers,
        string GuestThreadHandle,
        CliCpuCodeWindowReport? CodeWindow,
        CliCpuMemoryWindowReport? StackWindow,
        IReadOnlyList<CliCpuStackCodeCandidateReport>? StackCodeCandidates,
        CliCodeLocationReport? Location,
        IReadOnlyList<CliCpuStackFrameReport>? StackFrames);

    private sealed record CliCpuStackFrameReport(
        string FramePointer,
        string NextFramePointer,
        string ReturnAddress,
        CliCodeLocationReport? ReturnLocation,
        CliCpuCodeWindowReport? ReturnCodeWindow);

    private sealed record CliCodeLocationReport(
        string ImagePath,
        string ImageOffset);

    private sealed record CliCpuCodeWindowReport(
        string StartAddress,
        int InstructionOffset,
        string Bytes);

    private sealed record CliCpuMemoryWindowReport(
        string StartAddress,
        string Bytes,
        int ReferenceOffset);

    private sealed record CliCpuStackCodeCandidateReport(
        int StackOffset,
        string Address,
        CliCodeLocationReport? Location,
        CliCpuCodeWindowReport? CodeWindow,
        IReadOnlyList<CliCpuDecodedInstructionReport>? Instructions);

    private sealed record CliCpuDecodedInstructionReport(
        string Address,
        int Length,
        string Bytes,
        string Mnemonic,
        string Text,
        string FlowControl,
        string? NearBranchTarget,
        string? MemoryAddress);

    private sealed record CliCpuRegisterReport(
        string Rax,
        string Rbx,
        string Rcx,
        string Rdx,
        string Rsi,
        string Rdi,
        string Rbp,
        string Rsp,
        string R8,
        string R9,
        string R10,
        string R11,
        string R12,
        string R13,
        string R14,
        string R15);

    private sealed record CliCpuMemoryFaultReport(
        string InstructionPointer,
        string? Opcode,
        CliCpuMemoryAccessReport Access);

    private sealed record CliCpuMemoryAccessReport(string Address, int Size, string Kind);

    private sealed record CliCpuNotImplementedReport(
        string Source,
        string InstructionPointer,
        string? Nid,
        string? ExportName,
        string? LibraryName,
        string? Detail);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct STARTUPINFO
    {
        public int cb;
        public nint lpReserved;
        public nint lpDesktop;
        public nint lpTitle;
        public int dwX;
        public int dwY;
        public int dwXSize;
        public int dwYSize;
        public int dwXCountChars;
        public int dwYCountChars;
        public int dwFillAttribute;
        public int dwFlags;
        public short wShowWindow;
        public short cbReserved2;
        public nint lpReserved2;
        public nint hStdInput;
        public nint hStdOutput;
        public nint hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct STARTUPINFOEX
    {
        public STARTUPINFO StartupInfo;
        public nint lpAttributeList;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESS_INFORMATION
    {
        public nint hProcess;
        public nint hThread;
        public int dwProcessId;
        public int dwThreadId;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_BASIC_LIMIT_INFORMATION
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public nuint MinimumWorkingSetSize;
        public nuint MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public nint Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IO_COUNTERS
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
    {
        public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
        public IO_COUNTERS IoInfo;
        public nuint ProcessMemoryLimit;
        public nuint JobMemoryLimit;
        public nuint PeakProcessMemoryUsed;
        public nuint PeakJobMemoryUsed;
    }

    private sealed class TeeTextWriter : TextWriter
    {
        private readonly TextWriter _primary;
        private readonly TextWriter _mirror;

        public TeeTextWriter(TextWriter primary, TextWriter mirror)
        {
            _primary = primary;
            _mirror = mirror;
        }

        public override Encoding Encoding => _primary.Encoding;

        public override void Write(char value)
        {
            lock (ConsoleMirrorSync)
            {
                _primary.Write(value);
                _mirror.Write(value);
            }
        }

        public override void Write(string? value)
        {
            lock (ConsoleMirrorSync)
            {
                _primary.Write(value);
                _mirror.Write(value);
            }
        }

        public override void WriteLine(string? value)
        {
            lock (ConsoleMirrorSync)
            {
                _primary.WriteLine(value);
                _mirror.WriteLine(value);
            }
        }

        public override void Flush()
        {
            lock (ConsoleMirrorSync)
            {
                _primary.Flush();
                _mirror.Flush();
            }
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool InitializeProcThreadAttributeList(
        nint lpAttributeList,
        int dwAttributeCount,
        int dwFlags,
        ref nuint lpSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UpdateProcThreadAttribute(
        nint lpAttributeList,
        uint dwFlags,
        nint attribute,
        nint lpValue,
        nuint cbSize,
        nint lpPreviousValue,
        nint lpReturnSize);

    [DllImport("kernel32.dll")]
    private static extern void DeleteProcThreadAttributeList(nint lpAttributeList);

    [DllImport("kernel32.dll", EntryPoint = "CreateJobObjectW", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern nint CreateJobObjectW(nint lpJobAttributes, string? lpName);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetInformationJobObject(
        nint hJob,
        int jobObjectInfoClass,
        nint lpJobObjectInfo,
        uint cbJobObjectInfoLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AssignProcessToJobObject(nint hJob, nint hProcess);

    [DllImport("kernel32.dll", EntryPoint = "CreateProcessW", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateProcessW(
        string? applicationName,
        StringBuilder commandLine,
        nint processAttributes,
        nint threadAttributes,
        [MarshalAs(UnmanagedType.Bool)] bool inheritHandles,
        uint creationFlags,
        nint environment,
        string currentDirectory,
        ref STARTUPINFOEX startupInfo,
        out PROCESS_INFORMATION processInformation);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint WaitForSingleObject(nint handle, uint milliseconds);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetExitCodeProcess(nint process, out uint exitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool TerminateProcess(nint process, uint exitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(nint handle);

    [DllImport("kernel32.dll")]
    private static extern nint GetConsoleWindow();

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AttachConsole(int processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AllocConsole();

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nint GetStdHandle(int stdHandle);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetStdHandle(int stdHandle, nint handle);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetHandleInformation(nint handle, uint mask, uint flags);

    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern nint CreateFileW(
        string fileName,
        uint desiredAccess,
        uint shareMode,
        nint securityAttributes,
        uint creationDisposition,
        uint flagsAndAttributes,
        nint templateFile);
}
