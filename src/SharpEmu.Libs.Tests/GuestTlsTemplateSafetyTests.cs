// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Runtime.InteropServices;
using SharpEmu.HLE;
using Xunit;

namespace SharpEmu.Libs.Tests;

public sealed class GuestTlsTemplateSafetyTests : IDisposable
{
    public GuestTlsTemplateSafetyTests() => GuestTlsTemplate.Reset();

    public void Dispose() => GuestTlsTemplate.Reset();

    [Fact]
    public void RegistrationNormalizesAlignmentBiasBeforeLayoutArithmetic()
    {
        var offset = GuestTlsTemplate.RegisterModule(
            1,
            [0xA5],
            memorySize: 1,
            alignment: 0x10,
            alignmentBias: ulong.MaxValue);

        Assert.Equal(1UL, offset);
        Assert.Equal(0xFUL, unchecked(0UL - offset) & 0xFUL);
        Assert.Equal(offset, GuestTlsTemplate.RegisterModule(1, [0xA5], 1, 0x10, 0xF));
    }

    [Fact]
    public void RegistrationBuildsNonOverlappingVariantTwoLayout()
    {
        var first = GuestTlsTemplate.RegisterModule(1, [0x11, 0x22], 0x20, 0x10);
        var second = GuestTlsTemplate.RegisterModule(2, [0x33], 0x18, 0x20, 8);

        Assert.Equal(0x20UL, first);
        Assert.True(second >= first + 0x18);
        Assert.Equal(8UL, unchecked(0UL - second) & 0x1FUL);
        Assert.Equal(second, GuestTlsTemplate.StaticTlsSize);
        Assert.Equal(0x20UL, GuestTlsTemplate.MaximumAlignment);
    }

    [Fact]
    public void RegistrationClonesTemplateAndRejectsConflictingDuplicate()
    {
        byte[] image = [0x12, 0x34];
        var offset = GuestTlsTemplate.RegisterModule(1, image, 4, 4);
        image[0] = 0xFF;

        Assert.Equal(new byte[] { 0x12, 0x34 }, GuestTlsTemplate.InitImage);
        Assert.Equal(offset, GuestTlsTemplate.RegisterModule(1, [0x12, 0x34], 4, 4));
        Assert.Throws<InvalidOperationException>(
            () => GuestTlsTemplate.RegisterModule(1, [0x12, 0x35], 4, 4));
    }

    [Fact]
    public void FailedRegistrationDoesNotMutateRegistry()
    {
        GuestTlsTemplate.RegisterModule(1, [0x42], 1, 1);
        var generation = GuestTlsTemplate.Generation;

        Assert.Throws<InvalidOperationException>(
            () => GuestTlsTemplate.RegisterModule(2, [], GuestTlsTemplate.StartupStaticTlsReservation + 1, 1));

        Assert.Equal(generation, GuestTlsTemplate.Generation);
        Assert.Equal(1UL, GuestTlsTemplate.StaticTlsSize);
        Assert.False(GuestTlsTemplate.TryGetStaticOffset(2, out _));
    }

    [Fact]
    public void SeedThreadBlockInitializesStaticTlsAndGuestDtv()
    {
        const ulong threadPointer = 0x20_000;
        var memory = new FakeGuestMemory();
        memory.AddRegion(0x10_000, new byte[0x20_000]);
        var context = new CpuContext(memory, Generation.Gen5) { FsBase = threadPointer };
        var first = GuestTlsTemplate.RegisterModule(1, [0xAA, 0xBB], 4, 4);
        var second = GuestTlsTemplate.RegisterModule(2, [0xCC], 8, 8);

        GuestTlsTemplate.SeedThreadBlock(context, threadPointer);

        AssertBytes(memory, threadPointer - first, 0xAA, 0xBB, 0, 0);
        AssertBytes(memory, threadPointer - second, 0xCC, 0, 0, 0, 0, 0, 0, 0);
        Assert.True(context.TryReadUInt64(threadPointer + sizeof(ulong), out var dtvAddress));
        Assert.NotEqual(0UL, dtvAddress);
        Assert.Equal(unchecked((long)GuestTlsTemplate.Generation), Marshal.ReadInt64((nint)dtvAddress));
        Assert.Equal(2L, Marshal.ReadInt64((nint)dtvAddress, sizeof(ulong)));
        Assert.Equal(unchecked((long)(threadPointer - first)), Marshal.ReadInt64((nint)dtvAddress, 16));
        Assert.Equal(unchecked((long)(threadPointer - second)), Marshal.ReadInt64((nint)dtvAddress, 24));
    }

    [Fact]
    public void SeedThreadBlockRejectsMissingStaticTlsMapping()
    {
        const ulong threadPointer = 0x20_000;
        var memory = new FakeGuestMemory();
        memory.AddRegion(threadPointer, new byte[0x100]);
        var context = new CpuContext(memory, Generation.Gen5) { FsBase = threadPointer };
        GuestTlsTemplate.RegisterModule(1, [0xA5], 4, 4);

        var exception = Assert.Throws<InvalidOperationException>(
            () => GuestTlsTemplate.SeedThreadBlock(context, threadPointer));

        Assert.Contains("static TLS module 1", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ResolveAddressRequiresCanonicalThreadSeeding()
    {
        const ulong threadPointer = 0x20_000;
        var memory = new FakeGuestMemory();
        memory.AddRegion(0x10_000, new byte[0x20_000]);
        var context = new CpuContext(memory, Generation.Gen5) { FsBase = threadPointer };
        var staticOffset = GuestTlsTemplate.RegisterModule(1, [0xA5], 4, 4);

        Assert.Equal(0UL, GuestTlsTemplate.ResolveAddress(context, 1, 0));

        GuestTlsTemplate.SeedThreadBlock(context, threadPointer);

        Assert.Equal(threadPointer - staticOffset, GuestTlsTemplate.ResolveAddress(context, 1, 0));
    }

    [Fact]
    public void ModuleRegisteredAfterThreadSeedUsesInitializedDynamicEntry()
    {
        const ulong threadPointer = 0x20_000;
        var memory = new FakeGuestMemory();
        memory.AddRegion(0x10_000, new byte[0x20_000]);
        var context = new CpuContext(memory, Generation.Gen5) { FsBase = threadPointer };
        GuestTlsTemplate.RegisterModule(1, [0x11], 4, 4);
        GuestTlsTemplate.SeedThreadBlock(context, threadPointer);
        GuestTlsTemplate.RegisterModule(2, [0x7A], 4, 0x10);

        var address = GuestTlsTemplate.ResolveAddress(context, 2, 1);

        Assert.NotEqual(0UL, address);
        Assert.Equal(0, Marshal.ReadByte((nint)address));
        Assert.Equal(0x7A, Marshal.ReadByte((nint)(address - 1)));
        Assert.Equal(0UL, GuestTlsTemplate.ResolveAddress(context, 2, 4));
        Assert.Equal(0UL, GuestTlsTemplate.ResolveAddress(context, 99, 0));
    }

    [Fact]
    public void ResetRestoresEmptyRegistryDefaults()
    {
        GuestTlsTemplate.RegisterModule(1, [0x11], 8, 8);

        GuestTlsTemplate.Reset();

        Assert.Empty(GuestTlsTemplate.InitImage);
        Assert.Equal(0UL, GuestTlsTemplate.BlockSize);
        Assert.Equal(0UL, GuestTlsTemplate.StaticTlsSize);
        Assert.Equal(1UL, GuestTlsTemplate.Alignment);
        Assert.Equal(1UL, GuestTlsTemplate.MaximumAlignment);
        Assert.False(GuestTlsTemplate.TryGetStaticOffset(1, out _));
    }

    private static void AssertBytes(FakeGuestMemory memory, ulong address, params byte[] expected)
    {
        var actual = new byte[expected.Length];
        Assert.True(memory.TryRead(address, actual));
        Assert.Equal(expected, actual);
    }
}
