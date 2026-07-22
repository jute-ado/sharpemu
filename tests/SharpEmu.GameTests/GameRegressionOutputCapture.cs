// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Text;
using SharpEmu.Libs.VideoOut;

namespace SharpEmu.GameTests;

internal static class GameRegressionOutputCapture
{
    internal const long DefaultMaximumArtifactCharacters = 64L * 1024 * 1024;
    private const string TruncationMarker =
        "[SharpEmu.GameTests] Output artifact truncated; analysis continued.";

    public static async Task<GameOutputAnalysis> CaptureAsync(
        TextReader reader,
        string artifactPath,
        GameRegressionExpectations expectations,
        long maximumArtifactCharacters = DefaultMaximumArtifactCharacters)
    {
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentException.ThrowIfNullOrWhiteSpace(artifactPath);
        ArgumentNullException.ThrowIfNull(expectations);
        if (maximumArtifactCharacters <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maximumArtifactCharacters));
        }

        var analysis = new GameOutputAnalysis(expectations);
        await using var stream = new FileStream(
            artifactPath,
            FileMode.Create,
            FileAccess.Write,
            FileShare.Read,
            bufferSize: 64 * 1024,
            useAsync: true);
        await using var writer = new StreamWriter(
            stream,
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        var artifactCharacters = 0L;
        var artifactTruncated = false;
        while (await reader.ReadLineAsync() is { } line)
        {
            analysis.Observe(line);
            var lineCharacters = (long)line.Length + Environment.NewLine.Length;
            if (!artifactTruncated &&
                artifactCharacters + lineCharacters <= maximumArtifactCharacters)
            {
                await writer.WriteLineAsync(line);
                artifactCharacters += lineCharacters;
                continue;
            }

            if (!artifactTruncated)
            {
                artifactTruncated = true;
                await writer.WriteLineAsync(TruncationMarker);
            }
        }

        analysis.ArtifactTruncated = artifactTruncated;
        return analysis;
    }
}

internal sealed class GameOutputAnalysis
{
    private const int MaximumRelevantOutputCharacters = 1024 * 1024;
    private readonly GameRegressionExpectations _expectations;
    private readonly HashSet<string> _observedSubstrings =
        new(StringComparer.Ordinal);
    private readonly StringBuilder _relevantOutput = new();

    public GameOutputAnalysis(GameRegressionExpectations expectations)
    {
        _expectations = expectations;
    }

    public long MaximumObservedImportDispatch { get; private set; }

    public long MaximumPresentedGuestFrame { get; private set; }

    public int TotalImportWarnings { get; private set; }

    public int KnownImportWarnings { get; private set; }

    public int UnexpectedImportWarnings =>
        TotalImportWarnings - KnownImportWarnings;

    public bool ArtifactTruncated { get; internal set; }

    public string RelevantOutput => _relevantOutput.ToString();

    public void Observe(string line)
    {
        ArgumentNullException.ThrowIfNull(line);
        var observedDispatch =
            GameRegressionRunner.GetMaximumObservedImportDispatch(line);
        if (observedDispatch > MaximumObservedImportDispatch)
        {
            MaximumObservedImportDispatch = observedDispatch;
        }
        var presentedFrame =
            GameRegressionRunner.GetMaximumPresentedGuestFrame(line);
        if (presentedFrame > MaximumPresentedGuestFrame)
        {
            MaximumPresentedGuestFrame = presentedFrame;
        }

        if (line.Contains(
                "[LOADER][WARN] Import#",
                StringComparison.Ordinal))
        {
            TotalImportWarnings++;
            if (GameRegressionRunner.MatchesKnownImportWarning(
                    line.AsSpan(),
                    _expectations.KnownImportWarnings))
            {
                KnownImportWarnings++;
            }
        }

        ObserveConfiguredSubstrings(
            line,
            _expectations.RequiredOutputSubstrings);
        ObserveConfiguredSubstrings(
            line,
            _expectations.ForbiddenOutputSubstrings);
        ObserveVideoOutFingerprints(line);
        ObserveImageCapture(line);
    }

    public bool Contains(string value, StringComparison comparison)
    {
        if (comparison == StringComparison.Ordinal &&
            _observedSubstrings.Contains(value))
        {
            return true;
        }

        if (comparison != StringComparison.Ordinal)
        {
            foreach (var observed in _observedSubstrings)
            {
                if (string.Equals(observed, value, comparison))
                {
                    return true;
                }
            }
        }

        return _relevantOutput.ToString().Contains(value, comparison);
    }

    public static GameOutputAnalysis Combine(
        GameOutputAnalysis first,
        GameOutputAnalysis second)
    {
        ArgumentNullException.ThrowIfNull(first);
        ArgumentNullException.ThrowIfNull(second);
        var combined = new GameOutputAnalysis(first._expectations)
        {
            MaximumObservedImportDispatch = Math.Max(
                first.MaximumObservedImportDispatch,
                second.MaximumObservedImportDispatch),
            MaximumPresentedGuestFrame = Math.Max(
                first.MaximumPresentedGuestFrame,
                second.MaximumPresentedGuestFrame),
            TotalImportWarnings =
                first.TotalImportWarnings + second.TotalImportWarnings,
            KnownImportWarnings =
                first.KnownImportWarnings + second.KnownImportWarnings,
            ArtifactTruncated =
                first.ArtifactTruncated || second.ArtifactTruncated,
        };
        foreach (var value in first._observedSubstrings)
        {
            combined._observedSubstrings.Add(value);
        }
        foreach (var value in second._observedSubstrings)
        {
            combined._observedSubstrings.Add(value);
        }
        combined.AppendRelevant(first.RelevantOutput);
        combined.AppendRelevant(second.RelevantOutput);
        return combined;
    }

    private void ObserveConfiguredSubstrings(
        string line,
        string[] configuredSubstrings)
    {
        for (var index = 0; index < configuredSubstrings.Length; index++)
        {
            var configured = configuredSubstrings[index];
            if (line.Contains(configured, StringComparison.Ordinal))
            {
                _observedSubstrings.Add(configured);
            }
        }
    }

    private void ObserveVideoOutFingerprints(string line)
    {
        for (var index = 0;
            index < _expectations.RequiredVideoOutFrameFingerprints.Length;
            index++)
        {
            if (!GameRegressionRunner.TryNormalizeFrameFingerprint(
                    _expectations.RequiredVideoOutFrameFingerprints[index],
                    out var fingerprint))
            {
                continue;
            }

            var marker = $"fingerprint=0x{fingerprint}";
            if (line.Contains(marker, StringComparison.OrdinalIgnoreCase))
            {
                _observedSubstrings.Add(marker);
            }
        }
    }

    private void ObserveImageCapture(string line)
    {
        var relevant = false;
        if (_expectations.RequiredPresentedGuestImage is { } presentedImage)
        {
            var marker =
                $"vk.presented_guest_image frame={presentedImage.Frame} " +
                "fingerprint=0x";
            relevant = line.Contains(marker, StringComparison.OrdinalIgnoreCase);
        }

        if (!relevant &&
            _expectations.RequiredGuestImageWrite is { } guestImageWrite &&
            GuestImageWriteCaptureRequest.TryParse(
                guestImageWrite.Selector,
                out var captureRequest))
        {
            var marker =
                $"vk.guest_image_write_capture selector={captureRequest} " +
                "fingerprint=0x";
            relevant = line.Contains(marker, StringComparison.OrdinalIgnoreCase);
        }

        if (relevant)
        {
            AppendRelevant(line);
            AppendRelevant(Environment.NewLine);
        }
    }

    private void AppendRelevant(string value)
    {
        var remaining =
            MaximumRelevantOutputCharacters - _relevantOutput.Length;
        if (remaining <= 0)
        {
            return;
        }

        _relevantOutput.Append(
            value.AsSpan(0, Math.Min(value.Length, remaining)));
    }
}
