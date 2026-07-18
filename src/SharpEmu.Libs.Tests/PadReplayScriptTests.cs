// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.Pad;
using Xunit;

namespace SharpEmu.Libs.Tests;

public sealed class PadReplayScriptTests
{
    [Fact]
    public void ReplayUsesLatestCompleteStateAtElapsedTime()
    {
        const string json =
            """
            {
              "events": [
                {
                  "atMilliseconds": 100,
                  "buttons": ["Cross", "Right"],
                  "leftX": 255,
                  "leftY": 64,
                  "rightX": 32,
                  "rightY": 224,
                  "l2": 7,
                  "r2": 9
                },
                {
                  "atMilliseconds": 500,
                  "buttons": []
                }
              ]
            }
            """;

        Assert.True(PadReplayScript.TryParse(
            json,
            out var replay,
            out var error), error);
        Assert.NotNull(replay);

        AssertNeutral(replay.GetState(99));
        var pressed = replay.GetState(100);
        Assert.True(pressed.Connected);
        Assert.Equal(
            OrbisPadButton.Cross | OrbisPadButton.Right,
            pressed.Buttons);
        Assert.Equal(255, pressed.LeftX);
        Assert.Equal(64, pressed.LeftY);
        Assert.Equal(32, pressed.RightX);
        Assert.Equal(224, pressed.RightY);
        Assert.Equal(7, pressed.L2);
        Assert.Equal(9, pressed.R2);
        Assert.Equal(pressed, replay.GetState(499));
        AssertNeutral(replay.GetState(500));
    }

    [Fact]
    public void ReplayCanRepresentDisconnectedInput()
    {
        const string json =
            """
            {
              "events": [
                {
                  "atMilliseconds": 0,
                  "buttons": [],
                  "connected": false
                }
              ]
            }
            """;

        Assert.True(PadReplayScript.TryParse(
            json,
            out var replay,
            out var error), error);

        Assert.False(Assert.IsType<PadReplayScript>(replay)
            .GetState(0)
            .Connected);
    }

    [Theory]
    [InlineData("""{"events":[]}""", "at least one event")]
    [InlineData("""{"events":[null]}""", "event 0 is null")]
    [InlineData(
        """{"events":[{"atMilliseconds":0,"buttons":["Share"]}]}""",
        "unknown button")]
    [InlineData(
        """
        {"events":[
          {"atMilliseconds":100,"buttons":[]},
          {"atMilliseconds":100,"buttons":[]}
        ]}
        """,
        "strictly increasing")]
    [InlineData(
        """{"events":[{"atMilliseconds":-1,"buttons":[]}]}""",
        "out-of-range")]
    public void ReplayRejectsInvalidContracts(string json, string expectedError)
    {
        Assert.False(PadReplayScript.TryParse(
            json,
            out var replay,
            out var error));
        Assert.Null(replay);
        Assert.Contains(expectedError, error, StringComparison.Ordinal);
    }

    [Fact]
    public void LegacyAutoCrossUsesCanonicalReplayTimeline()
    {
        Assert.True(PadReplayScript.TryParseAutoCross(
            "1.0, 1.2, invalid, 2.0",
            out var replay));
        Assert.NotNull(replay);

        AssertNeutral(replay.GetState(999));
        Assert.Equal(OrbisPadButton.Cross, replay.GetState(1000).Buttons);
        Assert.Equal(OrbisPadButton.Cross, replay.GetState(1599).Buttons);
        AssertNeutral(replay.GetState(1600));
        Assert.Equal(OrbisPadButton.Cross, replay.GetState(2000).Buttons);
        AssertNeutral(replay.GetState(2400));
    }

    private static void AssertNeutral(PadState state)
    {
        Assert.True(state.Connected);
        Assert.Equal(0u, state.Buttons);
        Assert.Equal(128, state.LeftX);
        Assert.Equal(128, state.LeftY);
        Assert.Equal(128, state.RightX);
        Assert.Equal(128, state.RightY);
        Assert.Equal(0, state.L2);
        Assert.Equal(0, state.R2);
    }
}
