// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.Core.Testing;

/// <summary>
/// Describes the versioned, side-effect-free surface available to the shared
/// Emulator Test Lab before it launches private game content.
/// </summary>
public static class TestLabProbe
{
    public static bool IsRequest(IReadOnlyList<string> arguments) =>
        arguments.Count == 1 &&
        string.Equals(
            arguments[0],
            "--test-lab-probe",
            StringComparison.Ordinal);

    public static string CreateJson() =>
        """
        {
          "protocolVersion": 1,
          "emulator": "sharpemu",
          "adapterVersion": "1.0.0",
          "capabilities": [
            "bundle_fingerprint",
            "controller_recording",
            "controller_replay",
            "guest_image_write_capture",
            "presented_image_capture",
            "multi_presented_image_capture",
            "structured_report",
            "video_out_fingerprint"
          ]
        }
        """;
}
