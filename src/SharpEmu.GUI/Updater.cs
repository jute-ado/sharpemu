// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Diagnostics;
using System.Formats.Tar;
using System.IO.Compression;
using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using SharpEmu.Logging;

namespace SharpEmu.GUI;

/// <summary>Self-contained cross-platform updater; emulator layers do not depend on it.</summary>
public static class Updater
{
    private const string ApplyArgument = "--sharpemu-apply-update";
    private const string LatestReleaseUrl =
        "https://api.github.com/repos/" + BuildInfo.CanonicalRepository + "/releases/latest";
    private static readonly TimeSpan CheckTimeout = TimeSpan.FromSeconds(10);
    private static readonly HttpClient Http = CreateHttpClient();

    public sealed record UpdateInfo(
        string Sha,
        string Name,
        string DownloadUrl,
        long Size,
        string Sha256);

    public static async Task<UpdateInfo?> CheckAsync(string? currentSha, CancellationToken cancellationToken = default)
    {
        var platform = CurrentPlatform();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(CheckTimeout);

        using var response = await Http.GetAsync(LatestReleaseUrl, timeout.Token);
        response.EnsureSuccessStatusCode();
        return ParseRelease(
            await response.Content.ReadAsStringAsync(timeout.Token),
            currentSha,
            platform.Rid,
            platform.Extension);
    }

    public static async Task DownloadAndRestartAsync(
        UpdateInfo update,
        IProgress<int>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(update);
        if (!IsSafeArchiveName(update.Name))
        {
            throw new InvalidDataException($"The update archive name '{update.Name}' is not safe.");
        }

        var root = Path.Combine(
            Path.GetTempPath(),
            "SharpEmu.Update",
            $"{Environment.ProcessId}-{Guid.NewGuid():N}");
        var payload = Path.Combine(root, "payload");
        Directory.CreateDirectory(root);
        var archive = Path.Combine(root, update.Name);
        using var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        using (var response = await Http.GetAsync(update.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken))
        {
            response.EnsureSuccessStatusCode();
            await using var input = await response.Content.ReadAsStreamAsync(cancellationToken);
            await using var output = File.Create(archive);
            var buffer = new byte[81920];
            long written = 0;
            int read;
            while ((read = await input.ReadAsync(buffer, cancellationToken)) > 0)
            {
                await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                hasher.AppendData(buffer, 0, read);
                written += read;
                progress?.Report(update.Size == 0 ? 0 : (int)(written * 100 / update.Size));
            }

            if (written != update.Size)
            {
                throw new InvalidDataException($"Downloaded {written} bytes; expected {update.Size}.");
            }
        }

        var actualHash = Convert.ToHexString(hasher.GetHashAndReset()).ToLowerInvariant();
        if (!CryptographicOperations.FixedTimeEquals(
                Convert.FromHexString(actualHash),
                Convert.FromHexString(update.Sha256)))
        {
            throw new InvalidDataException(
                $"Downloaded update checksum {actualHash} does not match release checksum {update.Sha256}.");
        }

        var platform = CurrentPlatform();
        var stagedExe = ExtractArchive(archive, payload, platform.Extension, platform.ExecutableName);

        var start = new ProcessStartInfo(stagedExe)
        {
            UseShellExecute = false,
            WorkingDirectory = payload,
        };
        start.ArgumentList.Add(ApplyArgument);
        start.ArgumentList.Add(Environment.ProcessId.ToString());
        start.ArgumentList.Add(AppContext.BaseDirectory);
        using var helper = Process.Start(start)
            ?? throw new InvalidOperationException("The update installer could not be started.");
    }

    /// <summary>Runs from the downloaded executable after the old GUI exits.</summary>
    public static bool TryApply(string[] args, out int exitCode)
    {
        exitCode = 0;
        if (args.Length != 3 || args[0] != ApplyArgument)
        {
            return false;
        }

        try
        {
            if (int.TryParse(args[1], out var oldPid))
            {
                try
                {
                    if (!Process.GetProcessById(oldPid).WaitForExit(30_000))
                    {
                        throw new TimeoutException("SharpEmu did not close within 30 seconds.");
                    }
                }
                catch (ArgumentException)
                {
                    // The old process has already exited.
                }
            }

            var source = AppContext.BaseDirectory;
            var target = Path.GetFullPath(args[2]);
            ApplyPayloadAtomically(
                source,
                target,
                CurrentPlatform().ExecutableName,
                executable =>
                {
                    using var restarted = Process.Start(new ProcessStartInfo(executable)
                    {
                        UseShellExecute = false,
                        WorkingDirectory = target,
                    }) ?? throw new InvalidOperationException("The updated SharpEmu could not be started.");
                });
        }
        catch (Exception ex)
        {
            exitCode = 1;
            try
            {
                File.WriteAllText(Path.Combine(args[2], "update-error.log"), ex.ToString());
            }
            catch
            {
                // Best-effort diagnostics only.
            }
        }

        return true;
    }

    internal static UpdateInfo? ParseRelease(
        string json,
        string? currentSha,
        string rid,
        string extension)
    {
        using var document = JsonDocument.Parse(json);
        var releaseSha = ParseReleaseSha(document.RootElement);
        if (releaseSha is null)
        {
            return null;
        }

        var candidates = new List<(DateTimeOffset Created, UpdateInfo Update)>();
        foreach (var asset in document.RootElement.GetProperty("assets").EnumerateArray())
        {
            var name = asset.GetProperty("name").GetString() ?? "";
            var suffix = $"-{rid}{extension}";
            if (!IsSafeArchiveName(name) ||
                !name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var digest = asset.TryGetProperty("digest", out var digestElement)
                ? digestElement.GetString()
                : null;
            const string digestPrefix = "sha256:";
            if (digest is null ||
                !digest.StartsWith(digestPrefix, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var sha256 = digest[digestPrefix.Length..];
            if (sha256.Length != 64 || !sha256.All(Uri.IsHexDigit))
            {
                continue;
            }

            candidates.Add((
                asset.GetProperty("created_at").GetDateTimeOffset(),
                new UpdateInfo(
                    releaseSha,
                    name,
                    asset.GetProperty("browser_download_url").GetString()!,
                    asset.GetProperty("size").GetInt64(),
                    sha256.ToLowerInvariant())));
        }

        var latest = candidates.OrderByDescending(candidate => candidate.Created).FirstOrDefault().Update;
        return latest is null || IsSameCommit(latest.Sha, currentSha)
            ? null
            : latest;
    }

    private static string? ParseReleaseSha(JsonElement release)
    {
        if (release.TryGetProperty("target_commitish", out var targetElement))
        {
            var target = targetElement.GetString();
            if (target is { Length: >= 7 } && target.All(Uri.IsHexDigit))
            {
                return target.ToLowerInvariant();
            }
        }

        if (!release.TryGetProperty("tag_name", out var tagElement))
        {
            return null;
        }

        var tag = tagElement.GetString();
        var suffix = tag?.Split('-').LastOrDefault();
        return suffix is { Length: >= 7 } && suffix.All(Uri.IsHexDigit)
            ? suffix.ToLowerInvariant()
            : null;
    }

    private static bool IsSameCommit(string releaseSha, string? currentSha) =>
        currentSha is not null &&
        (releaseSha.StartsWith(currentSha, StringComparison.OrdinalIgnoreCase) ||
         currentSha.StartsWith(releaseSha, StringComparison.OrdinalIgnoreCase));

    internal static bool IsSafeArchiveName(string name) =>
        !string.IsNullOrWhiteSpace(name) &&
        string.Equals(name, Path.GetFileName(name), StringComparison.Ordinal) &&
        name.All(character =>
            char.IsAsciiLetterOrDigit(character) ||
            character is '.' or '-' or '_');

    internal static void ApplyPayloadAtomically(
        string source,
        string target,
        string executableName,
        Action<string> startUpdatedProcess)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(source);
        ArgumentException.ThrowIfNullOrWhiteSpace(target);
        ArgumentException.ThrowIfNullOrWhiteSpace(executableName);
        ArgumentNullException.ThrowIfNull(startUpdatedProcess);

        source = NormalizeDirectory(source);
        target = NormalizeDirectory(target);
        if (PathsEqual(source, target))
        {
            throw new InvalidOperationException("The update source and target must be different directories.");
        }

        if (PathsEqual(target, Path.GetPathRoot(target)!))
        {
            throw new InvalidOperationException("The update target cannot be a filesystem root.");
        }

        if (!Directory.Exists(source))
        {
            throw new DirectoryNotFoundException($"The update payload directory '{source}' does not exist.");
        }

        var targetParent = Path.GetDirectoryName(target)
            ?? throw new InvalidOperationException("The update target must have a parent directory.");
        Directory.CreateDirectory(targetParent);

        var targetName = Path.GetFileName(target);
        var nonce = Guid.NewGuid().ToString("N");
        var staging = Path.Combine(targetParent, $".{targetName}.update-{nonce}");
        var backup = Path.Combine(targetParent, $".{targetName}.backup-{nonce}");
        var targetMoved = false;
        var stagingActivated = false;
        var started = false;

        try
        {
            if (Directory.Exists(target))
            {
                CopyDirectory(target, staging, overwrite: false);
            }
            else
            {
                Directory.CreateDirectory(staging);
            }

            CopyDirectory(source, staging, overwrite: true);
            var stagedExecutable = Path.Combine(staging, executableName);
            if (!File.Exists(stagedExecutable))
            {
                throw new InvalidDataException(
                    $"The staged update does not contain {executableName}.");
            }

            if (Directory.Exists(target))
            {
                Directory.Move(target, backup);
                targetMoved = true;
            }

            Directory.Move(staging, target);
            stagingActivated = true;

            startUpdatedProcess(Path.Combine(target, executableName));
            started = true;
        }
        catch
        {
            if (stagingActivated && Directory.Exists(target))
            {
                Directory.Delete(target, recursive: true);
            }

            if (targetMoved && Directory.Exists(backup))
            {
                Directory.Move(backup, target);
            }

            throw;
        }
        finally
        {
            if (Directory.Exists(staging))
            {
                Directory.Delete(staging, recursive: true);
            }

            if (started && Directory.Exists(backup))
            {
                try
                {
                    Directory.Delete(backup, recursive: true);
                }
                catch
                {
                    // The update is active; a stale backup is safer than rollback here.
                }
            }
        }
    }

    private static void CopyDirectory(string source, string destination, bool overwrite)
    {
        Directory.CreateDirectory(destination);
        foreach (var directory in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(source, directory);
            Directory.CreateDirectory(Path.Combine(destination, relative));
        }

        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(source, file);
            var destinationFile = Path.Combine(destination, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(destinationFile)!);
            File.Copy(file, destinationFile, overwrite);
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(destinationFile, File.GetUnixFileMode(file));
            }
        }
    }

    private static string NormalizeDirectory(string path) =>
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

    private static bool PathsEqual(string left, string right) =>
        string.Equals(
            NormalizeDirectory(left),
            NormalizeDirectory(right),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    private static string ExtractArchive(
        string archive,
        string payload,
        string extension,
        string executableName)
    {
        if (extension == ".zip")
        {
            ZipFile.ExtractToDirectory(archive, payload);
        }
        else
        {
            Directory.CreateDirectory(payload);
            using var compressed = File.OpenRead(archive);
            using var gzip = new GZipStream(compressed, CompressionMode.Decompress);
            TarFile.ExtractToDirectory(gzip, payload, overwriteFiles: false);
        }

        var executable = Path.Combine(payload, executableName);
        if (!File.Exists(executable))
        {
            throw new InvalidDataException($"The update archive does not contain {executableName}.");
        }

        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(executable, File.GetUnixFileMode(executable) | UnixFileMode.UserExecute);
        }

        return executable;
    }

    private static HttpClient CreateHttpClient()
    {
        var client = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("SharpEmu", "0.0.1"));
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        return client;
    }

    private static PlatformInfo CurrentPlatform()
    {
        if (RuntimeInformation.ProcessArchitecture != Architecture.X64)
        {
            throw new PlatformNotSupportedException("SharpEmu releases require an x64 process.");
        }

        if (OperatingSystem.IsWindows()) return new("win-x64", ".zip", "SharpEmu.exe");
        if (OperatingSystem.IsLinux()) return new("linux-x64", ".tar.gz", "SharpEmu");
        if (OperatingSystem.IsMacOS()) return new("osx-x64", ".tar.gz", "SharpEmu");
        throw new PlatformNotSupportedException();
    }

    private sealed record PlatformInfo(string Rid, string Extension, string ExecutableName);
}
