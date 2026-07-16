// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Reflection;

namespace SharpEmu.GameTests;

internal static class GameRegressionPaths
{
    private const string ManifestEnvironmentVariable = "SHARPEMU_GAME_TEST_MANIFEST";

    public static string RepoRoot => ReadAssemblyMetadata("SharpEmu.RepoRoot");

    public static string ManifestPath
    {
        get
        {
            var configured = Environment.GetEnvironmentVariable(
                ManifestEnvironmentVariable);
            return string.IsNullOrWhiteSpace(configured)
                ? Path.Combine(
                    RepoRoot,
                    "tests",
                    "SharpEmu.GameTests",
                    "games.local.json")
                : Path.GetFullPath(configured);
        }
    }

    public static string CliAssemblyPath =>
        ReadAssemblyMetadata("SharpEmu.CliAssemblyPath");

    private static string ReadAssemblyMetadata(string key)
    {
        var attributes = typeof(GameRegressionPaths).Assembly
            .GetCustomAttributes<AssemblyMetadataAttribute>();
        foreach (var attribute in attributes)
        {
            if (string.Equals(attribute.Key, key, StringComparison.Ordinal))
            {
                return attribute.Value
                    ?? throw new InvalidOperationException(
                        $"Assembly metadata '{key}' has no value.");
            }
        }

        throw new InvalidOperationException(
            $"Required assembly metadata '{key}' is missing.");
    }
}
