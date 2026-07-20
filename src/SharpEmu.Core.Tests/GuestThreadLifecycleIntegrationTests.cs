// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Core.Cpu.Native;
using SharpEmu.Core.Memory;
using SharpEmu.HLE;
using Xunit;

namespace SharpEmu.Core.Tests;

[Collection(PhysicalVirtualMemoryTestCollection.Name)]
public sealed class GuestThreadLifecycleIntegrationTests
{
    private const ulong CodeAddress = 0x0000_0008_2000_0000;
    private const ulong ThreadHandle = 0x1234_5678;

    [HostX64Fact]
    public async Task ReturningGuestThreadPublishesExitAfterBecomingJoinable()
    {
        if (await NativeTestProcess.RunIfNeededAsync(
                typeof(GuestThreadLifecycleIntegrationTests)))
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
        Assert.True(memory.TryWrite(CodeAddress, [0x31, 0xC0, 0xC3]));

        var modules = new ModuleManager();
        modules.Freeze();
        using var backend = new DirectExecutionBackend(modules);
        var creatorContext = new CpuContext(memory, Generation.Gen5);
        var exited = new TaskCompletionSource<ulong>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        void HandleGuestThreadExited(ulong threadHandle)
        {
            if (threadHandle == ThreadHandle)
            {
                exited.TrySetResult(threadHandle);
            }
        }

        GuestThreadExecution.GuestThreadExited += HandleGuestThreadExited;
        try
        {
            Assert.True(
                backend.TryStartThread(
                    creatorContext,
                    new GuestThreadStartRequest(
                        ThreadHandle,
                        CodeAddress,
                        Argument: 0,
                        AttributeAddress: 0,
                        Name: "lifecycle-test",
                        Priority: 700,
                        AffinityMask: 0),
                    out var startError),
                startError);
            Assert.True(
                backend.TryJoinThread(
                    creatorContext,
                    ThreadHandle,
                    out var returnValue,
                    out var joinError),
                joinError);
            Assert.Equal(0UL, returnValue);
            Assert.Equal(
                ThreadHandle,
                await exited.Task.WaitAsync(TimeSpan.FromSeconds(5)));
        }
        finally
        {
            GuestThreadExecution.GuestThreadExited -= HandleGuestThreadExited;
        }
    }
}
