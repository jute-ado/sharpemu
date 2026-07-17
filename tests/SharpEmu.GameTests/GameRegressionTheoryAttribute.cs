// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Runtime.InteropServices;
using Xunit;

namespace SharpEmu.GameTests;

internal sealed class GameRegressionTheoryAttribute : TheoryAttribute
{
    public GameRegressionTheoryAttribute()
    {
        if (RuntimeInformation.ProcessArchitecture != Architecture.X64)
        {
            Skip = "Native game regressions require an x64 process.";
            return;
        }

        if (!File.Exists(GameRegressionPaths.ManifestPath))
        {
            Skip =
                "No local game manifest is configured. Copy games.example.json " +
                "to games.local.json or set SHARPEMU_GAME_TEST_MANIFEST.";
        }
    }
}
