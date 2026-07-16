// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Diagnostics;
using System.Text.Json;
using Xunit;

namespace SharpEmu.GameTests;

public sealed class GameRegressionHarnessTests
{
    [Fact]
    public void ManifestRequiresSupportedSchema()
    {
        var path = WriteTemporaryManifest(
            """
            {
              "schemaVersion": 2,
              "cases": [
                {
                  "name": "sample",
                  "executablePath": "eboot.bin",
                  "expectations": {
                    "allowedResults": [ "IMAGE_LOADED" ]
                  }
                }
              ]
            }
            """);

        try
        {
            var error = Assert.Throws<InvalidDataException>(
                () => GameRegressionManifest.Load(path));

            Assert.Contains("schema 2", error.Message, StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void ExecutionTimeoutCanBeAnExpectedSurvivalResult()
    {
        var testCase = CreateExecutionCase();
        using var report = JsonDocument.Parse(
            """
            {
              "result": {
                "name": "EXECUTION_TIMED_OUT"
              },
              "moduleInitializers": [],
              "moduleLoadFailures": []
            }
            """);

        GameRegressionRunner.ValidateReport(
            testCase,
            report.RootElement,
            "synthetic-report.json");
    }

    [Fact]
    public void ExecutionTimeoutWithoutPreparedMilestonesFails()
    {
        var testCase = CreateExecutionCase();
        using var report = JsonDocument.Parse(
            """
            {
              "result": {
                "name": "EXECUTION_TIMED_OUT"
              },
              "moduleInitializers": null,
              "moduleLoadFailures": null
            }
            """);

        var error = Assert.Throws<InvalidOperationException>(
            () => GameRegressionRunner.ValidateReport(
                testCase,
                report.RootElement,
                "synthetic-report.json"));

        Assert.Contains(
            "status was not captured",
            error.Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public void ExecutionProgressUsesLargestObservedImportDispatch()
    {
        var testCase = CreateExecutionCase(
            minimumObservedImportDispatches: 100_000);
        using var report = JsonDocument.Parse(
            """
            {
              "result": {
                "name": "EXECUTION_TIMED_OUT"
              },
              "moduleInitializers": [],
              "moduleLoadFailures": []
            }
            """);

        GameRegressionRunner.ValidateReport(
            testCase,
            report.RootElement,
            "synthetic-report.json",
            """
            [LOADER][TRACE] Import#1000: libKernel:first (first)
            [LOADER][TRACE] Import#120000: libSceAgc:later (later)
            [LOADER][WARN] Import#119999 result: -1 (later)
            """);

        Assert.Equal(
            120_000,
            GameRegressionRunner.GetMaximumObservedImportDispatch(
                "Import#7 Import#120000 Import#42"));
    }

    [Fact]
    public void ExecutionFailsWhenImportProgressRegresses()
    {
        var testCase = CreateExecutionCase(
            minimumObservedImportDispatches: 100_000);
        using var report = JsonDocument.Parse(
            """
            {
              "result": {
                "name": "EXECUTION_TIMED_OUT"
              },
              "moduleInitializers": [],
              "moduleLoadFailures": []
            }
            """);

        var error = Assert.Throws<InvalidOperationException>(
            () => GameRegressionRunner.ValidateReport(
                testCase,
                report.RootElement,
                "synthetic-report.json",
                "[LOADER][TRACE] Import#99999: libKernel:test (test)"));

        Assert.Contains(
            "99999 was below 100000",
            error.Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public void ExecutionCanRequireGpuOutputMilestone()
    {
        var testCase = CreateExecutionCase(
            requiredOutputSubstrings:
            [
                "Vulkan VideoOut presented guest frame",
            ]);
        using var report = JsonDocument.Parse(
            """
            {
              "result": {
                "name": "EXECUTION_TIMED_OUT"
              },
              "moduleInitializers": [],
              "moduleLoadFailures": []
            }
            """);

        GameRegressionRunner.ValidateReport(
            testCase,
            report.RootElement,
            "synthetic-report.json",
            "[LOADER][INFO] Vulkan VideoOut presented guest frame: image=0x1234");
    }

    [Fact]
    public void ExecutionFailsWhenRequiredOutputMilestoneIsMissing()
    {
        var testCase = CreateExecutionCase(
            requiredOutputSubstrings:
            [
                "Vulkan VideoOut presented guest frame",
            ]);
        using var report = JsonDocument.Parse(
            """
            {
              "result": {
                "name": "EXECUTION_TIMED_OUT"
              },
              "moduleInitializers": [],
              "moduleLoadFailures": []
            }
            """);

        var error = Assert.Throws<InvalidOperationException>(
            () => GameRegressionRunner.ValidateReport(
                testCase,
                report.RootElement,
                "synthetic-report.json",
                "[LOADER][INFO] Vulkan VideoOut ready"));

        Assert.Contains(
            "presented guest frame",
            error.Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public void ExecutionPassesWhenForbiddenOutputIsAbsent()
    {
        var testCase = CreateExecutionCase(
            forbiddenOutputSubstrings:
            [
                "DEVICE_NOT_CONNECTED",
            ]);
        using var report = JsonDocument.Parse(
            """
            {
              "result": {
                "name": "EXECUTION_TIMED_OUT"
              },
              "moduleInitializers": [],
              "moduleLoadFailures": []
            }
            """);

        GameRegressionRunner.ValidateReport(
            testCase,
            report.RootElement,
            "synthetic-report.json",
            "[LOADER][INFO] Controls: keyboard fallback active");
    }

    [Fact]
    public void ExecutionFailsWhenForbiddenOutputIsObserved()
    {
        var testCase = CreateExecutionCase(
            forbiddenOutputSubstrings:
            [
                "DEVICE_NOT_CONNECTED",
            ]);
        using var report = JsonDocument.Parse(
            """
            {
              "result": {
                "name": "EXECUTION_TIMED_OUT"
              },
              "moduleInitializers": [],
              "moduleLoadFailures": []
            }
            """);

        var error = Assert.Throws<InvalidOperationException>(
            () => GameRegressionRunner.ValidateReport(
                testCase,
                report.RootElement,
                "synthetic-report.json",
                "[LOADER][WARN] scePadOpen: DEVICE_NOT_CONNECTED"));

        Assert.Contains(
            "forbidden output was observed",
            error.Message,
            StringComparison.Ordinal);
        Assert.Contains(
            "DEVICE_NOT_CONNECTED",
            error.Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public void ExecutionCanRequireCapturedVideoOutFrameFingerprint()
    {
        var testCase = CreateExecutionCase(
            requiredVideoOutFrameFingerprints:
            [
                "0x7238bf0c5c979715",
            ]);
        using var report = JsonDocument.Parse(
            """
            {
              "result": {
                "name": "EXECUTION_TIMED_OUT"
              },
              "moduleInitializers": [],
              "moduleLoadFailures": []
            }
            """);

        GameRegressionRunner.ValidateReport(
            testCase,
            report.RootElement,
            "synthetic-report.json",
            """
            [LOADER][TRACE] videoout.dump_frame path=frame.bmp
            fingerprint=0x7238BF0C5C979715
            """);

        Assert.True(
            GameRegressionRunner.TryNormalizeFrameFingerprint(
                "7238bf0c5c979715",
                out var normalized));
        Assert.Equal("7238BF0C5C979715", normalized);
    }

    [Fact]
    public void ExecutionFailsWhenCapturedFrameFingerprintChanges()
    {
        var testCase = CreateExecutionCase(
            requiredVideoOutFrameFingerprints:
            [
                "7238BF0C5C979715",
            ]);
        using var report = JsonDocument.Parse(
            """
            {
              "result": {
                "name": "EXECUTION_TIMED_OUT"
              },
              "moduleInitializers": [],
              "moduleLoadFailures": []
            }
            """);

        var error = Assert.Throws<InvalidOperationException>(
            () => GameRegressionRunner.ValidateReport(
                testCase,
                report.RootElement,
                "synthetic-report.json",
                "fingerprint=0x0000000000000000"));

        Assert.Contains(
            "0x7238BF0C5C979715",
            error.Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public void ExecutionCanRequireActualPresentedGuestImageFingerprint()
    {
        var testCase = CreateExecutionCase(
            requiredPresentedGuestImage: new()
            {
                Frame = 1,
                Fingerprint = "0x7f046b95716664f5",
            });
        using var report = JsonDocument.Parse(
            """
            {
              "result": {
                "name": "EXECUTION_TIMED_OUT"
              },
              "moduleInitializers": [],
              "moduleLoadFailures": []
            }
            """);

        GameRegressionRunner.ValidateReport(
            testCase,
            report.RootElement,
            "synthetic-report.json",
            """
            [LOADER][TRACE] vk.presented_guest_image frame=1 fingerprint=0x7F046B95716664F5
            addr=0x0000000001260000 size=3840x2160
            """);
    }

    [Fact]
    public void ExecutionRejectsDifferentPresentedGuestImageFingerprint()
    {
        var testCase = CreateExecutionCase(
            requiredPresentedGuestImage: new()
            {
                Frame = 30,
                Fingerprint = "7F046B95716664F5",
            });
        using var report = JsonDocument.Parse(
            """
            {
              "result": {
                "name": "EXECUTION_TIMED_OUT"
              },
              "moduleInitializers": [],
              "moduleLoadFailures": []
            }
            """);

        var error = Assert.Throws<InvalidOperationException>(
            () => GameRegressionRunner.ValidateReport(
                testCase,
                report.RootElement,
                "synthetic-report.json",
                "vk.presented_guest_image frame=1 " +
                "fingerprint=0x7F046B95716664F5"));

        Assert.Contains(
            "frame 30",
            error.Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public void VideoOutCaptureEnvironmentIsDerivedOnlyFromExpectations()
    {
        var startInfo = new ProcessStartInfo();
        startInfo.Environment["SHARPEMU_DUMP_VIDEOOUT"] = "stale";
        startInfo.Environment["SHARPEMU_LOG_VIDEOOUT"] = "stale";
        startInfo.Environment[
            "SHARPEMU_CAPTURE_PRESENTED_GUEST_IMAGE_FRAME"] = "stale";
        startInfo.Environment[
            "SHARPEMU_PRESENTED_GUEST_IMAGE_DUMP_DIR"] = "stale";
        var artifactDirectory = Path.Combine(
            Path.GetTempPath(),
            "sharpemu-presented-test");

        GameRegressionRunner.ConfigureVideoOutEnvironment(
            startInfo,
            new GameRegressionExpectations(),
            artifactDirectory);

        Assert.False(startInfo.Environment.ContainsKey("SHARPEMU_DUMP_VIDEOOUT"));
        Assert.False(startInfo.Environment.ContainsKey("SHARPEMU_LOG_VIDEOOUT"));
        Assert.False(
            startInfo.Environment.ContainsKey(
                "SHARPEMU_CAPTURE_PRESENTED_GUEST_IMAGE_FRAME"));
        Assert.False(
            startInfo.Environment.ContainsKey(
                "SHARPEMU_PRESENTED_GUEST_IMAGE_DUMP_DIR"));

        GameRegressionRunner.ConfigureVideoOutEnvironment(
            startInfo,
            new GameRegressionExpectations
            {
                RequiredVideoOutFrameFingerprints = ["0123456789ABCDEF"],
                RequiredPresentedGuestImage = new()
                {
                    Frame = 30,
                    Fingerprint = "0123456789ABCDEF",
                },
            },
            artifactDirectory);

        Assert.Equal("1", startInfo.Environment["SHARPEMU_DUMP_VIDEOOUT"]);
        Assert.Equal("1", startInfo.Environment["SHARPEMU_LOG_VIDEOOUT"]);
        Assert.Equal(
            "30",
            startInfo.Environment[
                "SHARPEMU_CAPTURE_PRESENTED_GUEST_IMAGE_FRAME"]);
        Assert.Equal(
            Path.GetFullPath(artifactDirectory),
            startInfo.Environment[
                "SHARPEMU_PRESENTED_GUEST_IMAGE_DUMP_DIR"]);
    }

    [Fact]
    public void EarlyCpuTrapFailsExecutionSurvivalContract()
    {
        var testCase = CreateExecutionCase();
        using var report = JsonDocument.Parse(
            """
            {
              "result": {
                "name": "ORBIS_GEN2_ERROR_CPU_TRAP"
              },
              "moduleInitializers": [
                {
                  "result": {
                    "succeeded": true
                  }
                }
              ],
              "moduleLoadFailures": []
            }
            """);

        var error = Assert.Throws<InvalidOperationException>(
            () => GameRegressionRunner.ValidateReport(
                testCase,
                report.RootElement,
                "synthetic-report.json"));

        Assert.Contains(
            "ORBIS_GEN2_ERROR_CPU_TRAP",
            error.Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public void GameCaseRequiresExplicitAllowedResults()
    {
        var testCase = new GameRegressionCase
        {
            Name = "missing expectation",
            ExecutablePath = "eboot.bin",
            ExpectedBundleSha256 =
                "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef",
        };

        var error = Assert.Throws<InvalidDataException>(
            () => GameRegressionRunner.ValidateCase(testCase));

        Assert.Contains(
            "allowedResults",
            error.Message,
            StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not-a-sha256")]
    [InlineData("000000000000000000000000000000000000000000000000000000000000000")]
    public void GameCaseRequiresExactBundleFingerprint(string? fingerprint)
    {
        var testCase = CreateExecutionCase(
            expectedBundleSha256: fingerprint);

        var error = Assert.Throws<InvalidDataException>(
            () => GameRegressionRunner.ValidateCase(testCase));

        Assert.Contains(
            "expectedBundleSha256",
            error.Message,
            StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(0, "0123456789ABCDEF")]
    [InlineData(-1, "0123456789ABCDEF")]
    [InlineData(1, "not-a-fingerprint")]
    public void GameCaseRequiresValidPresentedImageMilestone(
        long frame,
        string fingerprint)
    {
        var testCase = CreateExecutionCase(
            requiredPresentedGuestImage: new()
            {
                Frame = frame,
                Fingerprint = fingerprint,
            });

        var error = Assert.Throws<InvalidDataException>(
            () => GameRegressionRunner.ValidateCase(testCase));

        Assert.Contains(
            "requiredPresentedGuestImage",
            error.Message,
            StringComparison.Ordinal);
    }

    private static GameRegressionCase CreateExecutionCase(
        long? minimumObservedImportDispatches = null,
        string[]? requiredOutputSubstrings = null,
        string[]? forbiddenOutputSubstrings = null,
        string[]? requiredVideoOutFrameFingerprints = null,
        PresentedGuestImageExpectation? requiredPresentedGuestImage = null,
        string? expectedBundleSha256 =
            "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef") => new()
    {
        Name = "execution survival",
        ExecutablePath = "eboot.bin",
        Mode = "execution",
        TimeoutSeconds = 20,
        ExpectedBundleSha256 = expectedBundleSha256,
        Expectations = new GameRegressionExpectations
        {
            AllowedResults =
            [
                "EXECUTION_TIMED_OUT",
                "ORBIS_GEN2_OK",
            ],
            ForbiddenResults =
            [
                "ORBIS_GEN2_ERROR_CPU_TRAP",
                "HOST_ERROR",
            ],
            RequireNoModuleLoadFailures = true,
            RequireSuccessfulModuleInitializers = true,
            MinimumObservedImportDispatches =
                minimumObservedImportDispatches,
            RequiredOutputSubstrings =
                requiredOutputSubstrings ?? [],
            ForbiddenOutputSubstrings =
                forbiddenOutputSubstrings ?? [],
            RequiredVideoOutFrameFingerprints =
                requiredVideoOutFrameFingerprints ?? [],
            RequiredPresentedGuestImage =
                requiredPresentedGuestImage,
        },
    };

    private static string WriteTemporaryManifest(string contents)
    {
        var path = Path.Combine(
            Path.GetTempPath(),
            $"sharpemu-game-manifest-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, contents);
        return path;
    }
}
