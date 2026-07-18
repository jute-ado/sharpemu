// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.Agc;
using SharpEmu.Libs.Gpu;
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
    public void DecodeDepthStatePreservesFrontAndBackStencilContracts()
    {
        var registers = new Dictionary<uint, uint>
        {
            [0x000] = 0x2,
            [0x00A] = 0x0000017F,
            [0x10B] = 0x00497565,
            [0x10C] = 0x78563412,
            [0x10D] = 0xEFCDAB90,
            [0x200] = 0x00600581,
        };

        var state = AgcExports.DecodeDepthState(registers);

        Assert.True(state.Stencil.TestEnable);
        Assert.True(state.Stencil.ClearEnable);
        Assert.Equal(0x7Fu, state.Stencil.ClearValue);
        Assert.Equal(
            new GuestStencilFaceState(
                FailOp: 5,
                PassOp: 6,
                DepthFailOp: 5,
                CompareOp: 5,
                CompareMask: 0x34,
                WriteMask: 0x56,
                Reference: 0x12,
                OperationValue: 0x78),
            state.Stencil.Front);
        Assert.Equal(
            new GuestStencilFaceState(
                FailOp: 7,
                PassOp: 9,
                DepthFailOp: 4,
                CompareOp: 6,
                CompareMask: 0xAB,
                WriteMask: 0xCD,
                Reference: 0x90,
                OperationValue: 0xEF),
            state.Stencil.Back);
    }

    [Fact]
    public void DecodeDepthStateReusesFrontStencilStateWhenBackfaceIsDisabled()
    {
        var registers = new Dictionary<uint, uint>
        {
            [0x10B] = 0x00000675,
            [0x10C] = 0x78563412,
            [0x10D] = 0xEFCDAB90,
            [0x200] = 0x00000301,
        };

        var state = AgcExports.DecodeDepthState(registers);

        Assert.Equal(state.Stencil.Front, state.Stencil.Back);
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
    public void DecodeDepthTargetPreservesSeparateStencilPlane()
    {
        var registers = new Dictionary<uint, uint>
        {
            [0x007] = 31u | (15u << 16),
            [0x010] = 3,
            [0x011] = 1u | (4u << 20),
            [0x012] = 0x1234,
            [0x013] = 0x2234,
            [0x014] = 0x1234,
            [0x015] = 0x2234,
            [0x01A] = 0x12,
            [0x01B] = 0x34,
            [0x01C] = 0x12,
            [0x01D] = 0x34,
            [0x200] = 0x1,
        };

        var target = Assert.IsType<GuestDepthTarget>(
            AgcExports.DecodeDepthTarget(registers));

        Assert.True(target.HasStencil);
        Assert.Equal(0x120000123400UL, target.ReadAddress);
        Assert.Equal(0x340000223400UL, target.StencilReadAddress);
        Assert.Equal(target.StencilReadAddress, target.StencilWriteAddress);
        Assert.Equal(1u, target.StencilFormat);
        Assert.Equal(4u, target.StencilSwizzleMode);
        Assert.Equal(0u, target.ClearStencil);
    }

    [Theory]
    [InlineData(0u)]
    [InlineData(2u)]
    public void DecodeDepthTargetRejectsActiveStencilWithoutS8Format(
        uint stencilFormat)
    {
        var registers = new Dictionary<uint, uint>
        {
            [0x007] = 15u | (15u << 16),
            [0x010] = 3,
            [0x011] = stencilFormat,
            [0x012] = 0x1234,
            [0x013] = 0x2234,
            [0x200] = 0x1,
        };

        Assert.Null(AgcExports.DecodeDepthTarget(registers));
    }

    [Theory]
    [InlineData(0u)]
    [InlineData(2u)]
    public void DecodeDepthTargetRejectsUnknownFormats(uint guestFormat)
    {
        var registers = new Dictionary<uint, uint>
        {
            [0x000] = 1,
            [0x007] = 15u | (15u << 16),
            [0x010] = guestFormat,
            [0x012] = 0x1234,
        };

        Assert.Null(AgcExports.DecodeDepthTarget(registers));
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
    public void ReadLinearZ16DepthPreservesNativeUnormBits()
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

        Assert.Equal(source, pixels);
    }

    [Fact]
    public void ReadLinearS8StencilPreservesNativeBytes()
    {
        const ulong address = 0x0041_8000;
        byte[] source = [0, 0x7F, byte.MaxValue];
        var memory = new FakeGuestMemory();
        memory.AddRegion(address, source);

        Assert.True(AgcExports.TryReadStencilPixels(
            memory,
            address,
            width: (uint)source.Length,
            height: 1,
            guestFormat: 1,
            swizzleMode: 0,
            out var pixels));

        Assert.Equal(source, pixels);
    }

    [Fact]
    public void ReadStencilRejectsUnknownFormat()
    {
        const ulong address = 0x0041_9000;
        var memory = new FakeGuestMemory();
        memory.AddRegion(address, new byte[4]);

        Assert.False(AgcExports.TryReadStencilPixels(
            memory,
            address,
            width: 2,
            height: 2,
            guestFormat: 0,
            swizzleMode: 0,
            out var pixels));
        Assert.Empty(pixels);
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
