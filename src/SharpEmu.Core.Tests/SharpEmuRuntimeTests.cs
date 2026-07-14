// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Diagnostics;
using System.Reflection;
using SharpEmu.Core.Runtime;
using SharpEmu.HLE;
using Xunit;

namespace SharpEmu.Core.Tests;

public sealed class SharpEmuRuntimeTests
{
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
            opcode: "48");
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

    private static async Task AssertSyntheticGuestTrapAsync(
        byte[] code,
        string exceptionCode,
        string opcode,
        string? diagnostic = null)
    {
        var execution = await RunSyntheticExecutableInCliAsync(code);

        Assert.Equal(4, execution.ExitCode);
        Assert.Contains("Result=ORBIS_GEN2_ERROR_CPU_TRAP", execution.StandardOutput, StringComparison.Ordinal);
        Assert.Contains($"Guest hardware exception 0x{exceptionCode}", execution.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("CPU trap at RIP=", execution.StandardOutput, StringComparison.Ordinal);
        Assert.Contains($"opcode=0x{opcode}", execution.StandardOutput, StringComparison.Ordinal);
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

    private static async Task<SyntheticProcessExecution> RunSyntheticExecutableInCliAsync(byte[] code)
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
            var cliAssemblyPath = GetCliAssemblyPath();
            Assert.True(File.Exists(cliAssemblyPath), $"SharpEmu CLI was not built at '{cliAssemblyPath}'.");

            var startInfo = new ProcessStartInfo("dotnet")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            startInfo.ArgumentList.Add(cliAssemblyPath);
            startInfo.ArgumentList.Add(executablePath);
            startInfo.Environment.Remove("SHARPEMU_MITIGATED_CHILD");
            startInfo.Environment.Remove("SHARPEMU_DISABLE_MITIGATION_RELAUNCH");
            startInfo.Environment["SHARPEMU_STALL_WATCHDOG_SECONDS"] = "0";

            using var process = Process.Start(startInfo)
                ?? throw new InvalidOperationException("Failed to start the SharpEmu CLI test process.");
            var standardOutput = process.StandardOutput.ReadToEndAsync();
            var standardError = process.StandardError.ReadToEndAsync();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            try
            {
                await process.WaitForExitAsync(timeout.Token);
            }
            catch (OperationCanceledException)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync();
                throw new TimeoutException("SharpEmu CLI did not finish the synthetic executable within 30 seconds.");
            }

            return new SyntheticProcessExecution(
                process.ExitCode,
                await standardOutput,
                await standardError);
        }
        finally
        {
            Directory.Delete(testDirectory, recursive: true);
        }
    }

    private static string GetCliAssemblyPath()
    {
        return typeof(SharpEmuRuntimeTests).Assembly
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .Single(attribute => attribute.Key == "SharpEmu.CliAssemblyPath")
            .Value
            ?? throw new InvalidOperationException("SharpEmu CLI test path metadata is missing.");
    }

    private readonly record struct SyntheticRuntimeExecution(
        OrbisGen2Result Result,
        string? Diagnostics,
        string? SessionSummary);

    private readonly record struct SyntheticProcessExecution(
        int ExitCode,
        string StandardOutput,
        string StandardError);
}
