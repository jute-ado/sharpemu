// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using SharpEmu.Libs.Ime;
using Xunit;

namespace SharpEmu.Libs.Tests;

public sealed class ImeKeyboardInfoTests
{
    private const ulong BufferAddress = 0x4000;
    private const int KeyboardInfoSize = 36;

    [Fact]
    public void KeyboardGetInfoZeroesOnlyTheGuestInfoStructure()
    {
        var bytes = Enumerable.Repeat((byte)0xCC, KeyboardInfoSize + 2).ToArray();
        var memory = new FakeGuestMemory();
        memory.AddRegion(BufferAddress, bytes);
        var context = new CpuContext(memory, Generation.Gen5);
        context[CpuRegister.Rdi] = 17;
        context[CpuRegister.Rsi] = BufferAddress + 1;

        var result = ImeExports.ImeKeyboardGetInfo(context);

        Assert.Equal(0, result);
        Assert.Equal(0UL, context[CpuRegister.Rax]);
        Assert.Equal(0xCC, bytes[0]);
        Assert.All(bytes.AsSpan(1, KeyboardInfoSize).ToArray(), value => Assert.Equal(0, value));
        Assert.Equal(0xCC, bytes[^1]);
    }

    [Fact]
    public void KeyboardGetInfoAcceptsNullOutputForCompatibility()
    {
        var context = new CpuContext(new FakeGuestMemory(), Generation.Gen5);
        context[CpuRegister.Rdi] = 17;
        context[CpuRegister.Rsi] = 0;

        var result = ImeExports.ImeKeyboardGetInfo(context);

        Assert.Equal(0, result);
        Assert.Equal(0UL, context[CpuRegister.Rax]);
    }
}
