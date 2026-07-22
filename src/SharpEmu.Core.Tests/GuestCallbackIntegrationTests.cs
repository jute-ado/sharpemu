// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Core.Cpu.Native;
using SharpEmu.Core.Memory;
using SharpEmu.HLE;
using Xunit;

namespace SharpEmu.Core.Tests;

[Collection(PhysicalVirtualMemoryTestCollection.Name)]
public sealed class GuestCallbackIntegrationTests
{
    private const ulong CodeAddress = 0x0000_0008_3000_0000;
    private const ulong CallbackStackAddress = 0x0000_7FFE_1000_0000;
    private const ulong CallbackStackSize = 0x1_0000;

    [HostX64Fact]
    public async Task CallbackCarriesThreeArgumentsAndFullWidthReturnValue()
    {
        if (await NativeTestProcess.RunIfNeededAsync(
                typeof(GuestCallbackIntegrationTests)))
        {
            return;
        }

        using var memory = new PhysicalVirtualMemory();
        Assert.Equal(
            CodeAddress,
            memory.AllocateAt(
                CodeAddress,
                0x1000,
                executable: true,
                allowAlternative: false));
        Assert.True(memory.TryWrite(
            CodeAddress,
            [
                0x48, 0x89, 0xF8, // mov rax, rdi
                0x48, 0x01, 0xF0, // add rax, rsi
                0x48, 0x01, 0xD0, // add rax, rdx
                0xC3,             // ret
            ]));
        Assert.Equal(
            CallbackStackAddress,
            memory.AllocateAt(
                CallbackStackAddress,
                CallbackStackSize,
                executable: false,
                allowAlternative: false));

        var modules = new ModuleManager();
        modules.Freeze();
        using var backend = new DirectExecutionBackend(modules);
        IGuestThreadScheduler scheduler = backend;
        var caller = new CpuContext(memory, Generation.Gen5);
        const ulong arg0 = 0x1234_5678_0000_0000;
        const ulong arg1 = 0x1111;
        const ulong arg2 = 0x2222;

        Assert.True(
            scheduler.TryCallGuestFunction(
                caller,
                CodeAddress,
                arg0,
                arg1,
                arg2,
                stackAddress: CallbackStackAddress,
                stackSize: CallbackStackSize,
                reason: "three-argument-callback",
                returnValue: out var returnValue,
                error: out var error),
            error);
        Assert.Equal(arg0 + arg1 + arg2, returnValue);
        Assert.True(memory.TryGetStackRange(CallbackStackAddress, out var stackStart, out var stackEnd));
        Assert.Equal(CallbackStackAddress, stackStart);
        Assert.Equal(CallbackStackAddress + CallbackStackSize, stackEnd);
    }

    [HostX64Fact]
    public async Task RepeatedImplicitCallbacksReuseIsolatedStack()
    {
        if (await NativeTestProcess.RunIfNeededAsync(
                typeof(GuestCallbackIntegrationTests)))
        {
            return;
        }

        using var memory = new PhysicalVirtualMemory();
        Assert.Equal(
            CodeAddress,
            memory.AllocateAt(
                CodeAddress,
                0x1000,
                executable: true,
                allowAlternative: false));
        Assert.True(memory.TryWrite(
            CodeAddress,
            [
                0x48, 0x89, 0xF8, // mov rax, rdi
                0xC3,             // ret
            ]));
        Assert.Equal(
            CallbackStackAddress,
            memory.AllocateAt(
                CallbackStackAddress,
                CallbackStackSize,
                executable: false,
                allowAlternative: false));
        memory.RegisterStackRange(
            CallbackStackAddress,
            CallbackStackSize);

        var modules = new ModuleManager();
        modules.Freeze();
        using var backend = new DirectExecutionBackend(modules);
        IGuestThreadScheduler scheduler = backend;
        var caller = new CpuContext(memory, Generation.Gen5);
        caller[CpuRegister.Rsp] = CallbackStackAddress + CallbackStackSize - 0x80;

        // The native backend reserves 256 guest-thread stack slots. Implicit,
        // synchronous callbacks must not consume one slot per invocation.
        for (ulong value = 1; value <= 257; value++)
        {
            Assert.True(
                scheduler.TryCallGuestFunction(
                    caller,
                    CodeAddress,
                    value,
                    arg1: 0,
                    arg2: 0,
                    stackAddress: 0,
                    stackSize: 0,
                    reason: "implicit-stack-callback",
                    returnValue: out var returnValue,
                    error: out var error),
                error);
            Assert.Equal(value, returnValue);
        }
    }
}
