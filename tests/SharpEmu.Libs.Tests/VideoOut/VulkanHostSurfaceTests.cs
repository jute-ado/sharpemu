// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.VideoOut;
using Xunit;

namespace SharpEmu.Libs.Tests.VideoOut;

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class VulkanHostSurfaceCollection
{
    public const string Name = "VulkanHostSurface";
}

[Collection(VulkanHostSurfaceCollection.Name)]
public sealed class VulkanHostSurfaceTests : IDisposable
{
    [Fact]
    public void Win32DescriptorRoundTripsAcrossProcessBoundary()
    {
        using var source = new VulkanHostSurface(
            VulkanHostSurfaceKind.Win32,
            windowHandle: (nint)0x1234,
            displayHandle: (nint)0x5678);
        source.UpdatePixelSize(1920, 1080);

        Assert.True(source.TryGetChildProcessDescriptor(out var descriptor));
        Assert.True(
            VulkanHostSurface.TryCreateChildProcessSurface(
                descriptor,
                out var restored,
                out var error),
            error);
        using (restored)
        {
            Assert.NotNull(restored);
            Assert.Equal(VulkanHostSurfaceKind.Win32, restored.Kind);
            Assert.Equal((nint)0x1234, restored.WindowHandle);
            Assert.Equal((nint)0x5678, restored.DisplayHandle);
            Assert.Equal(1920, restored.PixelWidth);
            Assert.Equal(1080, restored.PixelHeight);
        }
    }

    [Theory]
    [InlineData("")]
    [InlineData("win32")]
    [InlineData("win32:0:1280:720:0")]
    [InlineData("win32:1234:0:720:0")]
    [InlineData("unsupported:1234:1280:720:0")]
    public void InvalidDescriptorIsRejected(string descriptor)
    {
        Assert.False(
            VulkanHostSurface.TryCreateChildProcessSurface(
                descriptor,
                out var surface,
                out var error));
        Assert.Null(surface);
        Assert.False(string.IsNullOrWhiteSpace(error));
    }

    [Fact]
    public void PresenterAcceptsOnlyOneDetachedHostSurface()
    {
        using var first = new VulkanHostSurface(
            VulkanHostSurfaceKind.Win32,
            (nint)0x1111);
        using var second = new VulkanHostSurface(
            VulkanHostSurfaceKind.Win32,
            (nint)0x2222);

        Assert.True(VulkanVideoHost.TryAttachSurface(first));
        Assert.True(VulkanVideoHost.IsEmbedded);
        Assert.False(VulkanVideoHost.TryAttachSurface(second));

        VulkanVideoHost.DetachSurface(second);
        Assert.True(VulkanVideoHost.IsEmbedded);
        VulkanVideoHost.DetachSurface(first);
        Assert.False(VulkanVideoHost.IsEmbedded);
    }

    public void Dispose()
    {
        VulkanVideoHost.RequestClose();
    }
}
