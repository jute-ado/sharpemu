// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.Libs.Gpu;

internal static class GuestImageWriterTracker
{
    public static void RecordRenderTargets(
        IReadOnlyList<GuestRenderTarget> targets,
        Dictionary<ulong, long> writers,
        long sequence)
    {
        for (var index = 0; index < targets.Count; index++)
        {
            var address = targets[index].Address;
            if (address != 0)
            {
                writers[address] = sequence;
            }
        }
    }

    public static void RecordStorageTextures(
        IReadOnlyList<GuestDrawTexture> textures,
        Dictionary<ulong, long> writers,
        long sequence)
    {
        for (var index = 0; index < textures.Count; index++)
        {
            var texture = textures[index];
            if (texture.IsStorage && texture.Address != 0)
            {
                writers[texture.Address] = sequence;
            }
        }
    }
}
