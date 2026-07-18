// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.Agc;
using System.Buffers.Binary;
using Xunit;

namespace SharpEmu.Libs.Tests;

public sealed class GuestDepthStateTests
{
    [Theory]
    [InlineData(0x41u, true)]
    [InlineData(0x40u, false)]
    public void DecodeDepthStateReadsControlAndClearBits(
        uint renderControl,
        bool clearEnable)
    {
        var registers = new Dictionary<uint, uint>
        {
            [0x000] = renderControl,
            [0x200] = 0x76,
        };

        var state = AgcExports.DecodeDepthState(registers);

        Assert.True(state.TestEnable);
        Assert.True(state.WriteEnable);
        Assert.Equal(7u, state.CompareOp);
        Assert.Equal(clearEnable, state.ClearEnable);
    }

    [Fact]
    public void DecodeDepthTargetPreservesAddressesExtentAndClearValue()
    {
        var registers = new Dictionary<uint, uint>
        {
            [0x000] = 1,
            [0x007] = 1919u | (1079u << 16),
            [0x00B] = BitConverter.SingleToUInt32Bits(0.25f),
            [0x010] = 1u | (4u << 4),
            [0x012] = 0x1234,
            [0x014] = 0x5678,
            [0x01A] = 2,
            [0x01C] = 3,
            [0x200] = 0x76,
        };
        var memory = new FakeGuestMemory();

        var target = Assert.IsType<SharpEmu.Libs.Gpu.GuestDepthTarget>(
            AgcExports.DecodeDepthTarget(registers, memory));

        Assert.Equal(1920u, target.Width);
        Assert.Equal(1080u, target.Height);
        Assert.Equal((2UL << 40) | (0x1234UL << 8), target.ReadAddress);
        Assert.Equal((3UL << 40) | (0x5678UL << 8), target.WriteAddress);
        Assert.Equal(4u, target.SwizzleMode);
        Assert.Equal(0.25f, target.ClearDepth);
        Assert.False(target.ReadOnly);
        Assert.Same(memory, target.GuestMemory);
    }

    [Fact]
    public void ReadLinearZ32DepthPreservesFloatingPointBits()
    {
        const ulong address = 0x0040_0000;
        var source = new byte[2 * sizeof(float)];
        BinaryPrimitives.WriteInt32LittleEndian(
            source.AsSpan(0, sizeof(float)),
            BitConverter.SingleToInt32Bits(0.25f));
        BinaryPrimitives.WriteInt32LittleEndian(
            source.AsSpan(sizeof(float), sizeof(float)),
            BitConverter.SingleToInt32Bits(0.75f));
        var memory = new FakeGuestMemory();
        memory.AddRegion(address, source);

        Assert.True(AgcExports.TryReadDepthPixels(
            memory,
            address,
            width: 2,
            height: 1,
            guestFormat: 3,
            swizzleMode: 0,
            out var pixels));

        Assert.Equal(source, pixels);
    }

    [Fact]
    public void ReadLinearZ16DepthPromotesToD32WithoutChangingRepresentedValue()
    {
        const ulong address = 0x0041_0000;
        ushort[] values = [0, 32768, ushort.MaxValue];
        var source = new byte[values.Length * sizeof(ushort)];
        for (var index = 0; index < values.Length; index++)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(
                source.AsSpan(index * sizeof(ushort), sizeof(ushort)),
                values[index]);
        }
        var memory = new FakeGuestMemory();
        memory.AddRegion(address, source);

        Assert.True(AgcExports.TryReadDepthPixels(
            memory,
            address,
            width: (uint)values.Length,
            height: 1,
            guestFormat: 1,
            swizzleMode: 0,
            out var pixels));

        for (var index = 0; index < values.Length; index++)
        {
            Assert.Equal(
                values[index] / (float)ushort.MaxValue,
                BitConverter.Int32BitsToSingle(
                    BinaryPrimitives.ReadInt32LittleEndian(
                        pixels.AsSpan(index * sizeof(float), sizeof(float)))));
        }
    }

    [Fact]
    public void ReadTiledZ32DepthUsesTheTrustedDepthEquation()
    {
        const ulong address = 0x0042_0000;
        float[] values = [0.125f, 0.25f, 0.5f, 1f];
        var source = new byte[64 * 1024];
        for (var index = 0; index < values.Length; index++)
        {
            BinaryPrimitives.WriteInt32LittleEndian(
                source.AsSpan(index * sizeof(float), sizeof(float)),
                BitConverter.SingleToInt32Bits(values[index]));
        }
        var memory = new FakeGuestMemory();
        memory.AddRegion(address, source);

        Assert.True(AgcExports.TryReadDepthPixels(
            memory,
            address,
            width: 2,
            height: 2,
            guestFormat: 3,
            swizzleMode: 24,
            out var pixels));

        Assert.Equal(values[0], ReadDepth(pixels, 0));
        Assert.Equal(values[1], ReadDepth(pixels, 1));
        Assert.Equal(values[2], ReadDepth(pixels, 2));
        Assert.Equal(values[3], ReadDepth(pixels, 3));
    }

    [Theory]
    [InlineData(0u, 0u)]
    [InlineData(2u, 0u)]
    [InlineData(3u, 12u)]
    public void ReadDepthRejectsUnsupportedFormatsOrLayouts(
        uint guestFormat,
        uint swizzleMode)
    {
        const ulong address = 0x0043_0000;
        var memory = new FakeGuestMemory();
        memory.AddRegion(address, new byte[64 * 1024]);

        Assert.False(AgcExports.TryReadDepthPixels(
            memory,
            address,
            width: 2,
            height: 2,
            guestFormat,
            swizzleMode,
            out var pixels));
        Assert.Empty(pixels);
    }

    private static float ReadDepth(byte[] pixels, int index) =>
        BitConverter.Int32BitsToSingle(
            BinaryPrimitives.ReadInt32LittleEndian(
                pixels.AsSpan(index * sizeof(float), sizeof(float))));
}
