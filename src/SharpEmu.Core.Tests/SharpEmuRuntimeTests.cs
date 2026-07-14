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
        Assert.Equal(1, root.GetProperty("schemaVersion").GetInt32());
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
        Assert.Equal("Release", root.GetProperty("build").GetProperty("configuration").GetString());
        Assert.False(root.GetProperty("build").GetProperty("isOfficialRelease").GetBoolean());
        Assert.False(string.IsNullOrWhiteSpace(root.GetProperty("host").GetProperty("osDescription").GetString()));
        Assert.False(string.IsNullOrWhiteSpace(root.GetProperty("host").GetProperty("processArchitecture").GetString()));
        var application = root.GetProperty("application");
        Assert.Equal("Synthetic title", application.GetProperty("title").GetString());
        Assert.Equal("PPSA00001", application.GetProperty("titleId").GetString());
        Assert.Equal("EP0001-PPSA00001_00-SHARPEMUTEST0001", application.GetProperty("contentId").GetString());
        Assert.Equal("01.20", application.GetProperty("version").GetString());
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
        Assert.Equal(JsonValueKind.Null, root.GetProperty("application").ValueKind);
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
        string? paramJson = null) =>
        SyntheticCliGuest.RunAsync(
            code,
            requestReport,
            writeExecutable,
            stallWatchdogSeconds,
            executionTimeoutSeconds,
            paramJson);

    private readonly record struct SyntheticRuntimeExecution(
        OrbisGen2Result Result,
        string? Diagnostics,
        string? SessionSummary);

}
