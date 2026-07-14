// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;
using Xunit;

namespace SharpEmu.Core.Tests;

internal static class SyntheticCliGuest
{
    public static async Task<SyntheticProcessExecution> RunAsync(
        byte[] code,
        bool requestReport = false,
        bool writeExecutable = true,
        int stallWatchdogSeconds = 0,
        int? executionTimeoutSeconds = null,
        string? paramJson = null,
        bool loadOnly = false)
    {
        var testDirectory = Path.Combine(
            Path.GetTempPath(),
            "sharpemu-runtime-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(testDirectory);
        var executablePath = Path.Combine(testDirectory, "eboot.bin");
        var reportPath = Path.Combine(testDirectory, "execution-report.json");

        try
        {
            string? executableSha256 = null;
            if (writeExecutable)
            {
                var executable = SyntheticElfImage.CreateExecutable(code);
                File.WriteAllBytes(executablePath, executable);
                executableSha256 = Convert.ToHexStringLower(SHA256.HashData(executable));
            }
            if (paramJson is not null)
            {
                var systemDirectory = Path.Combine(testDirectory, "sce_sys");
                Directory.CreateDirectory(systemDirectory);
                File.WriteAllText(Path.Combine(systemDirectory, "param.json"), paramJson);
            }

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
            if (loadOnly)
            {
                startInfo.ArgumentList.Add("--load-only");
            }
            if (executionTimeoutSeconds is { } timeoutSeconds)
            {
                startInfo.ArgumentList.Add("--timeout-seconds");
                startInfo.ArgumentList.Add(timeoutSeconds.ToString());
            }
            if (requestReport)
            {
                startInfo.ArgumentList.Add("--report-json");
                startInfo.ArgumentList.Add(reportPath);
            }
            startInfo.ArgumentList.Add(executablePath);
            startInfo.Environment.Remove("SHARPEMU_MITIGATED_CHILD");
            startInfo.Environment.Remove("SHARPEMU_DISABLE_MITIGATION_RELAUNCH");
            startInfo.Environment["SHARPEMU_STALL_WATCHDOG_SECONDS"] = stallWatchdogSeconds.ToString();

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
                await standardError,
                File.Exists(reportPath) ? await File.ReadAllTextAsync(reportPath) : null,
                executableSha256);
        }
        finally
        {
            Directory.Delete(testDirectory, recursive: true);
        }
    }

    private static string GetCliAssemblyPath()
    {
        return typeof(SyntheticCliGuest).Assembly
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .Single(attribute => attribute.Key == "SharpEmu.CliAssemblyPath")
            .Value
            ?? throw new InvalidOperationException("SharpEmu CLI test path metadata is missing.");
    }
}

internal readonly record struct SyntheticProcessExecution(
    int ExitCode,
    string StandardOutput,
    string StandardError,
    string? ReportJson,
    string? ExecutableSha256);
