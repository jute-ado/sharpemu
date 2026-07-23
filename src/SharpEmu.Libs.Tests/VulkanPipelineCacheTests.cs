// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.VideoOut;
using Xunit;

namespace SharpEmu.Libs.Tests;

public sealed class VulkanPipelineCacheTests
{
    [Fact]
    public void GuestSubmissionFailureContextIdentifiesQueueSequenceAndWork()
    {
        var context = VulkanVideoPresenter.FormatGuestSubmissionContext(
            new VulkanGuestQueueIdentity("graphics.main", 42),
            workSequence: 173,
            ["SharpEmu draw sig=ABC", "SharpEmu compute sig=DEF"]);

        Assert.Equal(
            "queue=graphics.main submission=42 work_sequence=173 " +
            "work='SharpEmu draw sig=ABC | SharpEmu compute sig=DEF'",
            context);
    }

    [Fact]
    public void DefaultCachePathIsStableAndScopedPerGame()
    {
        var root = Path.Combine(Path.GetTempPath(), "sharpemu-pipeline-cache-tests");
        var firstGame = Path.Combine(root, "Games", "Dreaming Sarah", "app0");
        var secondGame = Path.Combine(root, "Games", "Void Terrarium", "app0");

        var firstPath = VulkanVideoPresenter.GetDefaultPipelineCachePath(root, firstGame);
        var repeatedPath = VulkanVideoPresenter.GetDefaultPipelineCachePath(
            root,
            firstGame + Path.DirectorySeparatorChar);
        var secondPath = VulkanVideoPresenter.GetDefaultPipelineCachePath(root, secondGame);

        Assert.Equal(firstPath, repeatedPath);
        Assert.NotEqual(firstPath, secondPath);
        Assert.StartsWith(
            Path.Combine(root, "SharpEmu", "vulkan-pipeline-cache"),
            firstPath);
        Assert.DoesNotContain("Dreaming Sarah", firstPath, StringComparison.Ordinal);
    }

    [Fact]
    public void PersistentCacheSizeIsBoundedAtLoadAndSaveLimit()
    {
        Assert.True(VulkanVideoPresenter.IsPersistentPipelineCacheSizeAllowed(0));
        Assert.True(VulkanVideoPresenter.IsPersistentPipelineCacheSizeAllowed(
            VulkanVideoPresenter.MaxPersistentPipelineCacheBytes));
        Assert.False(VulkanVideoPresenter.IsPersistentPipelineCacheSizeAllowed(-1));
        Assert.False(VulkanVideoPresenter.IsPersistentPipelineCacheSizeAllowed(
            VulkanVideoPresenter.MaxPersistentPipelineCacheBytes + 1));
    }
}
