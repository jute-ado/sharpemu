// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Text.Json;
using SharpEmu.Core.Runtime;
using SharpEmu.HLE;
using Xunit;

namespace SharpEmu.Core.Tests;

public sealed class SharpEmuRuntimeTests
{
    private const string SyntheticParamJson = """
        {
          "titleId": "PPSA00001",
          "contentId": "EP0001-PPSA00001_00-SHARPEMUTEST0001",
          "contentVersion": "01.20",
          "localizedParameters": {
            "defaultLanguage": "en-US",
            "en-US": { "titleName": "Synthetic title" }
          }
        }
        """;

    [Fact]
    public void InspectionRuntimeRejectsGuestExecution()
    {
        using var runtime = SharpEmuRuntime.CreateForInspection();

        var exception = Assert.Throws<InvalidOperationException>(() => runtime.Run("eboot.bin"));

        Assert.Contains("image inspection", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void InspectionRuntimeRepreparesWithoutLeakingModuleState()
    {
        var testDirectory = Path.Combine(
            Path.GetTempPath(),
            "sharpemu-runtime-tests",
            Guid.NewGuid().ToString("N"));
        var moduleDirectory = Path.Combine(testDirectory, "sce_module");
        Directory.CreateDirectory(moduleDirectory);
        var executablePath = Path.Combine(testDirectory, "eboot.bin");
        var modulePath = Path.Combine(moduleDirectory, "synthetic.prx");

        try
        {
            File.WriteAllBytes(executablePath, SyntheticElfImage.CreateExecutable([0xC3]));
            File.WriteAllBytes(modulePath, SyntheticElfImage.CreateExecutable([0xC3]));
            using var runtime = SharpEmuRuntime.CreateForInspection();

            var first = runtime.PrepareApplication(executablePath);
            var firstModule = Assert.Single(first.Modules);
            Assert.Empty(first.ModuleLoadFailures);
            Assert.NotEqual(first.MainImage.ImageBase, firstModule.Image.ImageBase);

            File.WriteAllBytes(modulePath, [0x01, 0x02, 0x03]);
            var second = runtime.PrepareApplication(executablePath);
            Assert.Empty(second.Modules);
            Assert.Single(second.ModuleLoadFailures);
            Assert.Equal(first.MainImage.ImageBase, second.MainImage.ImageBase);
            Assert.Single(first.Modules);
            Assert.Empty(first.ModuleLoadFailures);

            File.WriteAllBytes(modulePath, SyntheticElfImage.CreateExecutable([0xF4]));
            var third = runtime.PrepareApplication(executablePath);
            var thirdModule = Assert.Single(third.Modules);
            Assert.Empty(third.ModuleLoadFailures);
            Assert.Equal(first.MainImage.ImageBase, third.MainImage.ImageBase);
            Assert.Equal(firstModule.Image.ImageBase, thirdModule.Image.ImageBase);
            Assert.Same(third, runtime.LastPreparedApplication);
            Assert.Same(third.MainImage, runtime.LastLoadedImage);
        }
        finally
        {
            Directory.Delete(testDirectory, recursive: true);
        }
    }

    [WindowsX64Fact]
    public void DefaultRuntimeBootsContentFreeSyntheticExecutable()
    {
        var execution = RunSyntheticExecutable(
        [
            0x31, 0xC0, // xor eax, eax
            0xC3,       // ret
        ]);

        Assert.Equal(OrbisGen2Result.ORBIS_GEN2_OK, execution.Result);
        Assert.Null(execution.Diagnostics);
        Assert.Contains(
            "reason=ReturnedToHost",
            execution.SessionSummary,
            StringComparison.Ordinal);
    }

    [WindowsX64Fact]
    public void RuntimeStillRejectsNonZeroProcessEntryReturnValue()
    {
        var execution = RunSyntheticExecutable(
        [
            0xB8, 0x01, 0x00, 0x00, 0x00, // mov eax, 1
            0xC3,                         // ret
        ]);

        Assert.Equal(OrbisGen2Result.ORBIS_GEN2_ERROR_CPU_TRAP, execution.Result);
        Assert.Contains("reason=CpuTrap", execution.SessionSummary, StringComparison.Ordinal);
    }

    [WindowsX64Fact]
    public void RuntimeClearsModuleInitializerTraceBeforeNextPreparation()
    {
        var testDirectory = Path.Combine(
            Path.GetTempPath(),
            "sharpemu-runtime-tests",
            Guid.NewGuid().ToString("N"));
        var moduleDirectory = Path.Combine(testDirectory, "sce_module");
        Directory.CreateDirectory(moduleDirectory);
        var executablePath = Path.Combine(testDirectory, "eboot.bin");
        File.WriteAllBytes(
            executablePath,
            SyntheticElfImage.CreateExecutable([0x31, 0xC0, 0xC3]));
        File.WriteAllBytes(
            Path.Combine(moduleDirectory, "synthetic.prx"),
            SyntheticElfImage.CreateModuleWithInitializer([0xC3]));

        try
        {
            using var runtime = SharpEmuRuntime.CreateDefault();

            Assert.Equal(OrbisGen2Result.ORBIS_GEN2_OK, runtime.Run(executablePath));
            var execution = Assert.Single(runtime.LastModuleInitializerExecutions);
            Assert.Equal(0, execution.InitializerIndex);
            Assert.Equal(OrbisGen2Result.ORBIS_GEN2_OK, execution.Result);

            runtime.PrepareApplication(executablePath);

            Assert.Empty(runtime.LastModuleInitializerExecutions);
        }
        finally
        {
            Directory.Delete(testDirectory, recursive: true);
        }
    }

    [WindowsX64Fact]
    public async Task RuntimeExecutesAdjacentModuleInitializerBeforeMainEntry()
    {
        var execution = await RunSyntheticExecutableInCliAsync(
            [0xC3], // main entry would return successfully if reached
            requestReport: true,
            adjacentModuleImage: SyntheticElfImage.CreateModuleWithInitializer(
                [0x0F, 0x0B])); // ud2

        Assert.Equal(4, execution.ExitCode);
        Assert.NotNull(execution.ReportJson);
        using var document = JsonDocument.Parse(execution.ReportJson);
        var root = document.RootElement;
        Assert.Equal(
            "ORBIS_GEN2_ERROR_CPU_TRAP",
            root.GetProperty("result").GetProperty("name").GetString());
        Assert.Equal("CpuTrap", root.GetProperty("cpuSession").GetProperty("reason").GetString());
        Assert.Equal("0x0F", root.GetProperty("cpuTrap").GetProperty("opcode").GetString());
        var module = Assert.Single(root.GetProperty("modules").EnumerateArray());
        Assert.Equal("sce_module/synthetic.prx", module.GetProperty("path").GetString());
        Assert.StartsWith(
            "0x",
            module.GetProperty("image").GetProperty("initFunctionEntryPoint").GetString(),
            StringComparison.Ordinal);
        Assert.Contains("Starting module synthetic.prx", execution.StandardOutput, StringComparison.Ordinal);
        Assert.Contains(
            "Initializer dispatch failed",
            execution.StandardOutput + execution.StandardError,
            StringComparison.Ordinal);
    }

    [WindowsX64Fact]
    public async Task RuntimeContinuesToMainEntryAfterModuleInitializerReturns()
    {
        var execution = await RunSyntheticExecutableInCliAsync(
            [0x0F, 0x0B], // ud2 in the main image
            requestReport: true,
            adjacentModuleImage: SyntheticElfImage.CreateModuleWithInitializer(
                [0xC3])); // ret

        Assert.Equal(4, execution.ExitCode);
        Assert.NotNull(execution.ReportJson);
        using var document = JsonDocument.Parse(execution.ReportJson);
        var root = document.RootElement;
        var mainEntryPoint = root.GetProperty("image").GetProperty("entryPoint").GetString();
        var trapInstructionPointer = root.GetProperty("cpuTrap").GetProperty("instructionPointer").GetString();
        Assert.True(
            string.Equals(mainEntryPoint, trapInstructionPointer, StringComparison.Ordinal),
            $"Expected trap at main entry {mainEntryPoint}, got {trapInstructionPointer}. " +
            $"Report: {execution.ReportJson} Output: {execution.StandardOutput} Error: {execution.StandardError}");
        Assert.Equal("0x0F", root.GetProperty("cpuTrap").GetProperty("opcode").GetString());
        Assert.Equal("CpuTrap", root.GetProperty("cpuSession").GetProperty("reason").GetString());
        Assert.Single(root.GetProperty("modules").EnumerateArray());
        Assert.Contains("Starting module synthetic.prx", execution.StandardOutput, StringComparison.Ordinal);
        Assert.DoesNotContain(
            "Initializer dispatch failed",
            execution.StandardOutput + execution.StandardError,
            StringComparison.Ordinal);
    }

    [WindowsX64Fact]
    public async Task RuntimeExecutesModuleInitializerArrayBeforeMainEntry()
    {
        var execution = await RunSyntheticExecutableInCliAsync(
            [0xC3], // main entry returns successfully
            requestReport: true,
            adjacentModuleImage: SyntheticElfImage.CreateModuleWithInitializerArray(
                [0xC3],       // DT_INIT returns successfully
                [0x0F, 0x0B])); // DT_INIT_ARRAY executes ud2

        Assert.Equal(4, execution.ExitCode);
        Assert.NotNull(execution.ReportJson);
        using var document = JsonDocument.Parse(execution.ReportJson);
        var root = document.RootElement;
        Assert.Equal(
            "ORBIS_GEN2_ERROR_CPU_TRAP",
            root.GetProperty("result").GetProperty("name").GetString());
        Assert.Equal("CpuTrap", root.GetProperty("cpuSession").GetProperty("reason").GetString());
        Assert.Equal("0x0F", root.GetProperty("cpuTrap").GetProperty("opcode").GetString());
        var initializerExecutions = root.GetProperty("moduleInitializers").EnumerateArray().ToArray();
        Assert.Equal(2, initializerExecutions.Length);
        Assert.Equal("sce_module/synthetic.prx", initializerExecutions[0].GetProperty("path").GetString());
        Assert.Equal(0, initializerExecutions[0].GetProperty("index").GetInt32());
        Assert.Equal(
            "ORBIS_GEN2_OK",
            initializerExecutions[0].GetProperty("result").GetProperty("name").GetString());
        Assert.Equal(1, initializerExecutions[1].GetProperty("index").GetInt32());
        Assert.NotEqual(
            initializerExecutions[0].GetProperty("entryPoint").GetString(),
            initializerExecutions[1].GetProperty("entryPoint").GetString());
        Assert.Equal(
            "ORBIS_GEN2_ERROR_CPU_TRAP",
            initializerExecutions[1].GetProperty("result").GetProperty("name").GetString());
        Assert.Contains(
            "Initializer dispatch failed",
            execution.StandardOutput + execution.StandardError,
            StringComparison.Ordinal);
    }

    [WindowsX64Fact]
    public Task CliReportsSyntheticIllegalInstructionWithoutCrashingHostProcess()
    {
        return AssertSyntheticGuestTrapAsync(
            [0x0F, 0x0B], // ud2
            exceptionCode: "C000001D",
            opcode: "0F",
            diagnostic: "trap=ud2");
    }

    [WindowsX64Fact]
    public Task CliReportsSyntheticAccessViolationWithoutCrashingHostProcess()
    {
        return AssertSyntheticGuestTrapAsync(
            [
                0x48, 0x31, 0xC0, // xor rax, rax
                0x48, 0x8B, 0x00, // mov rax, [rax]
            ],
            exceptionCode: "C0000005",
            opcode: "48",
            access: "read@0x0000000000000000");
    }

    [WindowsX64Fact]
    public Task CliReportsSyntheticWriteAccessViolationWithoutCrashingHostProcess()
    {
        return AssertSyntheticGuestTrapAsync(
            [
                0x31, 0xC0,       // xor eax, eax
                0xC6, 0x00, 0x01, // mov byte ptr [rax], 1
            ],
            exceptionCode: "C0000005",
            opcode: "C6",
            access: "write@0x0000000000000000");
    }

    [WindowsX64Fact]
    public Task CliReportsSyntheticExecuteAccessViolationAtZeroWithoutCrashingHostProcess()
    {
        return AssertSyntheticGuestTrapAsync(
            [
                0x31, 0xC0, // xor eax, eax
                0xFF, 0xD0, // call rax
            ],
            exceptionCode: "C0000005",
            opcode: "00",
            access: "execute@0x0000000000000000");
    }

    [WindowsX64Fact]
    public Task CliReportsSyntheticBreakpointWithoutCrashingHostProcess()
    {
        return AssertSyntheticGuestTrapAsync(
            [0xCC], // int3
            exceptionCode: "80000003",
            opcode: "CC");
    }

    [WindowsX64Fact]
    public Task CliReportsSyntheticIntegerDivideByZeroWithoutCrashingHostProcess()
    {
        return AssertSyntheticGuestTrapAsync(
            [
                0x31, 0xD2,                         // xor edx, edx
                0xB8, 0x01, 0x00, 0x00, 0x00,       // mov eax, 1
                0x31, 0xC9,                         // xor ecx, ecx
                0xF7, 0xF1,                         // div ecx
            ],
            exceptionCode: "C0000094",
            opcode: "F7");
    }

    [WindowsX64Fact]
    public Task CliReportsSyntheticPrivilegedInstructionWithoutCrashingHostProcess()
    {
        return AssertSyntheticGuestTrapAsync(
            [0xF4], // hlt
            exceptionCode: "C0000096",
            opcode: "F4");
    }

    [WindowsX64Fact]
    public async Task CliWritesVersionedJsonExecutionReport()
    {
        var execution = await RunSyntheticExecutableInCliAsync(
            [
                0x31, 0xC0, // xor eax, eax
                0xC3,       // ret
            ],
            requestReport: true,
            executionTimeoutSeconds: 5,
            paramJson: SyntheticParamJson);

        Assert.Equal(0, execution.ExitCode);
        Assert.NotNull(execution.ReportJson);
        using var document = JsonDocument.Parse(execution.ReportJson);
        var root = document.RootElement;
        Assert.Equal(3, root.GetProperty("schemaVersion").GetInt32());
        Assert.Equal("execution", root.GetProperty("mode").GetString());
        Assert.Equal("ORBIS_GEN2_OK", root.GetProperty("result").GetProperty("name").GetString());
        Assert.Equal(0, root.GetProperty("result").GetProperty("code").GetInt32());
        Assert.True(root.GetProperty("result").GetProperty("succeeded").GetBoolean());
        Assert.Contains(
            "reason=ReturnedToHost",
            root.GetProperty("sessionSummary").GetString(),
            StringComparison.Ordinal);
        Assert.Equal(JsonValueKind.Null, root.GetProperty("diagnostics").ValueKind);
        var cpuSession = root.GetProperty("cpuSession");
        Assert.Equal("ORBIS_GEN2_OK", cpuSession.GetProperty("result").GetProperty("name").GetString());
        Assert.Equal("ReturnedToHost", cpuSession.GetProperty("reason").GetString());
        Assert.StartsWith("0x", cpuSession.GetProperty("lastGuestRip").GetString(), StringComparison.Ordinal);
        Assert.Equal(0, cpuSession.GetProperty("importsHit").GetInt32());
        Assert.Equal(JsonValueKind.Null, root.GetProperty("cpuTrap").ValueKind);
        Assert.True(root.GetProperty("durationMilliseconds").GetInt64() >= 0);
        Assert.True(root.GetProperty("executableSizeBytes").GetInt64() > 0);
        Assert.Equal(execution.ExecutableSha256, root.GetProperty("executableSha256").GetString());
        var bundle = root.GetProperty("bundle");
        Assert.Equal(1, bundle.GetProperty("manifestVersion").GetInt32());
        Assert.Equal("SHA-256", bundle.GetProperty("algorithm").GetString());
        Assert.Equal(64, bundle.GetProperty("sha256").GetString()!.Length);
        var bundleFiles = bundle.GetProperty("files").EnumerateArray().ToArray();
        Assert.Equal(2, bundleFiles.Length);
        Assert.Equal("eboot.bin", bundleFiles[0].GetProperty("path").GetString());
        Assert.Equal(execution.ExecutableSha256, bundleFiles[0].GetProperty("sha256").GetString());
        Assert.Equal("sce_sys/param.json", bundleFiles[1].GetProperty("path").GetString());
        Assert.Equal("Release", root.GetProperty("build").GetProperty("configuration").GetString());
        Assert.False(root.GetProperty("build").GetProperty("isOfficialRelease").GetBoolean());
        Assert.False(string.IsNullOrWhiteSpace(root.GetProperty("host").GetProperty("osDescription").GetString()));
        Assert.False(string.IsNullOrWhiteSpace(root.GetProperty("host").GetProperty("processArchitecture").GetString()));
        var application = root.GetProperty("application");
        Assert.Equal("Synthetic title", application.GetProperty("title").GetString());
        Assert.Equal("PPSA00001", application.GetProperty("titleId").GetString());
        Assert.Equal("EP0001-PPSA00001_00-SHARPEMUTEST0001", application.GetProperty("contentId").GetString());
        Assert.Equal("01.20", application.GetProperty("version").GetString());
        var image = root.GetProperty("image");
        Assert.Equal("ELF", image.GetProperty("format").GetString());
        Assert.Equal("Gen5", image.GetProperty("generation").GetString());
        Assert.Equal("0x0000000800000000", image.GetProperty("entryPoint").GetString());
        Assert.Equal(1, image.GetProperty("programHeaderCount").GetInt32());
        Assert.Equal(1, image.GetProperty("mappedRegionCount").GetInt32());
        Assert.Empty(root.GetProperty("modules").EnumerateArray());
        Assert.Empty(root.GetProperty("moduleInitializers").EnumerateArray());
        Assert.Empty(root.GetProperty("skippedModules").EnumerateArray());
        Assert.Empty(root.GetProperty("moduleLoadFailures").EnumerateArray());
    }

    [Fact]
    public async Task CliLoadsAndReportsImageWithoutExecutingGuestCode()
    {
        var execution = await RunSyntheticExecutableInCliAsync(
            [0xF4], // hlt: load-only must never dispatch this privileged instruction
            requestReport: true,
            paramJson: SyntheticParamJson,
            loadOnly: true,
            adjacentModuleCode: [0xF4],
            writeSkippedCoreModule: true);
        Assert.Equal(0, execution.ExitCode);
        Assert.NotNull(execution.ReportJson);
        using var document = JsonDocument.Parse(execution.ReportJson);
        var root = document.RootElement;
        Assert.Equal(3, root.GetProperty("schemaVersion").GetInt32());
        Assert.Equal("load-only", root.GetProperty("mode").GetString());
        Assert.Equal("IMAGE_LOADED", root.GetProperty("result").GetProperty("name").GetString());
        Assert.Equal(0, root.GetProperty("result").GetProperty("code").GetInt32());
        Assert.True(root.GetProperty("result").GetProperty("succeeded").GetBoolean());
        Assert.Equal(JsonValueKind.Null, root.GetProperty("cpuSession").ValueKind);
        Assert.Equal(JsonValueKind.Null, root.GetProperty("cpuTrap").ValueKind);
        Assert.Equal(JsonValueKind.Null, root.GetProperty("hostError").ValueKind);

        var image = root.GetProperty("image");
        Assert.Equal("ELF", image.GetProperty("format").GetString());
        Assert.Equal("Gen5", image.GetProperty("generation").GetString());
        Assert.Equal(2, image.GetProperty("elfType").GetInt32());
        Assert.Equal(0x3E, image.GetProperty("elfMachine").GetInt32());
        Assert.Equal("0x0000000800000000", image.GetProperty("entryPoint").GetString());
        Assert.Equal("0x0000000800000000", image.GetProperty("imageBase").GetString());
        Assert.Equal(1, image.GetProperty("programHeaderCount").GetInt32());
        Assert.Equal(1, image.GetProperty("mappedRegionCount").GetInt32());
        Assert.Equal(0, image.GetProperty("importStubCount").GetInt32());
        Assert.Equal("PPSA00001", root.GetProperty("application").GetProperty("titleId").GetString());
        var module = Assert.Single(root.GetProperty("modules").EnumerateArray());
        Assert.Equal("sce_module/synthetic.prx", module.GetProperty("path").GetString());
        Assert.Equal("ELF", module.GetProperty("image").GetProperty("format").GetString());
        Assert.Empty(root.GetProperty("moduleInitializers").EnumerateArray());
        var skippedModule = Assert.Single(root.GetProperty("skippedModules").EnumerateArray());
        Assert.Equal("sce_module/libkernel.prx", skippedModule.GetProperty("path").GetString());
        Assert.Contains("HLE", skippedModule.GetProperty("reason").GetString(), StringComparison.Ordinal);
        Assert.Empty(root.GetProperty("moduleLoadFailures").EnumerateArray());
        var expectedBundleSha256 = root.GetProperty("bundle").GetProperty("sha256").GetString();
        Assert.NotNull(expectedBundleSha256);
        var repeatedExecution = await RunSyntheticExecutableInCliAsync(
            [0xF4],
            requestReport: true,
            paramJson: SyntheticParamJson,
            loadOnly: true,
            adjacentModuleCode: [0xF4],
            writeSkippedCoreModule: true,
            expectedBundleSha256: expectedBundleSha256);
        Assert.Equal(0, repeatedExecution.ExitCode);
        Assert.NotNull(repeatedExecution.ReportJson);
        using var repeatedDocument = JsonDocument.Parse(repeatedExecution.ReportJson);
        var bundle = root.GetProperty("bundle");
        Assert.Equal(
            bundle.GetProperty("sha256").GetString(),
            repeatedDocument.RootElement.GetProperty("bundle").GetProperty("sha256").GetString());
        var bundleFiles = bundle.GetProperty("files").EnumerateArray().ToArray();
        Assert.Equal(4, bundleFiles.Length);
        Assert.Equal(
            new[]
            {
                "eboot.bin",
                "sce_module/libkernel.prx",
                "sce_module/synthetic.prx",
                "sce_sys/param.json",
            },
            bundleFiles.Select(file => file.GetProperty("path").GetString()).ToArray());
        Assert.All(bundleFiles, file =>
        {
            Assert.True(file.GetProperty("sizeBytes").GetInt64() > 0);
            Assert.Equal(64, file.GetProperty("sha256").GetString()!.Length);
        });
        Assert.DoesNotContain("Guest hardware exception", execution.StandardOutput, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CliRejectsUnexpectedBundleFingerprintWithoutExecutingGuestCode()
    {
        const string unexpectedSha256 =
            "0000000000000000000000000000000000000000000000000000000000000000";
        var execution = await RunSyntheticExecutableInCliAsync(
            [0xF4],
            requestReport: true,
            loadOnly: true,
            expectedBundleSha256: unexpectedSha256);

        Assert.Equal(8, execution.ExitCode);
        Assert.NotNull(execution.ReportJson);
        using var document = JsonDocument.Parse(execution.ReportJson);
        var root = document.RootElement;
        Assert.Equal(
            "BUNDLE_FINGERPRINT_MISMATCH",
            root.GetProperty("result").GetProperty("name").GetString());
        Assert.False(root.GetProperty("result").GetProperty("succeeded").GetBoolean());
        Assert.NotEqual(unexpectedSha256, root.GetProperty("bundle").GetProperty("sha256").GetString());
        Assert.Contains("fingerprint mismatch", root.GetProperty("hostError").GetString(), StringComparison.OrdinalIgnoreCase);
        Assert.Equal(JsonValueKind.Null, root.GetProperty("cpuSession").ValueKind);
        Assert.DoesNotContain("Guest hardware exception", execution.StandardOutput, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("not-a-sha256", true)]
    [InlineData("0000000000000000000000000000000000000000000000000000000000000000", false)]
    public async Task CliRejectsInvalidBundleFingerprintAssertionUsage(string expectedSha256, bool loadOnly)
    {
        var execution = await RunSyntheticExecutableInCliAsync(
            [0xF4],
            loadOnly: loadOnly,
            expectedBundleSha256: expectedSha256);

        Assert.Equal(1, execution.ExitCode);
        Assert.Contains("Usage: SharpEmu.CLI", execution.StandardOutput, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CliReportsAdjacentModuleLoadFailuresWithoutExecutingGuestCode()
    {
        var execution = await RunSyntheticExecutableInCliAsync(
            [0xF4],
            requestReport: true,
            loadOnly: true,
            writeInvalidAdjacentModule: true);

        Assert.Equal(0, execution.ExitCode);
        Assert.NotNull(execution.ReportJson);
        using var document = JsonDocument.Parse(execution.ReportJson);
        var root = document.RootElement;
        Assert.Empty(root.GetProperty("modules").EnumerateArray());
        var failure = Assert.Single(root.GetProperty("moduleLoadFailures").EnumerateArray());
        Assert.Equal("sce_module/broken.sprx", failure.GetProperty("path").GetString());
        Assert.Equal("InvalidDataException", failure.GetProperty("errorType").GetString());
        Assert.False(string.IsNullOrWhiteSpace(failure.GetProperty("message").GetString()));
        Assert.Equal(JsonValueKind.Null, root.GetProperty("cpuSession").ValueKind);
        Assert.Contains("module load failure", execution.StandardOutput, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CliRejectsExecutionTimeoutInLoadOnlyMode()
    {
        var execution = await RunSyntheticExecutableInCliAsync(
            [0xF4],
            executionTimeoutSeconds: 1,
            loadOnly: true);

        Assert.Equal(1, execution.ExitCode);
        Assert.Contains("Usage: SharpEmu.CLI", execution.StandardOutput, StringComparison.Ordinal);
    }

    [WindowsX64Fact]
    public async Task CliWritesGuestTrapDetailsToJsonExecutionReport()
    {
        var execution = await RunSyntheticExecutableInCliAsync(
            [
                0x48, 0x31, 0xC0, // xor rax, rax
                0x48, 0x8B, 0x00, // mov rax, [rax]
            ],
            requestReport: true);

        Assert.Equal(4, execution.ExitCode);
        Assert.NotNull(execution.ReportJson);
        using var document = JsonDocument.Parse(execution.ReportJson);
        var root = document.RootElement;
        Assert.Equal("ORBIS_GEN2_ERROR_CPU_TRAP", root.GetProperty("result").GetProperty("name").GetString());
        Assert.False(root.GetProperty("result").GetProperty("succeeded").GetBoolean());
        Assert.Contains(
            "exception=0xC0000005",
            root.GetProperty("diagnostics").GetString(),
            StringComparison.Ordinal);
        Assert.Contains(
            "reason=CpuTrap",
            root.GetProperty("sessionSummary").GetString(),
            StringComparison.Ordinal);
        Assert.Equal(JsonValueKind.Null, root.GetProperty("hostError").ValueKind);
        Assert.Equal("CpuTrap", root.GetProperty("cpuSession").GetProperty("reason").GetString());
        var cpuTrap = root.GetProperty("cpuTrap");
        Assert.Equal("0x48", cpuTrap.GetProperty("opcode").GetString());
        Assert.Equal("0xC0000005", cpuTrap.GetProperty("exceptionCode").GetString());
        Assert.StartsWith("0x", cpuTrap.GetProperty("instructionPointer").GetString(), StringComparison.Ordinal);
        Assert.Equal("0x0000000000000000", cpuTrap.GetProperty("accessAddress").GetString());
        Assert.Equal("read", cpuTrap.GetProperty("accessKind").GetString());
        Assert.Equal(JsonValueKind.Null, root.GetProperty("cpuMemoryFault").ValueKind);
        Assert.Equal(JsonValueKind.Null, root.GetProperty("cpuNotImplemented").ValueKind);
    }

    [Fact]
    public async Task CliWritesHostFailureToJsonExecutionReport()
    {
        var execution = await RunSyntheticExecutableInCliAsync(
            [],
            requestReport: true,
            writeExecutable: false);

        Assert.Equal(2, execution.ExitCode);
        Assert.NotNull(execution.ReportJson);
        using var document = JsonDocument.Parse(execution.ReportJson);
        var root = document.RootElement;
        Assert.Equal("HOST_ERROR", root.GetProperty("result").GetProperty("name").GetString());
        Assert.Equal(JsonValueKind.Null, root.GetProperty("result").GetProperty("code").ValueKind);
        Assert.False(root.GetProperty("result").GetProperty("succeeded").GetBoolean());
        Assert.Contains(
            "EBOOT file was not found",
            root.GetProperty("hostError").GetString(),
            StringComparison.Ordinal);
        Assert.Equal(JsonValueKind.Null, root.GetProperty("sessionSummary").ValueKind);
        Assert.Equal(JsonValueKind.Null, root.GetProperty("cpuSession").ValueKind);
        Assert.Equal(JsonValueKind.Null, root.GetProperty("cpuTrap").ValueKind);
        Assert.Equal(JsonValueKind.Null, root.GetProperty("cpuMemoryFault").ValueKind);
        Assert.Equal(JsonValueKind.Null, root.GetProperty("cpuNotImplemented").ValueKind);
        Assert.Equal(JsonValueKind.Null, root.GetProperty("executableSizeBytes").ValueKind);
        Assert.Equal(JsonValueKind.Null, root.GetProperty("executableSha256").ValueKind);
        Assert.Equal(JsonValueKind.Null, root.GetProperty("bundle").GetProperty("sha256").ValueKind);
        var missingFile = Assert.Single(root.GetProperty("bundle").GetProperty("files").EnumerateArray());
        Assert.Equal("eboot.bin", missingFile.GetProperty("path").GetString());
        Assert.Equal(JsonValueKind.Null, missingFile.GetProperty("sizeBytes").ValueKind);
        Assert.Equal(JsonValueKind.Null, missingFile.GetProperty("sha256").ValueKind);
        Assert.Equal(JsonValueKind.Null, root.GetProperty("application").ValueKind);
        Assert.Equal(JsonValueKind.Null, root.GetProperty("modules").ValueKind);
        Assert.Equal(JsonValueKind.Null, root.GetProperty("skippedModules").ValueKind);
        Assert.Equal(JsonValueKind.Null, root.GetProperty("moduleLoadFailures").ValueKind);
    }

    [WindowsX64Fact]
    public async Task CliWritesJsonReportWhenStallWatchdogTerminatesGuest()
    {
        var execution = await RunSyntheticExecutableInCliAsync(
            [0xEB, 0xFE], // jmp $
            requestReport: true,
            stallWatchdogSeconds: 1);

        Assert.Equal(6, execution.ExitCode);
        Assert.NotNull(execution.ReportJson);
        using var document = JsonDocument.Parse(execution.ReportJson);
        var root = document.RootElement;
        Assert.Equal("EXECUTION_STALLED", root.GetProperty("result").GetProperty("name").GetString());
        Assert.Equal(JsonValueKind.Null, root.GetProperty("result").GetProperty("code").ValueKind);
        Assert.False(root.GetProperty("result").GetProperty("succeeded").GetBoolean());
        Assert.Contains(
            "stalled",
            root.GetProperty("hostError").GetString(),
            StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Execution stalled", execution.StandardError, StringComparison.Ordinal);
    }

    [WindowsX64Fact]
    public async Task CliEnforcesExecutionTimeoutAndWritesJsonReport()
    {
        var execution = await RunSyntheticExecutableInCliAsync(
            [0xEB, 0xFE], // jmp $
            requestReport: true,
            executionTimeoutSeconds: 1,
            paramJson: SyntheticParamJson);

        Assert.Equal(7, execution.ExitCode);
        Assert.NotNull(execution.ReportJson);
        using var document = JsonDocument.Parse(execution.ReportJson);
        var root = document.RootElement;
        Assert.Equal("EXECUTION_TIMED_OUT", root.GetProperty("result").GetProperty("name").GetString());
        Assert.Equal(JsonValueKind.Null, root.GetProperty("result").GetProperty("code").ValueKind);
        Assert.False(root.GetProperty("result").GetProperty("succeeded").GetBoolean());
        Assert.Contains(
            "1 second",
            root.GetProperty("hostError").GetString(),
            StringComparison.OrdinalIgnoreCase);
        Assert.Contains("timed out after 1 second", execution.StandardError, StringComparison.OrdinalIgnoreCase);
        Assert.InRange(root.GetProperty("durationMilliseconds").GetInt64(), 900, 10_000);
        Assert.Equal(execution.ExecutableSha256, root.GetProperty("executableSha256").GetString());
        Assert.Equal("PPSA00001", root.GetProperty("application").GetProperty("titleId").GetString());
    }

    [Fact]
    public async Task CliRejectsZeroExecutionTimeout()
    {
        var execution = await RunSyntheticExecutableInCliAsync(
            [0xC3], // ret (must not be reached)
            executionTimeoutSeconds: 0);

        Assert.Equal(1, execution.ExitCode);
        Assert.Contains("Usage: SharpEmu.CLI", execution.StandardOutput, StringComparison.Ordinal);
    }

    private static async Task AssertSyntheticGuestTrapAsync(
        byte[] code,
        string exceptionCode,
        string opcode,
        string? diagnostic = null,
        string? access = null)
    {
        var execution = await RunSyntheticExecutableInCliAsync(code);

        Assert.Equal(4, execution.ExitCode);
        Assert.Contains("Result=ORBIS_GEN2_ERROR_CPU_TRAP", execution.StandardOutput, StringComparison.Ordinal);
        Assert.Contains($"Guest hardware exception 0x{exceptionCode}", execution.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("CPU trap at RIP=", execution.StandardOutput, StringComparison.Ordinal);
        Assert.Contains($"exception=0x{exceptionCode}", execution.StandardOutput, StringComparison.Ordinal);
        Assert.Contains($"opcode=0x{opcode}", execution.StandardOutput, StringComparison.Ordinal);
        if (access is not null)
        {
            Assert.Contains($"access={access}", execution.StandardOutput, StringComparison.Ordinal);
        }
        if (diagnostic is not null)
        {
            Assert.Contains(diagnostic, execution.StandardOutput, StringComparison.Ordinal);
        }
        Assert.Contains("reason=CpuTrap", execution.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("CET/CFG disabled", execution.StandardError, StringComparison.Ordinal);
        Assert.DoesNotContain("FAST_FAIL", execution.StandardError, StringComparison.OrdinalIgnoreCase);
    }

    private static SyntheticRuntimeExecution RunSyntheticExecutable(byte[] code)
    {
        var testDirectory = Path.Combine(
            Path.GetTempPath(),
            "sharpemu-runtime-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(testDirectory);
        var executablePath = Path.Combine(testDirectory, "eboot.bin");

        try
        {
            File.WriteAllBytes(executablePath, SyntheticElfImage.CreateExecutable(code));
            using var runtime = SharpEmuRuntime.CreateDefault();
            var result = runtime.Run(executablePath);
            return new SyntheticRuntimeExecution(
                result,
                runtime.LastExecutionDiagnostics,
                runtime.LastSessionSummary);
        }
        finally
        {
            Directory.Delete(testDirectory, recursive: true);
        }
    }

    private static Task<SyntheticProcessExecution> RunSyntheticExecutableInCliAsync(
        byte[] code,
        bool requestReport = false,
        bool writeExecutable = true,
        int stallWatchdogSeconds = 0,
        int? executionTimeoutSeconds = null,
        string? paramJson = null,
        bool loadOnly = false,
        byte[]? adjacentModuleCode = null,
        bool writeInvalidAdjacentModule = false,
        bool writeSkippedCoreModule = false,
        string? expectedBundleSha256 = null,
        byte[]? adjacentModuleImage = null) =>
        SyntheticCliGuest.RunAsync(
            code,
            requestReport,
            writeExecutable,
            stallWatchdogSeconds,
            executionTimeoutSeconds,
            paramJson,
            loadOnly,
            adjacentModuleCode,
            writeInvalidAdjacentModule,
            writeSkippedCoreModule,
            expectedBundleSha256,
            adjacentModuleImage);

    private readonly record struct SyntheticRuntimeExecution(
        OrbisGen2Result Result,
        string? Diagnostics,
        string? SessionSummary);

}
