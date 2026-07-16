// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Text.Json;
using Xunit;

namespace SharpEmu.GUI.Tests;

public sealed class UpdaterTests
{
    private const string ReleaseSha = "0123456789abcdef0123456789abcdef01234567";
    private const string Digest = "abcdef0123456789abcdef0123456789abcdef0123456789abcdef0123456789";

    [Fact]
    public void ParseRelease_SelectsForkArchiveAndUsesReleaseCommit()
    {
        var update = Updater.ParseRelease(
            CreateReleaseJson(),
            currentSha: "7654321",
            rid: "osx-x64",
            extension: ".tar.gz");

        Assert.NotNull(update);
        Assert.Equal(ReleaseSha, update.Sha);
        Assert.Equal("sharpemu-0.0.1-osx-x64.tar.gz", update.Name);
        Assert.Equal(Digest, update.Sha256);
    }

    [Fact]
    public void ParseRelease_TreatsShortCurrentShaAsCurrent()
    {
        var update = Updater.ParseRelease(
            CreateReleaseJson(),
            currentSha: ReleaseSha[..7],
            rid: "win-x64",
            extension: ".zip");

        Assert.Null(update);
    }

    [Fact]
    public void ParseRelease_RejectsArchiveWithoutGitHubDigest()
    {
        var update = Updater.ParseRelease(
            CreateReleaseJson(includeDigest: false),
            currentSha: null,
            rid: "linux-x64",
            extension: ".tar.gz");

        Assert.Null(update);
    }

    private static string CreateReleaseJson(bool includeDigest = true)
    {
        var assets = new[]
        {
            Asset("sharpemu-0.0.1-win-x64.zip", ".zip", includeDigest),
            Asset("sharpemu-0.0.1-linux-x64.tar.gz", ".tar.gz", includeDigest),
            Asset("sharpemu-0.0.1-osx-x64.tar.gz", ".tar.gz", includeDigest),
        };
        return JsonSerializer.Serialize(new
        {
            tag_name = "v0.0.1-main-0123456",
            target_commitish = ReleaseSha,
            assets,
        });
    }

    private static object Asset(string name, string extension, bool includeDigest) =>
        new Dictionary<string, object?>
        {
            ["name"] = name,
            ["created_at"] = "2026-07-16T12:00:00Z",
            ["browser_download_url"] = $"https://github.com/jute-ado/sharpemu/releases/download/test/{name}",
            ["size"] = extension == ".zip" ? 42 : 84,
            ["digest"] = includeDigest ? $"sha256:{Digest}" : null,
        };
}
