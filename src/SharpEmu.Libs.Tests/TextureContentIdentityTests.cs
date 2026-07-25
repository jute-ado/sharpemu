// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.Gpu;
using Xunit;

namespace SharpEmu.Libs.Tests;

public sealed class TextureContentIdentityTests
{
    [Fact]
    public void FromGuestTexturePreservesCompleteDescriptorShape()
    {
        var sampler = new GuestSampler(1, 2, 3, 4);
        var texture = new GuestDrawTexture(
            Address: 0x6000,
            Width: 64,
            Height: 32,
            Format: 10,
            NumberType: 7,
            RgbaPixels: [],
            IsFallback: false,
            IsStorage: false,
            Pitch: 80,
            TileMode: 13,
            DstSelect: 0xFAC,
            Sampler: sampler,
            ArrayedView: true,
            ArrayLayers: 6,
            Depth: 4,
            ThreeDimensionalView: true,
            CubeView: true);

        Assert.Equal(
            new TextureContentIdentity(
                Address: 0x6000,
                Width: 64,
                Height: 32,
                Format: 10,
                NumberType: 7,
                DstSelect: 0xFAC,
                TileMode: 13,
                Pitch: 80,
                Sampler: sampler,
                Arrayed: true,
                ArrayLayers: 6,
                Depth: 4,
                ThreeDimensional: true,
                Cube: true),
            TextureContentIdentity.FromGuestTexture(texture));
    }

    [Fact]
    public void FromGuestTextureNormalizesZeroLayerAndDepthCounts()
    {
        var texture = new GuestDrawTexture(
            Address: 0x7000,
            Width: 1,
            Height: 1,
            Format: 1,
            NumberType: 0,
            RgbaPixels: [],
            IsFallback: false,
            IsStorage: false,
            ArrayLayers: 0,
            Depth: 0);

        var identity = TextureContentIdentity.FromGuestTexture(texture);

        Assert.Equal(1u, identity.ArrayLayers);
        Assert.Equal(1u, identity.Depth);
    }
}
