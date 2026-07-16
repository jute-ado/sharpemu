// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Globalization;

namespace SharpEmu.Libs.VideoOut;

internal readonly record struct PresentedGuestImageCaptureRequest(long Frame)
{
    public bool IsEnabled => Frame > 0;

    public bool ShouldCapture(long frame) => IsEnabled && frame == Frame;

    public static bool TryParse(
        string? value,
        out PresentedGuestImageCaptureRequest request)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            request = default;
            return true;
        }

        if (!long.TryParse(
                value.AsSpan().Trim(),
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out var frame) ||
            frame <= 0)
        {
            request = default;
            return false;
        }

        request = new PresentedGuestImageCaptureRequest(frame);
        return true;
    }
}
