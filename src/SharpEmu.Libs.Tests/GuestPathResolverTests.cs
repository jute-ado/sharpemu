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
    [InlineData("/temp0/../../escape.txt")]
    [InlineData("/temp0\\..\\..\\escape.txt")]
    [InlineData("/download0/../escape.txt")]
    [InlineData("download0/../../escape.txt")]
    [InlineData("/hostapp/../escape.txt")]
    [InlineData("devlog/app/../../../escape.txt")]
    [InlineData("/app0/../../escape.txt")]
    [InlineData("$/../../escape.txt")]
    public void RejectsPathsThatEscapeKnownMounts(string guestPath)
    {
        Assert.False(KernelMemoryCompatExports.TryResolveGuestPath(guestPath, out _));
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
        Assert.False(KernelMemoryCompatExports.TryResolveGuestPath(
            mountPoint + "/../../escape.bin",
            out _));
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
