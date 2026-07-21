// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using SharpEmu.HLE;
using SharpEmu.Libs.UserService;
using Xunit;

namespace SharpEmu.Libs.Tests;

public sealed class UserServiceGamePresetsTests
{
    private const ulong PresetsAddress = 0x1000;
    private const int PresetsSize = 40;

    [Fact]
    public void GamePresetsUsesSecondArgumentAndWritesCompleteDefaults()
    {
        var presets = Enumerable.Repeat((byte)0xCC, PresetsSize).ToArray();
        BinaryPrimitives.WriteUInt64LittleEndian(presets, PresetsSize);
        var context = CreateContext(presets);
        context[CpuRegister.Rcx] = 0;

        var result = UserServiceExports.UserServiceGetGamePresets(context);

        Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_OK, result);
        Assert.Equal(0UL, context[CpuRegister.Rax]);
        Assert.Equal((ulong)PresetsSize, BinaryPrimitives.ReadUInt64LittleEndian(presets));
        Assert.All(presets.AsSpan(sizeof(ulong)).ToArray(), value => Assert.Equal(0, value));
    }

    [Fact]
    public void GamePresetsHonorsCallerStructureSize()
    {
        var presets = Enumerable.Repeat((byte)0xCC, PresetsSize).ToArray();
        BinaryPrimitives.WriteUInt64LittleEndian(presets, 16);
        var context = CreateContext(presets);

        var result = UserServiceExports.UserServiceGetGamePresets(context);

        Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_OK, result);
        Assert.Equal((ulong)PresetsSize, BinaryPrimitives.ReadUInt64LittleEndian(presets));
        Assert.All(presets.AsSpan(sizeof(ulong), 8).ToArray(), value => Assert.Equal(0, value));
        Assert.All(presets.AsSpan(16).ToArray(), value => Assert.Equal(0xCC, value));
    }

    [Fact]
    public void GamePresetsReturnsMemoryFaultForUnmappedOutput()
    {
        var context = new CpuContext(new FakeGuestMemory(), Generation.Gen5);
        context[CpuRegister.Rdi] = EmulatedUser.PrimaryId;
        context[CpuRegister.Rsi] = PresetsAddress;

        var result = UserServiceExports.UserServiceGetGamePresets(context);

        Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT, result);
        Assert.Equal(
            unchecked((ulong)(int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT),
            context[CpuRegister.Rax]);
    }

    private static CpuContext CreateContext(byte[] presets)
    {
        var memory = new FakeGuestMemory();
        memory.AddRegion(PresetsAddress, presets);
        var context = new CpuContext(memory, Generation.Gen5);
        context[CpuRegister.Rdi] = EmulatedUser.PrimaryId;
        context[CpuRegister.Rsi] = PresetsAddress;
        return context;
    }
}
