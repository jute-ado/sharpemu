// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Text.Json;
using SharpEmu.Logging;
using Xunit;

namespace SharpEmu.GUI.Tests;

public sealed class UpdaterTests
{
    private const string ReleaseSha = "0123456789abcdef0123456789abcdef01234567";
    private const string Digest = "abcdef0123456789abcdef0123456789abcdef0123456789abcdef0123456789";

    [Fact]
    public void ForkIdentityIsTheSingleReleaseSource()
    {
        Assert.Equal("jute-ado/sharpemu", BuildInfo.CanonicalRepository);
        Assert.Equal("https://github.com/jute-ado/sharpemu", BuildInfo.ProjectUrl);
    }

    [Theory]
    [InlineData("jute-ado/sharpemu", "Release", "push", "refs/heads/main", "0123456", true)]
    [InlineData("JUTE-ADO/SHARPEMU", "release", "workflow_dispatch", "refs/heads/main", "0123456", true)]
    [InlineData("jute-ado/sharpemu", "Release", "workflow_dispatch", "refs/heads/topic", "0123456", false)]
    [InlineData("sharpemu/sharpemu", "Release", "push", "refs/heads/main", "0123456", false)]
    [InlineData("jute-ado/sharpemu", "Release", "push", "refs/heads/topic", "0123456", false)]
    [InlineData("jute-ado/sharpemu", "Debug", "push", "refs/heads/main", "0123456", false)]
    [InlineData("jute-ado/sharpemu", "Release", "push", "refs/heads/main", null, false)]
    public void OfficialReleaseClassificationIsForkBound(
        string repository,
        string configuration,
        string eventName,
        string gitRef,
        string? commitSha,
        bool expected)
    {
        Assert.Equal(
            expected,
            BuildInfo.IsOfficialReleaseBuild(
                repository,
                configuration,
                eventName,
                gitRef,
                commitSha));
    }

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

    [Theory]
    [InlineData("sharpemu-0.0.1-win-x64.zip", true)]
    [InlineData("sharpemu-0.0.1-linux-x64.tar.gz", true)]
    [InlineData("../sharpemu-0.0.1-win-x64.zip", false)]
    [InlineData("folder/sharpemu-0.0.1-win-x64.zip", false)]
    [InlineData("folder\\sharpemu-0.0.1-win-x64.zip", false)]
    [InlineData("", false)]
    public void ArchiveNamesMustRemainWithinTheUpdateDirectory(string name, bool expected)
    {
        Assert.Equal(expected, Updater.IsSafeArchiveName(name));
    }

    [Fact]
    public void ParseRelease_RejectsArchiveNameWithDirectoryTraversal()
    {
        var update = Updater.ParseRelease(
            CreateReleaseJson(assetName: "../sharpemu-0.0.1-win-x64.zip"),
            currentSha: null,
            rid: "win-x64",
            extension: ".zip");

        Assert.Null(update);
    }

    [Fact]
    public void ApplyPayloadAtomicallyPreservesExistingStateAndOverlaysRelease()
    {
        using var directories = new TemporaryDirectories();
        Directory.CreateDirectory(directories.Target);
        File.WriteAllText(Path.Combine(directories.Target, "SharpEmu.exe"), "old");
        File.WriteAllText(Path.Combine(directories.Target, "gui-settings.json"), "settings");
        File.WriteAllText(Path.Combine(directories.Target, "obsolete-but-preserved.txt"), "old");
        Directory.CreateDirectory(Path.Combine(directories.Source, "Languages"));
        File.WriteAllText(Path.Combine(directories.Source, "SharpEmu.exe"), "new");
        File.WriteAllText(Path.Combine(directories.Source, "Languages", "en.json"), "new-language");

        string? startedExecutable = null;
        Updater.ApplyPayloadAtomically(
            directories.Source,
            directories.Target,
            "SharpEmu.exe",
            executable => startedExecutable = executable);

        Assert.Equal(Path.Combine(directories.Target, "SharpEmu.exe"), startedExecutable);
        Assert.Equal("new", File.ReadAllText(Path.Combine(directories.Target, "SharpEmu.exe")));
        Assert.Equal("settings", File.ReadAllText(Path.Combine(directories.Target, "gui-settings.json")));
        Assert.Equal("old", File.ReadAllText(Path.Combine(directories.Target, "obsolete-but-preserved.txt")));
        Assert.Equal(
            "new-language",
            File.ReadAllText(Path.Combine(directories.Target, "Languages", "en.json")));
        Assert.Empty(Directory.EnumerateDirectories(
            directories.Root,
            ".install.*",
            SearchOption.TopDirectoryOnly));
    }

    [Fact]
    public void ApplyPayloadAtomicallyRollsBackWhenRestartFails()
    {
        using var directories = new TemporaryDirectories();
        Directory.CreateDirectory(directories.Target);
        File.WriteAllText(Path.Combine(directories.Target, "SharpEmu.exe"), "old");
        File.WriteAllText(Path.Combine(directories.Source, "SharpEmu.exe"), "new");

        var exception = Assert.Throws<InvalidOperationException>(() =>
            Updater.ApplyPayloadAtomically(
                directories.Source,
                directories.Target,
                "SharpEmu.exe",
                _ => throw new InvalidOperationException("restart failed")));

        Assert.Equal("restart failed", exception.Message);
        Assert.Equal("old", File.ReadAllText(Path.Combine(directories.Target, "SharpEmu.exe")));
        Assert.Empty(Directory.EnumerateDirectories(
            directories.Root,
            ".install.*",
            SearchOption.TopDirectoryOnly));
    }

    [Fact]
    public void ApplyPayloadAtomicallyRejectsMissingExecutableWithoutTouchingTarget()
    {
        using var directories = new TemporaryDirectories();
        Directory.CreateDirectory(directories.Target);
        File.WriteAllText(Path.Combine(directories.Target, "SharpEmu.exe"), "old");
        File.WriteAllText(Path.Combine(directories.Source, "readme.txt"), "payload");

        Assert.Throws<InvalidDataException>(() =>
            Updater.ApplyPayloadAtomically(
                directories.Source,
                directories.Target,
                "Missing.exe",
                _ => throw new Xunit.Sdk.XunitException("must not restart")));

        Assert.Equal("old", File.ReadAllText(Path.Combine(directories.Target, "SharpEmu.exe")));
    }

    private static string CreateReleaseJson(
        bool includeDigest = true,
        string? assetName = null)
    {
        var assets = new[]
        {
            Asset(assetName ?? "sharpemu-0.0.1-win-x64.zip", ".zip", includeDigest),
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

    private sealed class TemporaryDirectories : IDisposable
    {
        public TemporaryDirectories()
        {
            Root = Path.Combine(Path.GetTempPath(), "SharpEmu.Updater.Tests", Guid.NewGuid().ToString("N"));
            Source = Path.Combine(Root, "payload");
            Target = Path.Combine(Root, "install");
            Directory.CreateDirectory(Source);
        }

        public string Root { get; }
        public string Source { get; }
        public string Target { get; }

        public void Dispose()
        {
            if (Directory.Exists(Root))
            {
                Directory.Delete(Root, recursive: true);
            }
        }
    }
}
