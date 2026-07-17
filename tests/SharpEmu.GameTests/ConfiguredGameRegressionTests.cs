// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using Xunit;
using Xunit.Abstractions;

namespace SharpEmu.GameTests;

public sealed class ConfiguredGameRegressionTests(ITestOutputHelper output)
{
    public static TheoryData<int, string> ConfiguredCases
    {
        get
        {
            var cases = new TheoryData<int, string>();
            if (!File.Exists(GameRegressionPaths.ManifestPath))
            {
                return cases;
            }

            var manifest = GameRegressionManifest.Load(
                GameRegressionPaths.ManifestPath);
            for (var index = 0; index < manifest.Cases.Length; index++)
            {
                cases.Add(index, manifest.Cases[index].Name);
            }

            return cases;
        }
    }

    [GameRegressionTheory]
    [MemberData(nameof(ConfiguredCases))]
    public async Task ConfiguredGameMeetsCompatibilityExpectations(
        int caseIndex,
        string caseName)
    {
        var manifestPath = GameRegressionPaths.ManifestPath;
        var manifest = GameRegressionManifest.Load(manifestPath);
        if ((uint)caseIndex >= (uint)manifest.Cases.Length ||
            !string.Equals(
                manifest.Cases[caseIndex].Name,
                caseName,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "The game regression manifest changed after test discovery.");
        }
        var artifactDirectory = GameRegressionRunner.ResolveArtifactDirectory(
            manifest,
            manifestPath);
        var execution = await GameRegressionRunner.RunAsync(
            manifest.Cases[caseIndex],
            manifestPath,
            artifactDirectory);
        output.WriteLine(
            $"{execution.Name} ({execution.Mode}): exit={execution.ExitCode}");
        output.WriteLine($"  report: {execution.ReportPath}");
        output.WriteLine($"  stdout: {execution.StandardOutputPath}");
        output.WriteLine($"  stderr: {execution.StandardErrorPath}");
    }
}
