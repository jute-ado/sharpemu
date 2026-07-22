// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Globalization;
using System.Text.Json;

namespace SharpEmu.Libs.Pad;

internal sealed class PadReplayScript
{
    private const long MaximumDurationMilliseconds = 24 * 60 * 60 * 1000;
    private const long MaximumPresentedFrame = 10_000_000;
    private const int MaximumEventCount = 1024;
    private static readonly PadState NeutralState = new(
        Connected: true,
        Buttons: 0,
        LeftX: 128,
        LeftY: 128,
        RightX: 128,
        RightY: 128,
        L2: 0,
        R2: 0);
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        AllowTrailingCommas = true,
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
    };
    private static readonly Dictionary<string, uint> ButtonBits =
        new(StringComparer.OrdinalIgnoreCase)
        {
            [nameof(OrbisPadButton.L3)] = OrbisPadButton.L3,
            [nameof(OrbisPadButton.R3)] = OrbisPadButton.R3,
            [nameof(OrbisPadButton.Options)] = OrbisPadButton.Options,
            [nameof(OrbisPadButton.Up)] = OrbisPadButton.Up,
            [nameof(OrbisPadButton.Right)] = OrbisPadButton.Right,
            [nameof(OrbisPadButton.Down)] = OrbisPadButton.Down,
            [nameof(OrbisPadButton.Left)] = OrbisPadButton.Left,
            [nameof(OrbisPadButton.L2)] = OrbisPadButton.L2,
            [nameof(OrbisPadButton.R2)] = OrbisPadButton.R2,
            [nameof(OrbisPadButton.L1)] = OrbisPadButton.L1,
            [nameof(OrbisPadButton.R1)] = OrbisPadButton.R1,
            [nameof(OrbisPadButton.Triangle)] = OrbisPadButton.Triangle,
            [nameof(OrbisPadButton.Circle)] = OrbisPadButton.Circle,
            [nameof(OrbisPadButton.Cross)] = OrbisPadButton.Cross,
            [nameof(OrbisPadButton.Square)] = OrbisPadButton.Square,
            [nameof(OrbisPadButton.TouchPad)] = OrbisPadButton.TouchPad,
        };

    private readonly PadReplayEvent[] _events;
    private readonly ReplayClock _clock;

    private PadReplayScript(PadReplayEvent[] events, ReplayClock clock)
    {
        _events = events;
        _clock = clock;
    }

    internal int EventCount => _events.Length;

    internal PadState GetState(long elapsedMilliseconds) =>
        GetState(elapsedMilliseconds, presentedFrame: 0);

    internal PadState GetState(
        long elapsedMilliseconds,
        long presentedFrame)
    {
        var position = _clock == ReplayClock.PresentedFrame
            ? presentedFrame
            : elapsedMilliseconds;
        var low = 0;
        var high = _events.Length - 1;
        var matchedIndex = -1;
        while (low <= high)
        {
            var middle = low + ((high - low) / 2);
            if (_events[middle].Position <= position)
            {
                matchedIndex = middle;
                low = middle + 1;
            }
            else
            {
                high = middle - 1;
            }
        }

        return matchedIndex >= 0
            ? _events[matchedIndex].State
            : NeutralState;
    }

    internal static bool TryParse(
        string? json,
        out PadReplayScript? script,
        out string error)
    {
        script = null;
        error = string.Empty;
        if (string.IsNullOrWhiteSpace(json))
        {
            error = "the replay document is empty";
            return false;
        }

        PadReplayDocument? document;
        try
        {
            document = JsonSerializer.Deserialize<PadReplayDocument>(
                json,
                SerializerOptions);
        }
        catch (JsonException exception)
        {
            error = $"invalid JSON: {exception.Message}";
            return false;
        }

        if (document?.Events is not { Length: > 0 } events)
        {
            error = "the replay requires at least one event";
            return false;
        }
        if (events.Length > MaximumEventCount)
        {
            error = $"the replay exceeds {MaximumEventCount} events";
            return false;
        }

        var parsedEvents = new PadReplayEvent[events.Length];
        long previousPosition = -1;
        ReplayClock? replayClock = null;
        for (var index = 0; index < events.Length; index++)
        {
            var replayEvent = events[index];
            if (replayEvent is null)
            {
                error = $"event {index} is null";
                return false;
            }
            var hasMilliseconds = replayEvent.AtMilliseconds.HasValue;
            var hasPresentedFrame = replayEvent.AtPresentedFrame.HasValue;
            if (hasMilliseconds == hasPresentedFrame)
            {
                error =
                    $"event {index} must specify exactly one replay clock";
                return false;
            }

            var eventClock = hasPresentedFrame
                ? ReplayClock.PresentedFrame
                : ReplayClock.ElapsedMilliseconds;
            if (replayClock is { } establishedClock &&
                eventClock != establishedClock)
            {
                error = "all replay events must use the same clock";
                return false;
            }
            replayClock ??= eventClock;

            var position = replayEvent.AtPresentedFrame ??
                replayEvent.AtMilliseconds!.Value;
            var maximumPosition = eventClock == ReplayClock.PresentedFrame
                ? MaximumPresentedFrame
                : MaximumDurationMilliseconds;
            if (position < 0 || position > maximumPosition)
            {
                error = eventClock == ReplayClock.PresentedFrame
                    ? $"event {index} has an out-of-range atPresentedFrame value"
                    : $"event {index} has an out-of-range atMilliseconds value";
                return false;
            }
            if (position <= previousPosition)
            {
                error = "event positions must be strictly increasing";
                return false;
            }
            if (!TryDecodeButtons(
                    replayEvent.Buttons,
                    out var buttons,
                    out var invalidButton))
            {
                error = $"event {index} has unknown button '{invalidButton}'";
                return false;
            }

            parsedEvents[index] = new PadReplayEvent(
                position,
                new PadState(
                    replayEvent.Connected,
                    buttons,
                    replayEvent.LeftX,
                    replayEvent.LeftY,
                    replayEvent.RightX,
                    replayEvent.RightY,
                    replayEvent.L2,
                    replayEvent.R2));
            previousPosition = position;
        }

        script = new PadReplayScript(parsedEvents, replayClock!.Value);
        return true;
    }

    internal static bool TryParseAutoCross(
        string? value,
        out PadReplayScript? script)
    {
        script = null;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var intervals = new List<AutoCrossInterval>();
        foreach (var token in value.Split(
                     ',',
                     StringSplitOptions.RemoveEmptyEntries |
                         StringSplitOptions.TrimEntries))
        {
            if (!double.TryParse(
                    token,
                    NumberStyles.Float,
                    CultureInfo.InvariantCulture,
                    out var seconds) ||
                !double.IsFinite(seconds) ||
                seconds < 0 ||
                seconds * 1000 > MaximumDurationMilliseconds - 400)
            {
                continue;
            }

            var start = checked((long)Math.Round(
                seconds * 1000,
                MidpointRounding.AwayFromZero));
            intervals.Add(new AutoCrossInterval(start, start + 400));
        }
        if (intervals.Count == 0)
        {
            return false;
        }

        intervals.Sort(static (left, right) =>
            left.StartMilliseconds.CompareTo(right.StartMilliseconds));
        var merged = new List<AutoCrossInterval>();
        var current = intervals[0];
        for (var index = 1; index < intervals.Count; index++)
        {
            var next = intervals[index];
            if (next.StartMilliseconds <= current.EndMilliseconds)
            {
                current = current with
                {
                    EndMilliseconds = Math.Max(
                        current.EndMilliseconds,
                        next.EndMilliseconds),
                };
                continue;
            }

            merged.Add(current);
            current = next;
        }
        merged.Add(current);
        if (merged.Count > MaximumEventCount / 2)
        {
            return false;
        }

        var events = new PadReplayEvent[merged.Count * 2];
        for (var index = 0; index < merged.Count; index++)
        {
            events[index * 2] = new PadReplayEvent(
                merged[index].StartMilliseconds,
                NeutralState with { Buttons = OrbisPadButton.Cross });
            events[(index * 2) + 1] = new PadReplayEvent(
                merged[index].EndMilliseconds,
                NeutralState);
        }

        script = new PadReplayScript(
            events,
            ReplayClock.ElapsedMilliseconds);
        return true;
    }

    private static bool TryDecodeButtons(
        string[]? names,
        out uint buttons,
        out string? invalidButton)
    {
        buttons = 0;
        invalidButton = null;
        if (names is null)
        {
            return false;
        }

        foreach (var name in names)
        {
            if (string.IsNullOrWhiteSpace(name) ||
                !ButtonBits.TryGetValue(name, out var bit))
            {
                invalidButton = name;
                return false;
            }
            buttons |= bit;
        }

        return true;
    }

    private sealed class PadReplayDocument
    {
        public PadReplayEventDocument[] Events { get; init; } = [];
    }

    private sealed class PadReplayEventDocument
    {
        public long? AtMilliseconds { get; init; }

        public long? AtPresentedFrame { get; init; }

        public string[] Buttons { get; init; } = [];

        public bool Connected { get; init; } = true;

        public byte LeftX { get; init; } = 128;

        public byte LeftY { get; init; } = 128;

        public byte RightX { get; init; } = 128;

        public byte RightY { get; init; } = 128;

        public byte L2 { get; init; }

        public byte R2 { get; init; }
    }

    private readonly record struct PadReplayEvent(
        long Position,
        PadState State);

    private enum ReplayClock
    {
        ElapsedMilliseconds,
        PresentedFrame,
    }

    private readonly record struct AutoCrossInterval(
        long StartMilliseconds,
        long EndMilliseconds);
}
