// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace SharpEmu.GameTests;

internal static class GameRegressionRunner
{
    public static async Task<GameRegressionExecution> RunAsync(
        GameRegressionCase testCase,
        string manifestPath,
        string artifactDirectory)
    {
        ValidateCase(testCase);
        var manifestDirectory = Path.GetDirectoryName(manifestPath)
            ?? throw new InvalidDataException(
                "The game regression manifest has no parent directory.");
        var executablePath = ResolvePath(
            manifestDirectory,
            testCase.ExecutablePath);
        if (!File.Exists(executablePath))
        {
            throw new FileNotFoundException(
                $"Configured game executable was not found for '{testCase.Name}'.",
                executablePath);
        }

        var cliAssemblyPath = GameRegressionPaths.CliAssemblyPath;
        if (!File.Exists(cliAssemblyPath))
        {
            throw new FileNotFoundException(
                "SharpEmu CLI has not been built for the game regression test.",
                cliAssemblyPath);
        }

        Directory.CreateDirectory(artifactDirectory);
        var artifactStem = SanitizeFileName(testCase.Name) + "-" + testCase.Mode;
        var reportPath = Path.Combine(
            artifactDirectory,
            artifactStem + ".report.json");
        var standardOutputPath = Path.Combine(
            artifactDirectory,
            artifactStem + ".stdout.log");
        var standardErrorPath = Path.Combine(
            artifactDirectory,
            artifactStem + ".stderr.log");

        var startInfo = new ProcessStartInfo("dotnet")
        {
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
        };
        startInfo.ArgumentList.Add(cliAssemblyPath);
        if (string.Equals(testCase.Mode, "load-only", StringComparison.Ordinal))
        {
            startInfo.ArgumentList.Add("--load-only");
        }
        else
        {
            startInfo.ArgumentList.Add("--timeout-seconds");
            startInfo.ArgumentList.Add(testCase.TimeoutSeconds.ToString());
        }
        if (!string.IsNullOrWhiteSpace(testCase.ExpectedBundleSha256))
        {
            startInfo.ArgumentList.Add("--expect-bundle-sha256");
            startInfo.ArgumentList.Add(testCase.ExpectedBundleSha256);
        }
        startInfo.ArgumentList.Add("--report-json");
        startInfo.ArgumentList.Add(reportPath);
        startInfo.ArgumentList.Add(executablePath);
        startInfo.Environment.Remove("SHARPEMU_MITIGATED_CHILD");
        startInfo.Environment.Remove("SHARPEMU_DISABLE_MITIGATION_RELAUNCH");

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException(
                "Failed to start the SharpEmu game regression process.");
        var standardOutputTask = process.StandardOutput.ReadToEndAsync();
        var standardErrorTask = process.StandardError.ReadToEndAsync();
        var outerTimeoutSeconds = checked(testCase.TimeoutSeconds + 60);
        using var timeout = new CancellationTokenSource(
            TimeSpan.FromSeconds(outerTimeoutSeconds));
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync();
            throw new TimeoutException(
                $"Game regression '{testCase.Name}' exceeded its outer " +
                $"{outerTimeoutSeconds}-second safety timeout.");
        }

        var standardOutput = await standardOutputTask;
        var standardError = await standardErrorTask;
        await File.WriteAllTextAsync(standardOutputPath, standardOutput);
        await File.WriteAllTextAsync(standardErrorPath, standardError);
        if (!File.Exists(reportPath))
        {
            throw new InvalidDataException(
                $"Game regression '{testCase.Name}' produced no JSON report. " +
                $"Exit code: {process.ExitCode}. Diagnostics: {standardErrorPath}");
        }

        var reportJson = await File.ReadAllTextAsync(reportPath);
        using var report = JsonDocument.Parse(reportJson);
        ValidateReport(testCase, report.RootElement, reportPath);
        return new GameRegressionExecution(
            testCase.Name,
            testCase.Mode,
            process.ExitCode,
            reportPath,
            standardOutputPath,
            standardErrorPath);
    }

    public static string ResolveArtifactDirectory(
        GameRegressionManifest manifest,
        string manifestPath)
    {
        if (string.IsNullOrWhiteSpace(manifest.ArtifactDirectory))
        {
            return Path.Combine(
                GameRegressionPaths.RepoRoot,
                "artifacts",
                "game-tests");
        }

        var manifestDirectory = Path.GetDirectoryName(manifestPath)
            ?? GameRegressionPaths.RepoRoot;
        return ResolvePath(manifestDirectory, manifest.ArtifactDirectory);
    }

    internal static void ValidateCase(GameRegressionCase testCase)
    {
        if (string.IsNullOrWhiteSpace(testCase.Name))
        {
            throw new InvalidDataException(
                "Every game regression case requires a name.");
        }
        if (string.IsNullOrWhiteSpace(testCase.ExecutablePath))
        {
            throw new InvalidDataException(
                $"Game regression '{testCase.Name}' requires an executablePath.");
        }
        if (!string.Equals(testCase.Mode, "load-only", StringComparison.Ordinal) &&
            !string.Equals(testCase.Mode, "execution", StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"Game regression '{testCase.Name}' has unsupported mode " +
                $"'{testCase.Mode}'.");
        }
        if (testCase.TimeoutSeconds is <= 0 or > 86400)
        {
            throw new InvalidDataException(
                $"Game regression '{testCase.Name}' has an invalid timeout.");
        }
        if (testCase.Expectations.AllowedResults.Length == 0)
        {
            throw new InvalidDataException(
                $"Game regression '{testCase.Name}' requires allowedResults.");
        }
    }

    internal static void ValidateReport(
        GameRegressionCase testCase,
        JsonElement report,
        string reportPath)
    {
        var failures = new StringBuilder();
        var resultName = GetRequiredString(
            report.GetProperty("result"),
            "name");
        if (!Contains(
                testCase.Expectations.AllowedResults,
                resultName))
        {
            failures.AppendLine(
                $"result '{resultName}' was not allowed " +
                $"({string.Join(", ", testCase.Expectations.AllowedResults)}).");
        }
        if (Contains(testCase.Expectations.ForbiddenResults, resultName))
        {
            failures.AppendLine($"result '{resultName}' was explicitly forbidden.");
        }

        if (!string.IsNullOrWhiteSpace(
                testCase.Expectations.ApplicationTitleId) &&
            (!report.TryGetProperty("application", out var application) ||
             application.ValueKind != JsonValueKind.Object ||
             !string.Equals(
                 GetOptionalString(application, "titleId"),
                 testCase.Expectations.ApplicationTitleId,
                 StringComparison.OrdinalIgnoreCase)))
        {
            failures.AppendLine(
                $"application title ID did not match " +
                $"'{testCase.Expectations.ApplicationTitleId}'.");
        }

        if (testCase.Expectations.MinimumModules is { } minimumModules)
        {
            var moduleCount = report.TryGetProperty("modules", out var modules) &&
                modules.ValueKind == JsonValueKind.Array
                ? modules.GetArrayLength()
                : 0;
            if (moduleCount < minimumModules)
            {
                failures.AppendLine(
                    $"module count {moduleCount} was below {minimumModules}.");
            }
        }

        if (testCase.Expectations.RequireNoModuleLoadFailures &&
            report.TryGetProperty("moduleLoadFailures", out var loadFailures) &&
            loadFailures.ValueKind == JsonValueKind.Array &&
            loadFailures.GetArrayLength() != 0)
        {
            failures.AppendLine(
                $"module load failures: {loadFailures.GetArrayLength()}.");
        }

        if (testCase.Expectations.RequireSuccessfulModuleInitializers &&
            report.TryGetProperty("moduleInitializers", out var initializers) &&
            initializers.ValueKind == JsonValueKind.Array)
        {
            foreach (var initializer in initializers.EnumerateArray())
            {
                if (!initializer.GetProperty("result")
                    .GetProperty("succeeded")
                    .GetBoolean())
                {
                    failures.AppendLine(
                        "at least one module initializer failed.");
                    break;
                }
            }
        }

        if (failures.Length != 0)
        {
            throw new InvalidOperationException(
                $"Game regression '{testCase.Name}' failed:{Environment.NewLine}" +
                failures +
                $"Report: {reportPath}");
        }
    }

    private static bool Contains(string[] values, string value)
    {
        for (var index = 0; index < values.Length; index++)
        {
            if (string.Equals(
                    values[index],
                    value,
                    StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static string ResolvePath(string baseDirectory, string path) =>
        Path.GetFullPath(
            Path.IsPathRooted(path)
                ? path
                : Path.Combine(baseDirectory, path));

    private static string GetRequiredString(JsonElement element, string name) =>
        GetOptionalString(element, name)
        ?? throw new InvalidDataException(
            $"Game regression report property '{name}' is missing.");

    private static string? GetOptionalString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) &&
        value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static string SanitizeFileName(string value)
    {
        var invalidCharacters = Path.GetInvalidFileNameChars();
        var result = new StringBuilder(value.Length);
        for (var index = 0; index < value.Length; index++)
        {
            result.Append(
                Array.IndexOf(invalidCharacters, value[index]) >= 0 ||
                char.IsWhiteSpace(value[index])
                    ? '-'
                    : char.ToLowerInvariant(value[index]));
        }

        return result.ToString();
    }
}

internal readonly record struct GameRegressionExecution(
    string Name,
    string Mode,
    int ExitCode,
    string ReportPath,
    string StandardOutputPath,
    string StandardErrorPath);
