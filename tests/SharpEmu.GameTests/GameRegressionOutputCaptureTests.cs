// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using Xunit;

namespace SharpEmu.GameTests;

public sealed class GameRegressionOutputCaptureTests
{
    [Fact]
    public async Task CaptureBoundsArtifactButAnalyzesAllOutput()
    {
        var expectations = CreateExpectations();
        var artifactPath = Path.GetTempFileName();
        try
        {
            using var reader = new StringReader(
                "filler that fills the artifact" + Environment.NewLine +
                "[LOADER][WARN] Import#9000 result: " +
                "ORBIS_GEN2_ERROR_TIMED_OUT (knownNid)" + Environment.NewLine +
                "[LOADER][WARN] Import#9001 unresolved: nid=newNid" +
                Environment.NewLine +
                "required-after-limit" + Environment.NewLine +
                "forbidden-after-limit" + Environment.NewLine +
                "[LOADER][TRACE] vk.present_progress frame=60" +
                Environment.NewLine +
                "[LOADER][TRACE] vk.present_progress frame=90" +
                Environment.NewLine +
                "vk.presented_guest_image frame=77 " +
                "fingerprint=0x0123456789ABCDEF " +
                "nonblack_pixels=12 distinct_colors=9");

            var analysis = await GameRegressionOutputCapture.CaptureAsync(
                reader,
                artifactPath,
                expectations,
                maximumArtifactCharacters: 16);

            Assert.True(analysis.ArtifactTruncated);
            Assert.Equal(9001, analysis.MaximumObservedImportDispatch);
            Assert.Equal(90, analysis.MaximumPresentedGuestFrame);
            Assert.Equal(2, analysis.TotalImportWarnings);
            Assert.Equal(1, analysis.KnownImportWarnings);
            Assert.Equal(1, analysis.UnexpectedImportWarnings);
            Assert.True(analysis.Contains(
                "required-after-limit",
                StringComparison.Ordinal));
            Assert.True(analysis.Contains(
                "forbidden-after-limit",
                StringComparison.Ordinal));
            Assert.Contains(
                "vk.presented_guest_image frame=77",
                analysis.RelevantOutput,
                StringComparison.Ordinal);

            var artifact = await File.ReadAllTextAsync(artifactPath);
            Assert.DoesNotContain(
                "required-after-limit",
                artifact,
                StringComparison.Ordinal);
            Assert.Contains(
                "Output artifact truncated",
                artifact,
                StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(artifactPath);
        }
    }

    [Fact]
    public async Task CombinedCapturePreservesBothStreams()
    {
        var expectations = CreateExpectations();
        var firstPath = Path.GetTempFileName();
        var secondPath = Path.GetTempFileName();
        try
        {
            using var firstReader = new StringReader(
                "[LOADER][WARN] Import#10 unresolved: nid=first" +
                Environment.NewLine +
                "[LOADER][TRACE] vk.present_progress frame=30" +
                Environment.NewLine +
                "required-after-limit");
            using var secondReader = new StringReader(
                "[LOADER][WARN] Import#20 result: " +
                "ORBIS_GEN2_ERROR_TIMED_OUT (knownNid)" +
                Environment.NewLine +
                "[LOADER][TRACE] vk.present_progress frame=120" +
                Environment.NewLine +
                "forbidden-after-limit");
            var first = await GameRegressionOutputCapture.CaptureAsync(
                firstReader,
                firstPath,
                expectations);
            var second = await GameRegressionOutputCapture.CaptureAsync(
                secondReader,
                secondPath,
                expectations);

            var combined = GameOutputAnalysis.Combine(first, second);

            Assert.Equal(20, combined.MaximumObservedImportDispatch);
            Assert.Equal(120, combined.MaximumPresentedGuestFrame);
            Assert.Equal(2, combined.TotalImportWarnings);
            Assert.Equal(1, combined.KnownImportWarnings);
            Assert.Equal(1, combined.UnexpectedImportWarnings);
            Assert.True(combined.Contains(
                "required-after-limit",
                StringComparison.Ordinal));
            Assert.True(combined.Contains(
                "forbidden-after-limit",
                StringComparison.Ordinal));
        }
        finally
        {
            File.Delete(firstPath);
            File.Delete(secondPath);
        }
    }

    private static GameRegressionExpectations CreateExpectations() =>
        new()
        {
            RequiredOutputSubstrings = ["required-after-limit"],
            ForbiddenOutputSubstrings = ["forbidden-after-limit"],
            KnownImportWarnings =
            [
                new ImportWarningExpectation
                {
                    Nid = "knownNid",
                    Result = "ORBIS_GEN2_ERROR_TIMED_OUT",
                },
            ],
            RequiredPresentedGuestImage = new PresentedGuestImageExpectation
            {
                Frame = 77,
                MinimumNonBlackPixels = 1,
            },
        };
}
