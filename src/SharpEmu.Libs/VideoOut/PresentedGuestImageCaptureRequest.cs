// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Globalization;

namespace SharpEmu.Libs.VideoOut;

internal readonly struct PresentedGuestImageCaptureRequest
{
    private const int MaximumFrameCount = 10_000;
    private readonly long[]? _frames;

    private PresentedGuestImageCaptureRequest(long[] frames)
    {
        _frames = frames;
    }

    public bool IsEnabled => _frames is { Length: > 0 };

    public bool ShouldCapture(long frame) =>
        _frames is not null &&
        Array.BinarySearch(_frames, frame) >= 0;

    public static bool TryParse(
        string? value,
        out PresentedGuestImageCaptureRequest request)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            request = default;
            return true;
        }

        var parts = value.Split(
            ',',
            StringSplitOptions.TrimEntries);
        if (parts.Length is < 1 or > MaximumFrameCount)
        {
            request = default;
            return false;
        }

        var frames = new long[parts.Length];
        long previous = 0;
        for (var index = 0; index < parts.Length; index++)
        {
            if (parts[index].Length == 0 ||
                !long.TryParse(
                    parts[index],
                    NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    out var frame) ||
                frame <= previous)
            {
                request = default;
                return false;
            }
            frames[index] = frame;
            previous = frame;
        }

        request = new PresentedGuestImageCaptureRequest(frames);
        return true;
    }
}
