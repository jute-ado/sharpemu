// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Diagnostics;
using System.Text;
using System.Text.Json;
using SharpEmu.Libs.Pad;
using SharpEmu.Libs.VideoOut;

namespace SharpEmu.GameTests;

internal static class GameRegressionRunner
{
    private static readonly JsonSerializerOptions PadReplaySerializerOptions =
        new(JsonSerializerDefaults.Web);

    public static async Task<GameRegressionExecution> RunAsync(
        GameRegressionCase testCase,
        string manifestPath,
        string artifactDirectory)
    {
        ValidateCase(testCase);
        var manifestDirectory = Path.GetDirectoryName(manifestPath)
            ?? throw new InvalidDataException(
                "The game regression manifest has no parent directory.");
        var executablePath = ResolvePath(
            manifestDirectory,
            testCase.ExecutablePath);
        if (!File.Exists(executablePath))
        {
            throw new FileNotFoundException(
                $"Configured game executable was not found for '{testCase.Name}'.",
                executablePath);
        }

        var cliAssemblyPath = GameRegressionPaths.CliAssemblyPath;
        if (!File.Exists(cliAssemblyPath))
        {
            throw new FileNotFoundException(
                "SharpEmu CLI has not been built for the game regression test.",
                cliAssemblyPath);
        }

        Directory.CreateDirectory(artifactDirectory);
        var artifactStem = SanitizeFileName(testCase.Name) + "-" + testCase.Mode;
        var reportPath = Path.Combine(
            artifactDirectory,
            artifactStem + ".report.json");
        var standardOutputPath = Path.Combine(
            artifactDirectory,
            artifactStem + ".stdout.log");
        var standardErrorPath = Path.Combine(
            artifactDirectory,
            artifactStem + ".stderr.log");

        var startInfo = new ProcessStartInfo("dotnet")
        {
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
        };
        startInfo.ArgumentList.Add(cliAssemblyPath);
        if (string.Equals(testCase.Mode, "load-only", StringComparison.Ordinal))
        {
            startInfo.ArgumentList.Add("--load-only");
        }
        else
        {
            startInfo.ArgumentList.Add("--timeout-seconds");
            startInfo.ArgumentList.Add(testCase.TimeoutSeconds.ToString());
        }
        if (!string.IsNullOrWhiteSpace(testCase.ExpectedBundleSha256))
        {
            startInfo.ArgumentList.Add("--expect-bundle-sha256");
            startInfo.ArgumentList.Add(testCase.ExpectedBundleSha256);
        }
        startInfo.ArgumentList.Add("--report-json");
        startInfo.ArgumentList.Add(reportPath);
        startInfo.ArgumentList.Add(executablePath);
        startInfo.Environment.Remove("SHARPEMU_MITIGATED_CHILD");
        startInfo.Environment.Remove("SHARPEMU_DISABLE_MITIGATION_RELAUNCH");
        ConfigureCaptureEnvironment(
            startInfo,
            testCase.Expectations,
            Path.Combine(
                artifactDirectory,
                artifactStem + ".presented"),
            Path.Combine(
                artifactDirectory,
                artifactStem + ".guest-write"));
        ConfigurePadReplayEnvironment(startInfo, testCase.PadReplay);

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException(
                "Failed to start the SharpEmu game regression process.");
        var standardOutputTask = GameRegressionOutputCapture.CaptureAsync(
            process.StandardOutput,
            standardOutputPath,
            testCase.Expectations);
        var standardErrorTask = GameRegressionOutputCapture.CaptureAsync(
            process.StandardError,
            standardErrorPath,
            testCase.Expectations);
        var outerTimeoutSeconds = checked(testCase.TimeoutSeconds + 60);
        using var timeout = new CancellationTokenSource(
            TimeSpan.FromSeconds(outerTimeoutSeconds));
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync();
            throw new TimeoutException(
                $"Game regression '{testCase.Name}' exceeded its outer " +
                $"{outerTimeoutSeconds}-second safety timeout.");
        }

        var standardOutput = await standardOutputTask;
        var standardError = await standardErrorTask;
        var outputAnalysis = GameOutputAnalysis.Combine(
            standardOutput,
            standardError);
        if (!File.Exists(reportPath))
        {
            throw new InvalidDataException(
                $"Game regression '{testCase.Name}' produced no JSON report. " +
                $"Exit code: {process.ExitCode}. Diagnostics: {standardErrorPath}");
        }

        var reportJson = await File.ReadAllTextAsync(reportPath);
        using var report = JsonDocument.Parse(reportJson);
        ValidateReport(
            testCase,
            report.RootElement,
            reportPath,
            outputAnalysis.RelevantOutput,
            outputAnalysis);
        return new GameRegressionExecution(
            testCase.Name,
            testCase.Mode,
            process.ExitCode,
            reportPath,
            standardOutputPath,
            standardErrorPath);
    }

    internal static void ConfigureCaptureEnvironment(
        ProcessStartInfo startInfo,
        GameRegressionExpectations expectations,
        string presentedImageArtifactDirectory,
        string guestImageWriteArtifactDirectory)
    {
        ArgumentNullException.ThrowIfNull(startInfo);
        ArgumentNullException.ThrowIfNull(expectations);
        ArgumentException.ThrowIfNullOrWhiteSpace(
            presentedImageArtifactDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(
            guestImageWriteArtifactDirectory);
        startInfo.Environment.Remove("SHARPEMU_DUMP_VIDEOOUT");
        startInfo.Environment.Remove("SHARPEMU_LOG_VIDEOOUT");
        startInfo.Environment.Remove("SHARPEMU_TRACE_GUEST_IMAGES");
        startInfo.Environment.Remove(
            "SHARPEMU_TRACE_PRESENTED_FRAME_PROGRESS");
        startInfo.Environment.Remove("SHARPEMU_TRACE_GUEST_WRITES");
        startInfo.Environment.Remove(
            "SHARPEMU_CAPTURE_PRESENTED_GUEST_IMAGE_FRAME");
        startInfo.Environment.Remove(
            "SHARPEMU_PRESENTED_GUEST_IMAGE_DUMP_DIR");
        startInfo.Environment.Remove("SHARPEMU_CAPTURE_GUEST_IMAGE_WRITE");
        startInfo.Environment.Remove("SHARPEMU_GUEST_IMAGE_DUMP_DIR");

        if (expectations.RequiredVideoOutFrameFingerprints.Length != 0)
        {
            startInfo.Environment["SHARPEMU_DUMP_VIDEOOUT"] = "1";
            startInfo.Environment["SHARPEMU_LOG_VIDEOOUT"] = "1";
        }
        if (expectations.RequiredPresentedGuestImage is { } presentedImage)
        {
            startInfo.Environment[
                "SHARPEMU_TRACE_PRESENTED_FRAME_PROGRESS"] = "1";
            startInfo.Environment[
                "SHARPEMU_CAPTURE_PRESENTED_GUEST_IMAGE_FRAME"] =
                presentedImage.Frame.ToString(
                    System.Globalization.CultureInfo.InvariantCulture);
            startInfo.Environment[
                "SHARPEMU_PRESENTED_GUEST_IMAGE_DUMP_DIR"] =
                Path.GetFullPath(presentedImageArtifactDirectory);
        }
        if (expectations.RequiredGuestImageWrite is { } guestImageWrite &&
            GuestImageWriteCaptureRequest.TryParse(
                guestImageWrite.Selector,
                out var captureRequest) &&
            captureRequest.IsEnabled)
        {
            startInfo.Environment["SHARPEMU_CAPTURE_GUEST_IMAGE_WRITE"] =
                captureRequest.ToString();
            startInfo.Environment["SHARPEMU_GUEST_IMAGE_DUMP_DIR"] =
                Path.GetFullPath(guestImageWriteArtifactDirectory);
        }
    }

    internal static void ConfigurePadReplayEnvironment(
        ProcessStartInfo startInfo,
        GamePadReplay? replay)
    {
        ArgumentNullException.ThrowIfNull(startInfo);
        startInfo.Environment.Remove("SHARPEMU_PAD_REPLAY");
        startInfo.Environment.Remove("SHARPEMU_AUTO_CROSS");
        if (replay is null)
        {
            return;
        }

        startInfo.Environment["SHARPEMU_PAD_REPLAY"] =
            SerializePadReplay(replay);
    }

    public static string ResolveArtifactDirectory(
        GameRegressionManifest manifest,
        string manifestPath)
    {
        if (string.IsNullOrWhiteSpace(manifest.ArtifactDirectory))
        {
            return Path.Combine(
                GameRegressionPaths.RepoRoot,
                "artifacts",
                "game-tests");
        }

        var manifestDirectory = Path.GetDirectoryName(manifestPath)
            ?? GameRegressionPaths.RepoRoot;
        return ResolvePath(manifestDirectory, manifest.ArtifactDirectory);
    }

    internal static void ValidateCase(GameRegressionCase testCase)
    {
        if (string.IsNullOrWhiteSpace(testCase.Name))
        {
            throw new InvalidDataException(
                "Every game regression case requires a name.");
        }
        if (string.IsNullOrWhiteSpace(testCase.ExecutablePath))
        {
            throw new InvalidDataException(
                $"Game regression '{testCase.Name}' requires an executablePath.");
        }
        if (!TryNormalizeSha256(testCase.ExpectedBundleSha256, out _))
        {
            throw new InvalidDataException(
                $"Game regression '{testCase.Name}' requires a valid " +
                "expectedBundleSha256 so its baseline cannot run against a " +
                "different local game build.");
        }
        if (!string.Equals(testCase.Mode, "load-only", StringComparison.Ordinal) &&
            !string.Equals(testCase.Mode, "execution", StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"Game regression '{testCase.Name}' has unsupported mode " +
                $"'{testCase.Mode}'.");
        }
        if (testCase.TimeoutSeconds is <= 0 or > 86400)
        {
            throw new InvalidDataException(
                $"Game regression '{testCase.Name}' has an invalid timeout.");
        }
        if (testCase.PadReplay is { } replay)
        {
            if (!string.Equals(
                    testCase.Mode,
                    "execution",
                    StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    $"Game regression '{testCase.Name}' can only use " +
                    "padReplay in execution mode.");
            }

            var replayJson = SerializePadReplay(replay);
            if (!PadReplayScript.TryParse(
                    replayJson,
                    out _,
                    out var replayError))
            {
                throw new InvalidDataException(
                    $"Game regression '{testCase.Name}' has an invalid " +
                    $"padReplay: {replayError}.");
            }
            if (replay.Events[^1].AtMilliseconds is { } lastMilliseconds &&
                lastMilliseconds >= checked(testCase.TimeoutSeconds * 1000L))
            {
                throw new InvalidDataException(
                    $"Game regression '{testCase.Name}' has a padReplay " +
                    "event at or after its timeout.");
            }
        }
        if (testCase.Expectations.AllowedResults.Length == 0)
        {
            throw new InvalidDataException(
                $"Game regression '{testCase.Name}' requires allowedResults.");
        }
        if (testCase.Expectations.MinimumObservedImportDispatches is < 0)
        {
            throw new InvalidDataException(
                $"Game regression '{testCase.Name}' has a negative " +
                "minimumObservedImportDispatches.");
        }
        if (testCase.Expectations.MaximumImportWarnings is < 0)
        {
            throw new InvalidDataException(
                $"Game regression '{testCase.Name}' has a negative " +
                "maximumImportWarnings.");
        }
        if (testCase.Expectations.KnownImportWarnings.Length != 0 &&
            testCase.Expectations.MaximumImportWarnings is null)
        {
            throw new InvalidDataException(
                $"Game regression '{testCase.Name}' requires " +
                "maximumImportWarnings when knownImportWarnings are configured.");
        }
        for (var index = 0;
            index < testCase.Expectations.KnownImportWarnings.Length;
            index++)
        {
            var warning = testCase.Expectations.KnownImportWarnings[index];
            if (string.IsNullOrWhiteSpace(warning.Nid) ||
                string.IsNullOrWhiteSpace(warning.Result))
            {
                throw new InvalidDataException(
                    $"Game regression '{testCase.Name}' has an invalid " +
                    "knownImportWarnings entry.");
            }

            for (var previousIndex = 0;
                previousIndex < index;
                previousIndex++)
            {
                var previous =
                    testCase.Expectations.KnownImportWarnings[previousIndex];
                if (string.Equals(
                        warning.Nid,
                        previous.Nid,
                        StringComparison.Ordinal) &&
                    string.Equals(
                        warning.Result,
                        previous.Result,
                        StringComparison.Ordinal))
                {
                    throw new InvalidDataException(
                        $"Game regression '{testCase.Name}' has a duplicate " +
                        "knownImportWarnings entry.");
                }
            }
        }
        for (var index = 0;
            index < testCase.Expectations.RequiredOutputSubstrings.Length;
            index++)
        {
            if (string.IsNullOrWhiteSpace(
                testCase.Expectations.RequiredOutputSubstrings[index]))
            {
                throw new InvalidDataException(
                    $"Game regression '{testCase.Name}' contains an empty " +
                    "requiredOutputSubstrings entry.");
                }
        }
        for (var index = 0;
            index < testCase.Expectations.ForbiddenOutputSubstrings.Length;
            index++)
        {
            if (string.IsNullOrWhiteSpace(
                testCase.Expectations.ForbiddenOutputSubstrings[index]))
            {
                throw new InvalidDataException(
                    $"Game regression '{testCase.Name}' contains an empty " +
                    "forbiddenOutputSubstrings entry.");
            }
        }
        for (var index = 0;
            index <
                testCase.Expectations.RequiredVideoOutFrameFingerprints.Length;
            index++)
        {
            if (!TryNormalizeFrameFingerprint(
                    testCase.Expectations
                        .RequiredVideoOutFrameFingerprints[index],
                    out _))
            {
                throw new InvalidDataException(
                    $"Game regression '{testCase.Name}' has an invalid " +
                    "requiredVideoOutFrameFingerprints entry.");
            }
        }
        if (testCase.Expectations.RequiredPresentedGuestImage is
            { } presentedImage)
        {
            if (presentedImage.Frame <= 0)
            {
                throw new InvalidDataException(
                    $"Game regression '{testCase.Name}' has an invalid " +
                    "requiredPresentedGuestImage frame.");
            }
            var hasRequiredFingerprint = TryNormalizeFrameFingerprint(
                presentedImage.Fingerprint,
                out var requiredFingerprint);
            if (!string.IsNullOrWhiteSpace(presentedImage.Fingerprint) &&
                !hasRequiredFingerprint)
            {
                throw new InvalidDataException(
                    $"Game regression '{testCase.Name}' has an invalid " +
                    "requiredPresentedGuestImage fingerprint.");
            }
            if (presentedImage.MinimumNonBlackPixels is <= 0)
            {
                throw new InvalidDataException(
                    $"Game regression '{testCase.Name}' has an invalid " +
                    "requiredPresentedGuestImage minimumNonBlackPixels.");
            }
            if (presentedImage.MinimumDistinctColors is
                < 2 or > GuestImageMetrics.MaximumDistinctPixelValues)
            {
                throw new InvalidDataException(
                    $"Game regression '{testCase.Name}' has an invalid " +
                    "requiredPresentedGuestImage minimumDistinctColors.");
            }
            if (!hasRequiredFingerprint &&
                presentedImage.ForbiddenFingerprints.Length == 0 &&
                presentedImage.MinimumNonBlackPixels is null &&
                presentedImage.MinimumDistinctColors is null)
            {
                throw new InvalidDataException(
                    $"Game regression '{testCase.Name}' requires " +
                    "requiredPresentedGuestImage fingerprint or " +
                    "forbiddenFingerprints, minimumNonBlackPixels, or " +
                    "minimumDistinctColors.");
            }
            for (var index = 0;
                index < presentedImage.ForbiddenFingerprints.Length;
                index++)
            {
                if (!TryNormalizeFrameFingerprint(
                        presentedImage.ForbiddenFingerprints[index],
                        out var forbiddenFingerprint))
                {
                    throw new InvalidDataException(
                        $"Game regression '{testCase.Name}' has an invalid " +
                        "requiredPresentedGuestImage forbiddenFingerprints " +
                        "entry.");
                }
                if (hasRequiredFingerprint &&
                    string.Equals(
                        requiredFingerprint,
                        forbiddenFingerprint,
                        StringComparison.Ordinal))
                {
                    throw new InvalidDataException(
                        $"Game regression '{testCase.Name}' has a " +
                        "requiredPresentedGuestImage fingerprint that is " +
                        "also forbidden.");
                }
            }
        }
        if (testCase.Expectations.RequiredGuestImageWrite is
            { } guestImageWrite)
        {
            if (!GuestImageWriteCaptureRequest.TryParse(
                    guestImageWrite.Selector,
                    out var captureRequest) ||
                !captureRequest.IsEnabled)
            {
                throw new InvalidDataException(
                    $"Game regression '{testCase.Name}' has an invalid " +
                    "requiredGuestImageWrite selector.");
            }
            var hasRequiredFingerprint = TryNormalizeFrameFingerprint(
                guestImageWrite.Fingerprint,
                out var requiredFingerprint);
            if (!string.IsNullOrWhiteSpace(guestImageWrite.Fingerprint) &&
                !hasRequiredFingerprint)
            {
                throw new InvalidDataException(
                    $"Game regression '{testCase.Name}' has an invalid " +
                    "requiredGuestImageWrite fingerprint.");
            }
            if (guestImageWrite.MinimumNonBlackPixels is <= 0)
            {
                throw new InvalidDataException(
                    $"Game regression '{testCase.Name}' has an invalid " +
                    "requiredGuestImageWrite minimumNonBlackPixels.");
            }
            if (guestImageWrite.MinimumDistinctColors is
                < 2 or > GuestImageMetrics.MaximumDistinctPixelValues)
            {
                throw new InvalidDataException(
                    $"Game regression '{testCase.Name}' has an invalid " +
                    "requiredGuestImageWrite minimumDistinctColors.");
            }
            if (!hasRequiredFingerprint &&
                guestImageWrite.ForbiddenFingerprints.Length == 0 &&
                guestImageWrite.MinimumNonBlackPixels is null &&
                guestImageWrite.MinimumDistinctColors is null)
            {
                throw new InvalidDataException(
                    $"Game regression '{testCase.Name}' requires " +
                    "requiredGuestImageWrite fingerprint or " +
                    "forbiddenFingerprints, minimumNonBlackPixels, or " +
                    "minimumDistinctColors.");
            }
            for (var index = 0;
                index < guestImageWrite.ForbiddenFingerprints.Length;
                index++)
            {
                if (!TryNormalizeFrameFingerprint(
                        guestImageWrite.ForbiddenFingerprints[index],
                        out var forbiddenFingerprint))
                {
                    throw new InvalidDataException(
                        $"Game regression '{testCase.Name}' has an invalid " +
                        "requiredGuestImageWrite forbiddenFingerprints entry.");
                }
                if (hasRequiredFingerprint &&
                    string.Equals(
                        requiredFingerprint,
                        forbiddenFingerprint,
                        StringComparison.Ordinal))
                {
                    throw new InvalidDataException(
                        $"Game regression '{testCase.Name}' has a " +
                        "requiredGuestImageWrite fingerprint that is also " +
                        "forbidden.");
                }
            }
        }
    }

    private static string SerializePadReplay(GamePadReplay replay) =>
        JsonSerializer.Serialize(replay, PadReplaySerializerOptions);

    internal static void ValidateReport(
        GameRegressionCase testCase,
        JsonElement report,
        string reportPath,
        string capturedOutput = "",
        GameOutputAnalysis? outputAnalysis = null)
    {
        var searchableOutput =
            outputAnalysis?.RelevantOutput ?? capturedOutput;
        var failures = new StringBuilder();
        var resultName = GetRequiredString(
            report.GetProperty("result"),
            "name");
        if (!Contains(
                testCase.Expectations.AllowedResults,
                resultName))
        {
            failures.AppendLine(
                $"result '{resultName}' was not allowed " +
                $"({string.Join(", ", testCase.Expectations.AllowedResults)}).");
        }
        if (Contains(testCase.Expectations.ForbiddenResults, resultName))
        {
            failures.AppendLine($"result '{resultName}' was explicitly forbidden.");
        }

        if (!string.IsNullOrWhiteSpace(
                testCase.Expectations.ApplicationTitleId) &&
            (!report.TryGetProperty("application", out var application) ||
             application.ValueKind != JsonValueKind.Object ||
             !string.Equals(
                 GetOptionalString(application, "titleId"),
                 testCase.Expectations.ApplicationTitleId,
                 StringComparison.OrdinalIgnoreCase)))
        {
            failures.AppendLine(
                $"application title ID did not match " +
                $"'{testCase.Expectations.ApplicationTitleId}'.");
        }

        if (testCase.Expectations.MinimumModules is { } minimumModules)
        {
            var moduleCount = report.TryGetProperty("modules", out var modules) &&
                modules.ValueKind == JsonValueKind.Array
                ? modules.GetArrayLength()
                : 0;
            if (moduleCount < minimumModules)
            {
                failures.AppendLine(
                    $"module count {moduleCount} was below {minimumModules}.");
            }
        }

        if (testCase.Expectations.RequireNoModuleLoadFailures)
        {
            if (!report.TryGetProperty(
                    "moduleLoadFailures",
                    out var loadFailures) ||
                loadFailures.ValueKind != JsonValueKind.Array)
            {
                failures.AppendLine(
                    "module load failure status was not captured.");
            }
            else if (loadFailures.GetArrayLength() != 0)
            {
                failures.AppendLine(
                    $"module load failures: {loadFailures.GetArrayLength()}.");
            }
        }

        if (testCase.Expectations.RequireNoUnsupportedRelocations)
        {
            if (!report.TryGetProperty("image", out var image) ||
                image.ValueKind != JsonValueKind.Object)
            {
                failures.AppendLine(
                    "main image relocation status was not captured.");
            }
            else
            {
                AppendUnsupportedRelocationFailures(
                    image,
                    "main image",
                    failures);
            }

            if (!report.TryGetProperty("modules", out var modules) ||
                modules.ValueKind != JsonValueKind.Array)
            {
                failures.AppendLine(
                    "module relocation status was not captured.");
            }
            else
            {
                foreach (var module in modules.EnumerateArray())
                {
                    var modulePath = GetOptionalString(module, "path") ??
                        "(unknown module)";
                    if (!module.TryGetProperty("image", out var moduleImage) ||
                        moduleImage.ValueKind != JsonValueKind.Object)
                    {
                        failures.AppendLine(
                            $"{modulePath} relocation status was not captured.");
                        continue;
                    }

                    AppendUnsupportedRelocationFailures(
                        moduleImage,
                        modulePath,
                        failures);
                }
            }
        }

        if (testCase.Expectations.RequireSuccessfulModuleInitializers)
        {
            if (!report.TryGetProperty(
                    "moduleInitializers",
                    out var initializers) ||
                initializers.ValueKind != JsonValueKind.Array)
            {
                failures.AppendLine(
                    "module initializer status was not captured.");
            }
            else
            {
                foreach (var initializer in initializers.EnumerateArray())
                {
                    if (!initializer.GetProperty("result")
                        .GetProperty("succeeded")
                        .GetBoolean())
                    {
                        failures.AppendLine(
                            "at least one module initializer failed.");
                        break;
                    }
                }
            }
        }

        if (testCase.Expectations.MinimumObservedImportDispatches is
            { } minimumImportDispatches)
        {
            var observedImportDispatches = outputAnalysis is null
                ? GetMaximumObservedImportDispatch(capturedOutput)
                : outputAnalysis.MaximumObservedImportDispatch;
            if (observedImportDispatches < minimumImportDispatches)
            {
                failures.AppendLine(
                    $"maximum observed import dispatch " +
                    $"{observedImportDispatches} was below " +
                    $"{minimumImportDispatches}.");
            }
        }

        if (testCase.Expectations.MaximumImportWarnings is
            { } maximumImportWarnings)
        {
            var importWarnings = outputAnalysis is null
                ? AnalyzeImportWarnings(
                    capturedOutput,
                    testCase.Expectations.KnownImportWarnings)
                : (
                    Total: outputAnalysis.TotalImportWarnings,
                    Known: outputAnalysis.KnownImportWarnings,
                    Unexpected: outputAnalysis.UnexpectedImportWarnings);
            if (importWarnings.Unexpected > maximumImportWarnings)
            {
                failures.AppendLine(
                    $"unexpected import warnings " +
                    $"{importWarnings.Unexpected} exceeded maximum " +
                    $"{maximumImportWarnings} " +
                    $"(known {importWarnings.Known}, " +
                    $"total {importWarnings.Total}).");
            }
        }

        for (var index = 0;
            index < testCase.Expectations.RequiredOutputSubstrings.Length;
            index++)
        {
            var requiredOutput =
                testCase.Expectations.RequiredOutputSubstrings[index];
            if (!OutputContains(
                    capturedOutput,
                    outputAnalysis,
                    requiredOutput,
                    StringComparison.Ordinal))
            {
                failures.AppendLine(
                    $"required output milestone was not observed: " +
                    $"'{requiredOutput}'.");
            }
        }

        for (var index = 0;
            index < testCase.Expectations.ForbiddenOutputSubstrings.Length;
            index++)
        {
            var forbiddenOutput =
                testCase.Expectations.ForbiddenOutputSubstrings[index];
            if (OutputContains(
                    capturedOutput,
                    outputAnalysis,
                    forbiddenOutput,
                    StringComparison.Ordinal))
            {
                failures.AppendLine(
                    $"forbidden output was observed: '{forbiddenOutput}'.");
            }
        }

        for (var index = 0;
            index <
                testCase.Expectations.RequiredVideoOutFrameFingerprints.Length;
            index++)
        {
            _ = TryNormalizeFrameFingerprint(
                testCase.Expectations.RequiredVideoOutFrameFingerprints[index],
                out var fingerprint);
            var marker = $"fingerprint=0x{fingerprint}";
            if (!OutputContains(
                    capturedOutput,
                    outputAnalysis,
                    marker,
                    StringComparison.OrdinalIgnoreCase))
            {
                failures.AppendLine(
                    $"required VideoOut frame fingerprint was not observed: " +
                    $"0x{fingerprint}.");
            }
        }

        if (testCase.Expectations.RequiredPresentedGuestImage is
            { } presentedImage)
        {
            if (!TryGetPresentedGuestImageObservation(
                    searchableOutput,
                    presentedImage.Frame,
                    out var actualFingerprint,
                    out var nonBlackPixels,
                    out var distinctColors))
            {
                var maximumPresentedFrame = outputAnalysis is null
                    ? GetMaximumPresentedGuestFrame(capturedOutput)
                    : outputAnalysis.MaximumPresentedGuestFrame;
                failures.AppendLine(
                    $"required presented guest image frame {presentedImage.Frame} " +
                    "was not observed" +
                    (maximumPresentedFrame > 0
                        ? $" (highest presented frame: " +
                          $"{maximumPresentedFrame})"
                        : string.Empty) +
                    ".");
            }
            else
            {
                if (TryNormalizeFrameFingerprint(
                        presentedImage.Fingerprint,
                        out var requiredFingerprint) &&
                    !string.Equals(
                        actualFingerprint,
                        requiredFingerprint,
                        StringComparison.Ordinal))
                {
                    failures.AppendLine(
                        $"required presented guest image frame " +
                        $"{presentedImage.Frame} fingerprint was not observed: " +
                        $"0x{requiredFingerprint} (actual " +
                        $"0x{actualFingerprint}).");
                }
                for (var index = 0;
                    index < presentedImage.ForbiddenFingerprints.Length;
                    index++)
                {
                    _ = TryNormalizeFrameFingerprint(
                        presentedImage.ForbiddenFingerprints[index],
                        out var forbiddenFingerprint);
                    if (string.Equals(
                            actualFingerprint,
                            forbiddenFingerprint,
                            StringComparison.Ordinal))
                    {
                        failures.AppendLine(
                            $"presented guest image frame " +
                            $"{presentedImage.Frame} forbidden fingerprint " +
                            $"was observed: 0x{forbiddenFingerprint}.");
                    }
                }
                if (presentedImage.MinimumNonBlackPixels is
                    { } minimumNonBlackPixels)
                {
                    if (nonBlackPixels is null)
                    {
                        failures.AppendLine(
                            $"presented guest image frame " +
                            $"{presentedImage.Frame} did not report " +
                            "non-black pixel coverage.");
                    }
                    else if (nonBlackPixels < minimumNonBlackPixels)
                    {
                        failures.AppendLine(
                            $"presented guest image frame " +
                            $"{presentedImage.Frame} non-black pixels " +
                            $"{nonBlackPixels} were below " +
                            $"{minimumNonBlackPixels}.");
                    }
                }
                if (presentedImage.MinimumDistinctColors is
                    { } minimumDistinctColors)
                {
                    if (distinctColors is null)
                    {
                        failures.AppendLine(
                            $"presented guest image frame " +
                            $"{presentedImage.Frame} did not report " +
                            "color diversity.");
                    }
                    else if (distinctColors < minimumDistinctColors)
                    {
                        failures.AppendLine(
                            $"presented guest image frame " +
                            $"{presentedImage.Frame} distinct colors " +
                            $"{distinctColors} were below " +
                            $"{minimumDistinctColors}.");
                    }
                }
            }
        }

        if (testCase.Expectations.RequiredGuestImageWrite is
            { } guestImageWrite)
        {
            _ = GuestImageWriteCaptureRequest.TryParse(
                guestImageWrite.Selector,
                out var captureRequest);
            var marker =
                $"vk.guest_image_write_capture selector={captureRequest} " +
                "fingerprint=0x";
            if (!TryGetCapturedImageObservation(
                    searchableOutput,
                    marker,
                    out var actualFingerprint,
                    out var nonBlackPixels,
                    out var distinctColors))
            {
                failures.AppendLine(
                    $"required guest image write {captureRequest} " +
                    "was not observed.");
            }
            else
            {
                if (TryNormalizeFrameFingerprint(
                        guestImageWrite.Fingerprint,
                        out var requiredFingerprint) &&
                    !string.Equals(
                        actualFingerprint,
                        requiredFingerprint,
                        StringComparison.Ordinal))
                {
                    failures.AppendLine(
                        $"required guest image write {captureRequest} " +
                        $"fingerprint was not observed: " +
                        $"0x{requiredFingerprint} (actual " +
                        $"0x{actualFingerprint}).");
                }
                for (var index = 0;
                    index < guestImageWrite.ForbiddenFingerprints.Length;
                    index++)
                {
                    _ = TryNormalizeFrameFingerprint(
                        guestImageWrite.ForbiddenFingerprints[index],
                        out var forbiddenFingerprint);
                    if (string.Equals(
                            actualFingerprint,
                            forbiddenFingerprint,
                            StringComparison.Ordinal))
                    {
                        failures.AppendLine(
                            $"guest image write {captureRequest} forbidden " +
                            $"fingerprint was observed: " +
                            $"0x{forbiddenFingerprint}.");
                    }
                }
                if (guestImageWrite.MinimumNonBlackPixels is
                    { } minimumNonBlackPixels)
                {
                    if (nonBlackPixels is null)
                    {
                        failures.AppendLine(
                            $"guest image write {captureRequest} did not " +
                            "report non-black pixel coverage.");
                    }
                    else if (nonBlackPixels < minimumNonBlackPixels)
                    {
                        failures.AppendLine(
                            $"guest image write {captureRequest} non-black " +
                            $"pixels {nonBlackPixels} were below " +
                            $"{minimumNonBlackPixels}.");
                    }
                }
                if (guestImageWrite.MinimumDistinctColors is
                    { } minimumDistinctColors)
                {
                    if (distinctColors is null)
                    {
                        failures.AppendLine(
                            $"guest image write {captureRequest} did not " +
                            "report color diversity.");
                    }
                    else if (distinctColors < minimumDistinctColors)
                    {
                        failures.AppendLine(
                            $"guest image write {captureRequest} distinct " +
                            $"colors {distinctColors} were below " +
                            $"{minimumDistinctColors}.");
                    }
                }
            }
        }

        if (failures.Length != 0)
        {
            throw new InvalidOperationException(
                $"Game regression '{testCase.Name}' failed:{Environment.NewLine}" +
                failures +
                $"Report: {reportPath}");
        }
    }

    private static bool Contains(string[] values, string value)
    {
        for (var index = 0; index < values.Length; index++)
        {
            if (string.Equals(
                    values[index],
                    value,
                    StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static bool OutputContains(
        string capturedOutput,
        GameOutputAnalysis? outputAnalysis,
        string value,
        StringComparison comparison) =>
        outputAnalysis?.Contains(value, comparison) ??
        capturedOutput.Contains(value, comparison);

    private static void AppendUnsupportedRelocationFailures(
        JsonElement image,
        string label,
        StringBuilder failures)
    {
        if (!image.TryGetProperty(
                "unsupportedRelocationTypes",
                out var unsupportedTypes) ||
            unsupportedTypes.ValueKind != JsonValueKind.Array)
        {
            failures.AppendLine(
                $"{label} unsupported relocation status was not captured.");
            return;
        }

        if (unsupportedTypes.GetArrayLength() == 0)
        {
            return;
        }

        var typeList = new StringBuilder();
        foreach (var relocationType in unsupportedTypes.EnumerateArray())
        {
            if (typeList.Length != 0)
            {
                typeList.Append(", ");
            }
            typeList.Append(relocationType.GetRawText());
        }
        failures.AppendLine(
            $"{label} uses unsupported relocation types: {typeList}.");
    }

    internal static long GetMaximumObservedImportDispatch(string output)
    {
        const string marker = "Import#";
        var maximum = 0L;
        var searchOffset = 0;
        while (searchOffset < output.Length)
        {
            var relativeMarker = output.AsSpan(searchOffset)
                .IndexOf(marker, StringComparison.Ordinal);
            if (relativeMarker < 0)
            {
                break;
            }

            var digitStart = searchOffset + relativeMarker + marker.Length;
            var digitEnd = digitStart;
            while (digitEnd < output.Length &&
                char.IsAsciiDigit(output[digitEnd]))
            {
                digitEnd++;
            }

            if (digitEnd > digitStart &&
                long.TryParse(
                    output.AsSpan(digitStart, digitEnd - digitStart),
                    out var dispatch) &&
                dispatch > maximum)
            {
                maximum = dispatch;
            }

            searchOffset = Math.Max(digitEnd, digitStart + 1);
        }

        return maximum;
    }

    internal static long GetMaximumPresentedGuestFrame(string output)
    {
        const string marker = "vk.present_progress frame=";
        var maximum = 0L;
        var searchOffset = 0;
        while (searchOffset < output.Length)
        {
            var relativeMarker = output.AsSpan(searchOffset)
                .IndexOf(marker, StringComparison.Ordinal);
            if (relativeMarker < 0)
            {
                break;
            }

            var digitStart = searchOffset + relativeMarker + marker.Length;
            var digitLength = 0;
            while (digitStart + digitLength < output.Length &&
                   char.IsAsciiDigit(output[digitStart + digitLength]))
            {
                digitLength++;
            }
            if (digitLength != 0 &&
                long.TryParse(
                    output.AsSpan(digitStart, digitLength),
                    out var frame))
            {
                maximum = Math.Max(maximum, frame);
            }

            searchOffset = digitStart + Math.Max(digitLength, 1);
        }

        return maximum;
    }

    private static (int Total, int Known, int Unexpected) AnalyzeImportWarnings(
        string output,
        ImportWarningExpectation[] knownWarnings)
    {
        const string marker = "[LOADER][WARN] Import#";
        var total = 0;
        var known = 0;
        var searchOffset = 0;
        while (searchOffset < output.Length)
        {
            var relativeMarker = output.AsSpan(searchOffset)
                .IndexOf(marker, StringComparison.Ordinal);
            if (relativeMarker < 0)
            {
                break;
            }

            total++;
            var warningStart = searchOffset + relativeMarker;
            var lineEnd = output.IndexOfAny(
                ['\r', '\n'],
                warningStart);
            if (lineEnd < 0)
            {
                lineEnd = output.Length;
            }
            if (MatchesKnownImportWarning(
                    output.AsSpan(warningStart, lineEnd - warningStart),
                    knownWarnings))
            {
                known++;
            }

            searchOffset = Math.Max(lineEnd, warningStart + marker.Length);
        }

        return (total, known, total - known);
    }

    internal static bool MatchesKnownImportWarning(
        ReadOnlySpan<char> warningLine,
        ImportWarningExpectation[] knownWarnings)
    {
        const string resultMarker = " result: ";
        var resultOffset = warningLine.IndexOf(
            resultMarker,
            StringComparison.Ordinal);
        if (resultOffset < 0)
        {
            return false;
        }

        var resultStart = resultOffset + resultMarker.Length;
        var nidMarkerOffset = warningLine[resultStart..].IndexOf(
            " (",
            StringComparison.Ordinal);
        if (nidMarkerOffset < 0)
        {
            return false;
        }

        var nidStart = resultStart + nidMarkerOffset + 2;
        var nidEndOffset = warningLine[nidStart..].IndexOf(')');
        if (nidEndOffset < 0)
        {
            return false;
        }

        var result = warningLine[resultStart..(resultStart + nidMarkerOffset)];
        var nid = warningLine[nidStart..(nidStart + nidEndOffset)];
        for (var index = 0; index < knownWarnings.Length; index++)
        {
            var knownWarning = knownWarnings[index];
            if (nid.Equals(
                    knownWarning.Nid,
                    StringComparison.Ordinal) &&
                result.Equals(
                    knownWarning.Result,
                    StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static bool TryGetPresentedGuestImageObservation(
        string output,
        long frame,
        out string fingerprint,
        out long? nonBlackPixels,
        out int? distinctColors)
    {
        var marker =
            $"vk.presented_guest_image frame={frame} fingerprint=0x";
        return TryGetCapturedImageObservation(
            output,
            marker,
            out fingerprint,
            out nonBlackPixels,
            out distinctColors);
    }

    private static bool TryGetCapturedImageObservation(
        string output,
        string marker,
        out string fingerprint,
        out long? nonBlackPixels,
        out int? distinctColors)
    {
        fingerprint = string.Empty;
        nonBlackPixels = null;
        distinctColors = null;
        var markerOffset = output.IndexOf(
            marker,
            StringComparison.OrdinalIgnoreCase);
        if (markerOffset < 0)
        {
            return false;
        }

        var fingerprintOffset = markerOffset + marker.Length;
        const int fingerprintLength = 16;
        if (fingerprintOffset + fingerprintLength > output.Length)
        {
            return false;
        }

        if (!TryNormalizeFrameFingerprint(
                output.Substring(fingerprintOffset, fingerprintLength),
                out fingerprint))
        {
            return false;
        }

        var lineEnd = output.IndexOfAny(
            ['\r', '\n'],
            fingerprintOffset + fingerprintLength);
        if (lineEnd < 0)
        {
            lineEnd = output.Length;
        }

        if (TryGetImageMetric(
                output,
                "nonblack_pixels=",
                fingerprintOffset + fingerprintLength,
                lineEnd,
                out var parsedNonBlackPixels))
        {
            nonBlackPixels = parsedNonBlackPixels;
        }
        if (TryGetImageMetric(
                output,
                "distinct_colors=",
                fingerprintOffset + fingerprintLength,
                lineEnd,
                out var parsedDistinctColors) &&
            parsedDistinctColors <= int.MaxValue)
        {
            distinctColors = (int)parsedDistinctColors;
        }

        return true;
    }

    private static bool TryGetImageMetric(
        string output,
        string marker,
        int searchStart,
        int lineEnd,
        out long value)
    {
        value = 0;
        var markerOffset = output.IndexOf(
            marker,
            searchStart,
            lineEnd - searchStart,
            StringComparison.OrdinalIgnoreCase);
        if (markerOffset < 0)
        {
            return false;
        }

        var valueStart = markerOffset + marker.Length;
        var valueEnd = valueStart;
        while (valueEnd < lineEnd && char.IsAsciiDigit(output[valueEnd]))
        {
            valueEnd++;
        }

        return valueEnd > valueStart &&
            long.TryParse(
                output.AsSpan(valueStart, valueEnd - valueStart),
                out value);
    }

    internal static bool TryNormalizeFrameFingerprint(
        string? value,
        out string fingerprint)
    {
        fingerprint = string.Empty;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var normalized = value.Trim();
        if (normalized.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            normalized = normalized[2..];
        }
        if (normalized.Length != 16)
        {
            return false;
        }

        for (var index = 0; index < normalized.Length; index++)
        {
            if (!char.IsAsciiHexDigit(normalized[index]))
            {
                return false;
            }
        }

        fingerprint = normalized.ToUpperInvariant();
        return true;
    }

    internal static bool TryNormalizeSha256(
        string? value,
        out string fingerprint)
    {
        fingerprint = string.Empty;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var normalized = value.Trim();
        if (normalized.Length != 64)
        {
            return false;
        }

        for (var index = 0; index < normalized.Length; index++)
        {
            if (!char.IsAsciiHexDigit(normalized[index]))
            {
                return false;
            }
        }

        fingerprint = normalized.ToLowerInvariant();
        return true;
    }

    private static string ResolvePath(string baseDirectory, string path) =>
        Path.GetFullPath(
            Path.IsPathRooted(path)
                ? path
                : Path.Combine(baseDirectory, path));

    private static string GetRequiredString(JsonElement element, string name) =>
        GetOptionalString(element, name)
        ?? throw new InvalidDataException(
            $"Game regression report property '{name}' is missing.");

    private static string? GetOptionalString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) &&
        value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static string SanitizeFileName(string value)
    {
        var invalidCharacters = Path.GetInvalidFileNameChars();
        var result = new StringBuilder(value.Length);
        for (var index = 0; index < value.Length; index++)
        {
            result.Append(
                Array.IndexOf(invalidCharacters, value[index]) >= 0 ||
                char.IsWhiteSpace(value[index])
                    ? '-'
                    : char.ToLowerInvariant(value[index]));
        }

        return result.ToString();
    }
}

internal readonly record struct GameRegressionExecution(
    string Name,
    string Mode,
    int ExitCode,
    string ReportPath,
    string StandardOutputPath,
    string StandardErrorPath);
