// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Text.Json;
using SharpEmu.Core.Testing;
using Xunit;

namespace SharpEmu.Core.Tests;

public sealed class TestLabProbeTests
{
    [Fact]
    public void RecognizesOnlyTheExactSideEffectFreeProbeRequest()
    {
        Assert.True(TestLabProbe.IsRequest(["--test-lab-probe"]));
        Assert.False(TestLabProbe.IsRequest([]));
        Assert.False(TestLabProbe.IsRequest(["--test-lab-probe", "game"]));
        Assert.False(TestLabProbe.IsRequest(["--TEST-LAB-PROBE"]));
    }

    [Fact]
    public void EmitsCanonicalProtocolOneCapabilityDocument()
    {
        using var document = JsonDocument.Parse(TestLabProbe.CreateJson());
        var root = document.RootElement;

        Assert.Equal(
            ["protocolVersion", "emulator", "adapterVersion", "capabilities"],
            root.EnumerateObject().Select(property => property.Name));
        Assert.Equal(1, root.GetProperty("protocolVersion").GetInt32());
        Assert.Equal("sharpemu", root.GetProperty("emulator").GetString());
        Assert.Equal("1.0.0", root.GetProperty("adapterVersion").GetString());
        Assert.Equal(
            [
                "bundle_fingerprint",
                "console_profile_ps5",
                "controller_recording",
                "controller_replay",
                "guest_image_write_capture",
                "presented_image_capture",
                "multi_presented_image_capture",
                "presented_frame_timing_trace",
                "render_resolution_scale",
                "strict_dynlib_resolution",
                "structured_report",
                "video_out_fingerprint",
            ],
            root.GetProperty("capabilities")
                .EnumerateArray()
                .Select(value => value.GetString()));
    }
}
