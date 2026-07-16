// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Diagnostics;
using SharpEmu.Libs.Gpu;
using SharpEmu.Libs.VideoOut;
using SharpEmu.ShaderCompiler.Vulkan;
using Xunit;

namespace SharpEmu.GpuTests;

/// <summary>
/// End-to-end regressions for resource ordering inside the canonical Vulkan
/// presenter.
/// </summary>
public sealed class RenderTargetSamplingTests
{
    private const uint Width = 32;
    private const uint Height = 32;
    private const uint Rgba8DataFormat = 10;
    private const uint UnormNumberType = 0;
    private const ulong SourceAddress = 0x0010_0000;
    private const ulong FirstCopyAddress = 0x0020_0000;
    private const ulong CapturedCopyAddress = 0x0030_0000;

    /// <summary>
    /// Verifies that a render target rewritten after an earlier sample exposes
    /// the new pixels to the next sampling draw.
    /// </summary>
    [GpuConformanceFact]
    public void RewrittenRenderTarget_IsSampledByFollowingDraw()
    {
        var captureDirectory = Path.Combine(
            Path.GetTempPath(),
            $"sharpemu-gpu-{Guid.NewGuid():N}");
        Directory.CreateDirectory(captureDirectory);
        Environment.SetEnvironmentVariable(
            "SHARPEMU_CAPTURE_GUEST_IMAGE_WRITE",
            $"0x{CapturedCopyAddress:X}@1");
        Environment.SetEnvironmentVariable(
            "SHARPEMU_GUEST_IMAGE_DUMP_DIR",
            captureDirectory);

        try
        {
            VulkanVideoPresenter.EnsureStarted(Width, Height);
            VulkanVideoPresenter.HideSplashScreen();

            SubmitSolid(SourceAddress, red: 1f, green: 0f, blue: 0f);
            Assert.True(CopySourceTo(FirstCopyAddress));

            // Reuse the same guest render target, then sample it again. This
            // catches stale contents and missing render-to-sample ordering.
            SubmitSolid(SourceAddress, red: 0.125f, green: 0.5f, blue: 0.875f);
            Assert.True(CopySourceTo(CapturedCopyAddress));

            var capturePath = WaitForCapture(captureDirectory);
            var pixels = File.ReadAllBytes(capturePath);
            Assert.Equal(checked((int)(Width * Height * 4)), pixels.Length);
            AssertRgbaPixels(
                pixels,
                expectedRed: 32,
                expectedGreen: 128,
                expectedBlue: 223,
                expectedAlpha: 255);
        }
        finally
        {
            VulkanVideoPresenter.RequestClose();
            Environment.SetEnvironmentVariable(
                "SHARPEMU_CAPTURE_GUEST_IMAGE_WRITE",
                null);
            Environment.SetEnvironmentVariable(
                "SHARPEMU_GUEST_IMAGE_DUMP_DIR",
                null);
            Directory.Delete(captureDirectory, recursive: true);
        }
    }

    private static void SubmitSolid(
        ulong address,
        float red,
        float green,
        float blue)
    {
        VulkanVideoPresenter.SubmitOffscreenTranslatedDraw(
            SpirvFixedShaders.CreateSolidFragment(red, green, blue, alpha: 1f),
            [],
            [],
            attributeCount: 0,
            new GuestRenderTarget(
                address,
                Width,
                Height,
                Rgba8DataFormat,
                UnormNumberType));
    }

    private static bool CopySourceTo(ulong destinationAddress) =>
        VulkanVideoPresenter.TrySubmitGuestImageBlit(
            SourceAddress,
            Width,
            Height,
            Rgba8DataFormat,
            destinationAddress,
            Width,
            Height,
            Rgba8DataFormat);

    private static string WaitForCapture(string captureDirectory)
    {
        var stopwatch = Stopwatch.StartNew();
        while (stopwatch.Elapsed < TimeSpan.FromSeconds(30))
        {
            var captures = Directory.GetFiles(captureDirectory, "*.rgba");
            var previews = Directory.GetFiles(captureDirectory, "*.bmp");
            if (captures.Length != 0 && previews.Length != 0)
            {
                Assert.Single(captures);
                Assert.Single(previews);
                return captures[0];
            }

            Thread.Sleep(25);
        }

        throw new TimeoutException(
            "Vulkan presenter did not capture the sampled render target " +
            "within 30 seconds.");
    }

    private static void AssertRgbaPixels(
        byte[] pixels,
        byte expectedRed,
        byte expectedGreen,
        byte expectedBlue,
        byte expectedAlpha)
    {
        for (var offset = 0; offset < pixels.Length; offset += 4)
        {
            Assert.InRange(pixels[offset + 0], expectedRed - 1, expectedRed + 1);
            Assert.InRange(pixels[offset + 1], expectedGreen - 1, expectedGreen + 1);
            Assert.InRange(pixels[offset + 2], expectedBlue - 1, expectedBlue + 1);
            Assert.InRange(pixels[offset + 3], expectedAlpha - 1, expectedAlpha);
        }
    }
}
