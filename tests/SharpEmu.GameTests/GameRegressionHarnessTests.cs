// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

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
        };

        var error = Assert.Throws<InvalidDataException>(
            () => GameRegressionRunner.ValidateCase(testCase));

        Assert.Contains(
            "allowedResults",
            error.Message,
            StringComparison.Ordinal);
    }

    private static GameRegressionCase CreateExecutionCase(
        long? minimumObservedImportDispatches = null,
        string[]? requiredOutputSubstrings = null) => new()
    {
        Name = "execution survival",
        ExecutablePath = "eboot.bin",
        Mode = "execution",
        TimeoutSeconds = 20,
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
