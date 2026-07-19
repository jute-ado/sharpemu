// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.Kernel;
using Xunit;

namespace SharpEmu.Libs.Tests;

public sealed class GuestPathResolverTests
{
    private static readonly string Root = Path.Combine(
        Path.GetTempPath(),
        "SharpEmu.Tests",
        Guid.NewGuid().ToString("N"));

    static GuestPathResolverTests()
    {
        Environment.SetEnvironmentVariable("SHARPEMU_APP0_DIR", Path.Combine(Root, "app0"));
        Environment.SetEnvironmentVariable("SHARPEMU_TEMP0_DIR", Path.Combine(Root, "temp0"));
        Environment.SetEnvironmentVariable("SHARPEMU_DOWNLOAD0_DIR", Path.Combine(Root, "download0"));
        Environment.SetEnvironmentVariable("SHARPEMU_HOSTAPP_DIR", Path.Combine(Root, "hostapp"));
        Environment.SetEnvironmentVariable("SHARPEMU_DEVLOG_APP_DIR", Path.Combine(Root, "devlog"));
    }

    [Theory]
    [InlineData("/temp0/../../escape.txt", "temp0")]
    [InlineData("/temp0\\..\\..\\escape.txt", "temp0")]
    [InlineData("/download0/../escape.txt", "download0")]
    [InlineData("download0/../../escape.txt", "download0")]
    [InlineData("/hostapp/../escape.txt", "hostapp")]
    [InlineData("devlog/app/../../../escape.txt", "devlog")]
    [InlineData("/app0/../../escape.txt", "app0")]
    [InlineData("$/../../escape.txt", "app0")]
    public void ClampsTraversalAtKnownMountRoots(string guestPath, string rootName)
    {
        Assert.True(KernelMemoryCompatExports.TryResolveGuestPath(guestPath, out var hostPath));
        Assert.Equal(Path.Combine(Root, rootName, "escape.txt"), hostPath);
    }

    [Theory]
    [InlineData("C:\\Windows\\win.ini")]
    [InlineData("C:drive-relative.txt")]
    [InlineData("\\\\server\\share\\file.bin")]
    [InlineData("\\\\?\\C:\\Windows\\win.ini")]
    [InlineData("/etc/passwd")]
    public void RejectsHostAbsolutePaths(string guestPath)
    {
        Assert.False(KernelMemoryCompatExports.TryResolveGuestPath(guestPath, out _));
    }

    [Theory]
    [InlineData("/temp0/cache/data.bin", "temp0", "cache/data.bin")]
    [InlineData("/download0/content/update.bin", "download0", "content/update.bin")]
    [InlineData("/hostapp/assets/image.bin", "hostapp", "assets/image.bin")]
    [InlineData("/devlog/app/session/log.txt", "devlog", "session/log.txt")]
    [InlineData("/app0/sce_sys/param.json", "app0", "sce_sys/param.json")]
    [InlineData("$/sce_module/module.prx", "app0", "sce_module/module.prx")]
    [InlineData("relative/file.bin", "app0", "relative/file.bin")]
    public void ResolvesSafePathsInsideTheirMount(
        string guestPath,
        string rootName,
        string relativePath)
    {
        Assert.True(KernelMemoryCompatExports.TryResolveGuestPath(guestPath, out var hostPath));

        var expected = Path.GetFullPath(Path.Combine(
            Root,
            rootName,
            relativePath.Replace('/', Path.DirectorySeparatorChar)));
        Assert.Equal(expected, hostPath);
    }

    [Fact]
    public void RegisteredMountsEnforceTheSameContainmentBoundary()
    {
        var mountPoint = "/savedata-" + Guid.NewGuid().ToString("N");
        var mountRoot = Path.Combine(Root, "registered");
        KernelMemoryCompatExports.RegisterGuestPathMount(mountPoint, mountRoot);

        Assert.True(KernelMemoryCompatExports.TryResolveGuestPath(
            mountPoint + "/slot/save.bin",
            out var safePath));
        Assert.Equal(
            Path.GetFullPath(Path.Combine(mountRoot, "slot", "save.bin")),
            safePath);
        Assert.True(KernelMemoryCompatExports.TryResolveGuestPath(
            mountPoint + "/../../escape.bin",
            out var clampedPath));
        Assert.Equal(Path.Combine(mountRoot, "escape.bin"), clampedPath);
    }

    [Fact]
    public void App0ResolutionFollowsCurrentRuntimeBinding()
    {
        var original = Environment.GetEnvironmentVariable("SHARPEMU_APP0_DIR");
        var firstRoot = Path.Combine(Root, "first-app0");
        var secondRoot = Path.Combine(Root, "second-app0");
        try
        {
            Environment.SetEnvironmentVariable("SHARPEMU_APP0_DIR", firstRoot);
            Assert.True(KernelMemoryCompatExports.TryResolveGuestPath(
                "/app0/content.bin",
                out var firstPath));

            Environment.SetEnvironmentVariable("SHARPEMU_APP0_DIR", secondRoot);
            Assert.True(KernelMemoryCompatExports.TryResolveGuestPath(
                "/app0/content.bin",
                out var secondPath));

            Assert.Equal(
                Path.Combine(Path.GetFullPath(firstRoot), "content.bin"),
                firstPath);
            Assert.Equal(
                Path.Combine(Path.GetFullPath(secondRoot), "content.bin"),
                secondPath);
        }
        finally
        {
            Environment.SetEnvironmentVariable("SHARPEMU_APP0_DIR", original);
        }
    }

    [Fact]
    public void DefaultWritableMountsFollowCurrentRuntimeBinding()
    {
        var originalApp0 = Environment.GetEnvironmentVariable("SHARPEMU_APP0_DIR");
        var originalTemp0 = Environment.GetEnvironmentVariable("SHARPEMU_TEMP0_DIR");
        var originalDownload0 = Environment.GetEnvironmentVariable("SHARPEMU_DOWNLOAD0_DIR");
        var suffix = Guid.NewGuid().ToString("N");
        var firstName = $"first-app0-{suffix}";
        var secondName = $"second-app0-{suffix}";
        var firstRoot = Path.Combine(Root, firstName);
        var secondRoot = Path.Combine(Root, secondName);
        var writableRoots = new[]
        {
            Path.Combine(Path.GetTempPath(), "SharpEmu", firstName),
            Path.Combine(Path.GetTempPath(), "SharpEmu", secondName),
        };

        try
        {
            Environment.SetEnvironmentVariable("SHARPEMU_TEMP0_DIR", null);
            Environment.SetEnvironmentVariable("SHARPEMU_DOWNLOAD0_DIR", null);
            Environment.SetEnvironmentVariable("SHARPEMU_APP0_DIR", firstRoot);
            Assert.True(KernelMemoryCompatExports.TryResolveGuestPath(
                "/temp0/cache.bin",
                out var firstTempPath));
            Assert.True(KernelMemoryCompatExports.TryResolveGuestPath(
                "/download0/cache.bin",
                out var firstDownloadPath));

            Environment.SetEnvironmentVariable("SHARPEMU_APP0_DIR", secondRoot);
            Assert.True(KernelMemoryCompatExports.TryResolveGuestPath(
                "/temp0/cache.bin",
                out var secondTempPath));
            Assert.True(KernelMemoryCompatExports.TryResolveGuestPath(
                "/download0/cache.bin",
                out var secondDownloadPath));

            Assert.Equal(
                Path.Combine(writableRoots[0], "temp0", "cache.bin"),
                firstTempPath);
            Assert.Equal(
                Path.Combine(writableRoots[0], "download0", "cache.bin"),
                firstDownloadPath);
            Assert.Equal(
                Path.Combine(writableRoots[1], "temp0", "cache.bin"),
                secondTempPath);
            Assert.Equal(
                Path.Combine(writableRoots[1], "download0", "cache.bin"),
                secondDownloadPath);
        }
        finally
        {
            Environment.SetEnvironmentVariable("SHARPEMU_APP0_DIR", originalApp0);
            Environment.SetEnvironmentVariable("SHARPEMU_TEMP0_DIR", originalTemp0);
            Environment.SetEnvironmentVariable("SHARPEMU_DOWNLOAD0_DIR", originalDownload0);
            foreach (var writableRoot in writableRoots)
            {
                if (Directory.Exists(writableRoot))
                {
                    Directory.Delete(writableRoot, recursive: true);
                }
            }
        }
    }

    [Fact]
    public void RejectsSymlinksBelowAMountRoot()
    {
        var tempRoot = Path.Combine(Root, "temp0");
        var outsideRoot = Path.Combine(Root, "outside");
        var link = Path.Combine(tempRoot, "link");
        Directory.CreateDirectory(tempRoot);
        Directory.CreateDirectory(outsideRoot);
        try
        {
            Directory.CreateSymbolicLink(link, outsideRoot);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            return;
        }

        Assert.False(KernelMemoryCompatExports.TryResolveGuestPath(
            "/temp0/link/escape.bin",
            out _));
    }
}
