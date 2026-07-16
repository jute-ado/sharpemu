// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using Xunit;
using Xunit.Abstractions;

namespace SharpEmu.GameTests;

public sealed class ConfiguredGameRegressionTests(ITestOutputHelper output)
{
    [GameRegressionFact]
    public async Task ConfiguredGamesMeetCompatibilityExpectations()
    {
        var manifestPath = GameRegressionPaths.ManifestPath;
        var manifest = GameRegressionManifest.Load(manifestPath);
        var artifactDirectory = GameRegressionRunner.ResolveArtifactDirectory(
            manifest,
            manifestPath);

        for (var index = 0; index < manifest.Cases.Length; index++)
        {
            var execution = await GameRegressionRunner.RunAsync(
                manifest.Cases[index],
                manifestPath,
                artifactDirectory);
            output.WriteLine(
                $"{execution.Name} ({execution.Mode}): exit={execution.ExitCode}");
            output.WriteLine($"  report: {execution.ReportPath}");
            output.WriteLine($"  stdout: {execution.StandardOutputPath}");
            output.WriteLine($"  stderr: {execution.StandardErrorPath}");
        }
    }
}
