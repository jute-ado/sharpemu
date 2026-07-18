// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.Libs.Gpu;

internal static class GuestComputeWorkPolicy
{
    internal static bool HasObservableOutput(
        IReadOnlyList<GuestDrawTexture> textures,
        bool writesGlobalMemory)
    {
        if (writesGlobalMemory)
        {
            return true;
        }

        foreach (var texture in textures)
        {
            if (texture.IsStorage)
            {
                return true;
            }
        }

        return false;
    }
}
