// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace SharpEmu.Core.Tests;

internal static class NativeTestProcess
{
    private const string ChildEnvironmentVariable = "SHARPEMU_NATIVE_TEST_CHILD";

    public static async Task<bool> RunIfNeededAsync(
        Type testClass,
        [CallerMemberName] string testMethod = "")
    {
        if (string.Equals(
                Environment.GetEnvironmentVariable(ChildEnvironmentVariable),
                "1",
                StringComparison.Ordinal))
        {
            return false;
        }

        var testAssemblyPath = testClass.Assembly.Location;
        var fullyQualifiedName = $"{testClass.FullName}.{testMethod}";
        var startInfo = new ProcessStartInfo("dotnet")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        startInfo.ArgumentList.Add("test");
        startInfo.ArgumentList.Add(testAssemblyPath);
        startInfo.ArgumentList.Add("--filter");
        startInfo.ArgumentList.Add($"FullyQualifiedName={fullyQualifiedName}");
        startInfo.ArgumentList.Add("--logger");
        startInfo.ArgumentList.Add("console;verbosity=minimal");
        startInfo.Environment[ChildEnvironmentVariable] = "1";

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException($"Failed to start isolated native test '{fullyQualifiedName}'.");
        var standardOutput = process.StandardOutput.ReadToEndAsync();
        var standardError = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync();
            throw new TimeoutException($"Isolated native test '{fullyQualifiedName}' exceeded 60 seconds.");
        }

        var output = await standardOutput;
        var error = await standardError;
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"Isolated native test '{fullyQualifiedName}' exited with {process.ExitCode}." +
                $"{Environment.NewLine}stdout:{Environment.NewLine}{output}" +
                $"{Environment.NewLine}stderr:{Environment.NewLine}{error}");
        }

        return true;
    }
}
