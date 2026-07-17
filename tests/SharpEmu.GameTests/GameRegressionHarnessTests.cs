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
    public void UnsupportedModuleRelocationFailsExplicitCompatibilityContract()
    {
        var testCase = CreateExecutionCase(requireNoUnsupportedRelocations: true);
        using var report = JsonDocument.Parse(
            """
            {
              "result": { "name": "ORBIS_GEN2_OK" },
              "image": { "unsupportedRelocationTypes": [] },
              "modules": [
                {
                  "path": "sce_module/synthetic.prx",
                  "image": { "unsupportedRelocationTypes": [ 42 ] }
                }
              ],
              "moduleInitializers": [],
              "moduleLoadFailures": []
            }
            """);

        var error = Assert.Throws<InvalidOperationException>(
            () => GameRegressionRunner.ValidateReport(
                testCase,
                report.RootElement,
                "synthetic-report.json"));

        Assert.Contains("sce_module/synthetic.prx", error.Message, StringComparison.Ordinal);
        Assert.Contains("42", error.Message, StringComparison.Ordinal);
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
    public void ExecutionPassesWithinImportWarningBudget()
    {
        var testCase = CreateExecutionCase(maximumImportWarnings: 1);
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
            "[LOADER][WARN] Import#10 unresolved: nid=missing");
    }

    [Fact]
    public void ExecutionFailsWhenImportWarningBudgetIsExceeded()
    {
        var testCase = CreateExecutionCase(maximumImportWarnings: 1);
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
                """
                [LOADER][WARN] Import#10 result: -1 (first)
                [LOADER][TRACE] Import#11: expected trace
                [LOADER][WARN] Import#12 not implemented for generation Gen5
                """));

        Assert.Contains(
            "unexpected import warnings 2 exceeded maximum 1",
            error.Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public void KnownImportWarningSignaturesDoNotConsumeTheBudget()
    {
        var testCase = CreateExecutionCase(
            maximumImportWarnings: 0,
            knownImportWarnings:
            [
                new()
                {
                    Nid = "knownNid",
                    Result = "ORBIS_GEN2_ERROR_PERMISSION_DENIED",
                },
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
            [LOADER][WARN] Import#10 result: ORBIS_GEN2_ERROR_PERMISSION_DENIED (knownNid) rdi=0x1
            [LOADER][WARN] Import#12 result: ORBIS_GEN2_ERROR_PERMISSION_DENIED (knownNid) rdi=0x2
            """);
    }

    [Theory]
    [InlineData(
        "[LOADER][WARN] Import#10 result: ORBIS_GEN2_ERROR_NOT_FOUND (knownNid)",
        "different result")]
    [InlineData(
        "[LOADER][WARN] Import#10 unresolved: nid=knownNid",
        "unresolved warning")]
    [InlineData(
        "[LOADER][WARN] Import#10 result: ORBIS_GEN2_ERROR_PERMISSION_DENIED (otherNid)",
        "different NID")]
    public void KnownImportWarningMatchingRequiresTheExactResultAndNid(
        string warning,
        string scenario)
    {
        var testCase = CreateExecutionCase(
            maximumImportWarnings: 0,
            knownImportWarnings:
            [
                new()
                {
                    Nid = "knownNid",
                    Result = "ORBIS_GEN2_ERROR_PERMISSION_DENIED",
                },
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
                $"synthetic-{scenario}.json",
                warning));

        Assert.Contains(
            "unexpected import warnings 1 exceeded maximum 0",
            error.Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public void GameCaseRejectsDuplicateKnownImportWarningSignatures()
    {
        var warning = new ImportWarningExpectation
        {
            Nid = "knownNid",
            Result = "ORBIS_GEN2_ERROR_NOT_FOUND",
        };
        var testCase = CreateExecutionCase(
            maximumImportWarnings: 0,
            knownImportWarnings:
            [
                warning,
                new()
                {
                    Nid = warning.Nid,
                    Result = warning.Result,
                },
            ]);

        var error = Assert.Throws<InvalidDataException>(
            () => GameRegressionRunner.ValidateCase(testCase));

        Assert.Contains(
            "duplicate knownImportWarnings",
            error.Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public void GameCaseRequiresBudgetForKnownImportWarningSignatures()
    {
        var testCase = CreateExecutionCase(
            knownImportWarnings:
            [
                new()
                {
                    Nid = "knownNid",
                    Result = "ORBIS_GEN2_ERROR_NOT_FOUND",
                },
            ]);

        var error = Assert.Throws<InvalidDataException>(
            () => GameRegressionRunner.ValidateCase(testCase));

        Assert.Contains(
            "requires maximumImportWarnings",
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
    public void ExecutionCanRequirePresentedImageToMovePastKnownBadFrames()
    {
        var testCase = CreateExecutionCase(
            requiredPresentedGuestImage: new()
            {
                Frame = 500,
                ForbiddenFingerprints = ["0x7f046b95716664f5"],
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
            "vk.presented_guest_image frame=500 " +
            "fingerprint=0xFE9CE3C77AB8A325");
    }

    [Fact]
    public void ExecutionRejectsKnownBadPresentedImageFingerprint()
    {
        var testCase = CreateExecutionCase(
            requiredPresentedGuestImage: new()
            {
                Frame = 500,
                ForbiddenFingerprints = ["7F046B95716664F5"],
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
                "vk.presented_guest_image frame=500 " +
                "fingerprint=0x7F046B95716664F5"));

        Assert.Contains(
            "forbidden fingerprint was observed",
            error.Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public void ExecutionCanRequirePresentedImageContentWithoutExactPixels()
    {
        var testCase = CreateExecutionCase(
            requiredPresentedGuestImage: new()
            {
                Frame = 500,
                MinimumNonBlackPixels = 1,
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
            "vk.presented_guest_image frame=500 " +
            "fingerprint=0xFE9CE3C77AB8A325 " +
            "nonblack_pixels=42/8294400");
    }

    [Fact]
    public void ExecutionRejectsBlackPresentedImage()
    {
        var testCase = CreateExecutionCase(
            requiredPresentedGuestImage: new()
            {
                Frame = 500,
                MinimumNonBlackPixels = 1,
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
                "vk.presented_guest_image frame=500 " +
                "fingerprint=0xFE9CE3C77AB8A325 " +
                "nonblack_pixels=0/8294400"));

        Assert.Contains(
            "non-black pixels 0 were below 1",
            error.Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public void ExecutionRequiresPresentedImageCoverageMetric()
    {
        var testCase = CreateExecutionCase(
            requiredPresentedGuestImage: new()
            {
                Frame = 500,
                MinimumNonBlackPixels = 1,
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
                "vk.presented_guest_image frame=500 " +
                "fingerprint=0xFE9CE3C77AB8A325"));

        Assert.Contains(
            "did not report non-black pixel coverage",
            error.Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public void ExecutionCanRequireIntermediateGuestImageWrite()
    {
        var testCase = CreateExecutionCase(
            requiredGuestImageWrite: new()
            {
                Selector = " 1280 X 720 @ 105 ",
                Fingerprint = "0xDAF3D652D1606985",
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
            [LOADER][TRACE] vk.guest_image_write_capture selector=1280x720@105 fingerprint=0xDAF3D652D1606985
            """);
    }

    [Fact]
    public void ExecutionRejectsDifferentIntermediateGuestImageWrite()
    {
        var testCase = CreateExecutionCase(
            requiredGuestImageWrite: new()
            {
                Selector = "1280x720@105",
                Fingerprint = "DAF3D652D1606985",
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
                "vk.guest_image_write_capture selector=1280x720@105 " +
                "fingerprint=0x0000000000000000"));

        Assert.Contains(
            "1280x720@105",
            error.Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public void CaptureEnvironmentIsDerivedOnlyFromExpectations()
    {
        var startInfo = new ProcessStartInfo();
        startInfo.Environment["SHARPEMU_DUMP_VIDEOOUT"] = "stale";
        startInfo.Environment["SHARPEMU_LOG_VIDEOOUT"] = "stale";
        startInfo.Environment["SHARPEMU_TRACE_GUEST_IMAGES"] = "stale";
        startInfo.Environment["SHARPEMU_TRACE_GUEST_WRITES"] = "stale";
        startInfo.Environment[
            "SHARPEMU_CAPTURE_PRESENTED_GUEST_IMAGE_FRAME"] = "stale";
        startInfo.Environment[
            "SHARPEMU_PRESENTED_GUEST_IMAGE_DUMP_DIR"] = "stale";
        startInfo.Environment[
            "SHARPEMU_CAPTURE_GUEST_IMAGE_WRITE"] = "stale";
        startInfo.Environment[
            "SHARPEMU_GUEST_IMAGE_DUMP_DIR"] = "stale";
        var presentedArtifactDirectory = Path.Combine(
            Path.GetTempPath(),
            "sharpemu-presented-test");
        var guestWriteArtifactDirectory = Path.Combine(
            Path.GetTempPath(),
            "sharpemu-guest-write-test");

        GameRegressionRunner.ConfigureCaptureEnvironment(
            startInfo,
            new GameRegressionExpectations(),
            presentedArtifactDirectory,
            guestWriteArtifactDirectory);

        Assert.False(startInfo.Environment.ContainsKey("SHARPEMU_DUMP_VIDEOOUT"));
        Assert.False(startInfo.Environment.ContainsKey("SHARPEMU_LOG_VIDEOOUT"));
        Assert.False(
            startInfo.Environment.ContainsKey("SHARPEMU_TRACE_GUEST_IMAGES"));
        Assert.False(
            startInfo.Environment.ContainsKey("SHARPEMU_TRACE_GUEST_WRITES"));
        Assert.False(
            startInfo.Environment.ContainsKey(
                "SHARPEMU_CAPTURE_PRESENTED_GUEST_IMAGE_FRAME"));
        Assert.False(
            startInfo.Environment.ContainsKey(
                "SHARPEMU_PRESENTED_GUEST_IMAGE_DUMP_DIR"));
        Assert.False(
            startInfo.Environment.ContainsKey(
                "SHARPEMU_CAPTURE_GUEST_IMAGE_WRITE"));
        Assert.False(
            startInfo.Environment.ContainsKey(
                "SHARPEMU_GUEST_IMAGE_DUMP_DIR"));

        GameRegressionRunner.ConfigureCaptureEnvironment(
            startInfo,
            new GameRegressionExpectations
            {
                RequiredVideoOutFrameFingerprints = ["0123456789ABCDEF"],
                RequiredPresentedGuestImage = new()
                {
                    Frame = 30,
                    Fingerprint = "0123456789ABCDEF",
                },
                RequiredGuestImageWrite = new()
                {
                    Selector = " 0xC490000 @ 3 ",
                    Fingerprint = "FEDCBA9876543210",
                },
            },
            presentedArtifactDirectory,
            guestWriteArtifactDirectory);

        Assert.Equal("1", startInfo.Environment["SHARPEMU_DUMP_VIDEOOUT"]);
        Assert.Equal("1", startInfo.Environment["SHARPEMU_LOG_VIDEOOUT"]);
        Assert.Equal(
            "30",
            startInfo.Environment[
                "SHARPEMU_CAPTURE_PRESENTED_GUEST_IMAGE_FRAME"]);
        Assert.Equal(
            Path.GetFullPath(presentedArtifactDirectory),
            startInfo.Environment[
                "SHARPEMU_PRESENTED_GUEST_IMAGE_DUMP_DIR"]);
        Assert.Equal(
            "0xC490000@3",
            startInfo.Environment["SHARPEMU_CAPTURE_GUEST_IMAGE_WRITE"]);
        Assert.Equal(
            Path.GetFullPath(guestWriteArtifactDirectory),
            startInfo.Environment["SHARPEMU_GUEST_IMAGE_DUMP_DIR"]);
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

    [Fact]
    public void GameCaseAllowsRelationalPresentedImageMilestone()
    {
        var testCase = CreateExecutionCase(
            requiredPresentedGuestImage: new()
            {
                Frame = 500,
                ForbiddenFingerprints = ["7F046B95716664F5"],
            });

        GameRegressionRunner.ValidateCase(testCase);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("not-a-fingerprint")]
    public void GameCaseRequiresValidRelationalPresentedImageMilestone(
        string? forbiddenFingerprint)
    {
        var forbiddenFingerprints = forbiddenFingerprint is null
            ? Array.Empty<string>()
            : new[] { forbiddenFingerprint };
        var testCase = CreateExecutionCase(
            requiredPresentedGuestImage: new()
            {
                Frame = 500,
                ForbiddenFingerprints = forbiddenFingerprints,
            });

        var error = Assert.Throws<InvalidDataException>(
            () => GameRegressionRunner.ValidateCase(testCase));

        Assert.Contains(
            "requiredPresentedGuestImage",
            error.Message,
            StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("", "0123456789ABCDEF")]
    [InlineData("1280x720", "0123456789ABCDEF")]
    [InlineData("1280x720@0", "0123456789ABCDEF")]
    [InlineData("1280x720@1", "not-a-fingerprint")]
    public void GameCaseRequiresValidGuestImageWriteMilestone(
        string selector,
        string fingerprint)
    {
        var testCase = CreateExecutionCase(
            requiredGuestImageWrite: new()
            {
                Selector = selector,
                Fingerprint = fingerprint,
            });

        var error = Assert.Throws<InvalidDataException>(
            () => GameRegressionRunner.ValidateCase(testCase));

        Assert.Contains(
            "requiredGuestImageWrite",
            error.Message,
            StringComparison.Ordinal);
    }

    private static GameRegressionCase CreateExecutionCase(
        long? minimumObservedImportDispatches = null,
        int? maximumImportWarnings = null,
        ImportWarningExpectation[]? knownImportWarnings = null,
        string[]? requiredOutputSubstrings = null,
        string[]? forbiddenOutputSubstrings = null,
        string[]? requiredVideoOutFrameFingerprints = null,
        PresentedGuestImageExpectation? requiredPresentedGuestImage = null,
        GuestImageWriteExpectation? requiredGuestImageWrite = null,
        bool requireNoUnsupportedRelocations = false,
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
            RequireNoUnsupportedRelocations = requireNoUnsupportedRelocations,
            RequireSuccessfulModuleInitializers = true,
            MinimumObservedImportDispatches =
                minimumObservedImportDispatches,
            MaximumImportWarnings = maximumImportWarnings,
            KnownImportWarnings = knownImportWarnings ?? [],
            RequiredOutputSubstrings =
                requiredOutputSubstrings ?? [],
            ForbiddenOutputSubstrings =
                forbiddenOutputSubstrings ?? [],
            RequiredVideoOutFrameFingerprints =
                requiredVideoOutFrameFingerprints ?? [],
            RequiredPresentedGuestImage =
                requiredPresentedGuestImage,
            RequiredGuestImageWrite =
                requiredGuestImageWrite,
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
