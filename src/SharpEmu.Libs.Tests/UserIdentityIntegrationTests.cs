// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using SharpEmu.HLE;
using SharpEmu.Libs.Mouse;
using SharpEmu.Libs.Pad;
using SharpEmu.Libs.UserService;
using Xunit;

namespace SharpEmu.Libs.Tests;

public sealed class UserIdentityIntegrationTests
{
    private const ulong UserIdAddress = 0x1000;
    private const ulong ImeParameterAddress = 0x2000;

    [Fact]
    public void InitialUserCanOpenPrimaryPad()
    {
        var context = CreateContext(out var userId);
        AssertCall(0, context, PadExports.PadInit);

        context[CpuRegister.Rdi] = unchecked((ulong)userId);
        context[CpuRegister.Rsi] = 0;
        context[CpuRegister.Rdx] = 0;
        context[CpuRegister.Rcx] = 0;

        AssertCall(1, context, PadExports.PadOpen);
    }

    [Fact]
    public void InitialUserCanOpenPrimaryMouse()
    {
        var context = CreateContext(out var userId);
        AssertCall(0, context, MouseExports.MouseInit);

        context[CpuRegister.Rdi] = unchecked((ulong)userId);
        context[CpuRegister.Rsi] = 0;
        context[CpuRegister.Rdx] = 0;

        AssertCall(0, context, MouseExports.MouseOpen);

        context[CpuRegister.Rdi] = 0;
        AssertCall(0, context, MouseExports.MouseClose);
    }

    [Fact]
    public void InitialUserCanOpenImeKeyboard()
    {
        var context = CreateContext(out var userId);
        context[CpuRegister.Rdi] = unchecked((ulong)userId);
        context[CpuRegister.Rsi] = ImeParameterAddress;

        AssertCall(0, context, ImeExports.ImeKeyboardOpen);
    }

    private static CpuContext CreateContext(out int userId)
    {
        var userIdBytes = new byte[sizeof(int)];
        var memory = new FakeGuestMemory();
        memory.AddRegion(UserIdAddress, userIdBytes);
        memory.AddRegion(ImeParameterAddress, new byte[1]);
        var context = new CpuContext(memory, Generation.Gen5);
        context[CpuRegister.Rdi] = UserIdAddress;

        AssertCall(0, context, UserServiceExports.UserServiceGetInitialUser);
        userId = BinaryPrimitives.ReadInt32LittleEndian(userIdBytes);
        return context;
    }

    private static void AssertCall(
        int expected,
        CpuContext context,
        Func<CpuContext, int> export)
    {
        var result = export(context);
        Assert.Equal(expected, result);
        Assert.Equal(unchecked((ulong)result), context[CpuRegister.Rax]);
    }
}
