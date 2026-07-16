// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Text.Json;

namespace SharpEmu.GameTests;

internal sealed class GameRegressionManifest
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        AllowTrailingCommas = true,
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
    };

    public int SchemaVersion { get; init; }

    public string? ArtifactDirectory { get; init; }

    public GameRegressionCase[] Cases { get; init; } = [];

    public static GameRegressionManifest Load(string path)
    {
        using var stream = File.OpenRead(path);
        var manifest = JsonSerializer.Deserialize<GameRegressionManifest>(
            stream,
            SerializerOptions)
            ?? throw new InvalidDataException("The game regression manifest is empty.");
        if (manifest.SchemaVersion != 1)
        {
            throw new InvalidDataException(
                $"Unsupported game regression manifest schema {manifest.SchemaVersion}.");
        }
        if (manifest.Cases.Length == 0)
        {
            throw new InvalidDataException("The game regression manifest has no cases.");
        }

        return manifest;
    }
}

internal sealed class GameRegressionCase
{
    public string Name { get; init; } = string.Empty;

    public string ExecutablePath { get; init; } = string.Empty;

    public string Mode { get; init; } = "execution";

    public int TimeoutSeconds { get; init; } = 20;

    public string? ExpectedBundleSha256 { get; init; }

    public GameRegressionExpectations Expectations { get; init; } = new();
}

internal sealed class GameRegressionExpectations
{
    public string[] AllowedResults { get; init; } = [];

    public string[] ForbiddenResults { get; init; } = [];

    public string? ApplicationTitleId { get; init; }

    public int? MinimumModules { get; init; }

    public bool RequireNoModuleLoadFailures { get; init; }

    public bool RequireSuccessfulModuleInitializers { get; init; }

    public long? MinimumObservedImportDispatches { get; init; }
}
