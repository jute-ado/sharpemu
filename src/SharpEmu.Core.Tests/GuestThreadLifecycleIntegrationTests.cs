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
    private const ulong DetachedThreadHandle = 0x1234_5679;

    [HostX64Fact]
    public async Task ReturningGuestThreadFinishesExitCallbacksBeforeBecomingJoinable()
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
        var exiting = new TaskCompletionSource<(ulong Handle, ulong CurrentHandle, CpuContext Context)>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var allowExitCallbacks = new ManualResetEventSlim(initialState: false);

        void HandleGuestThreadExiting(ulong threadHandle, CpuContext context)
        {
            if (threadHandle == ThreadHandle)
            {
                exiting.TrySetResult((
                    threadHandle,
                    GuestThreadExecution.CurrentGuestThreadHandle,
                    context));
                allowExitCallbacks.Wait(TimeSpan.FromSeconds(5));
            }
        }

        void HandleGuestThreadExited(ulong threadHandle)
        {
            if (threadHandle == ThreadHandle)
            {
                exited.TrySetResult(threadHandle);
            }
        }

        GuestThreadExecution.GuestThreadExiting += HandleGuestThreadExiting;
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
            var joinStarted = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var join = Task.Run(() =>
            {
                joinStarted.TrySetResult();
                var joined = backend.TryJoinThread(
                    creatorContext,
                    ThreadHandle,
                    out var returnValue,
                    out var joinError);
                return (Joined: joined, ReturnValue: returnValue, Error: joinError);
            });
            await joinStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var exitingState = await exiting.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(ThreadHandle, exitingState.Handle);
            Assert.Equal(ThreadHandle, exitingState.CurrentHandle);
            Assert.Equal(Generation.Gen5, exitingState.Context.TargetGeneration);
            await Task.Delay(100);
            Assert.False(join.IsCompleted);

            allowExitCallbacks.Set();
            var joinResult = await join.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(joinResult.Joined, joinResult.Error);
            Assert.Equal(0UL, joinResult.ReturnValue);
            Assert.Equal(
                ThreadHandle,
                await exited.Task.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.True(backend.TryReapThread(ThreadHandle));
            Assert.False(
                backend.TryJoinThread(
                    creatorContext,
                    ThreadHandle,
                    out _,
                    out var repeatedJoinError));
            Assert.Contains("unknown guest thread", repeatedJoinError, StringComparison.Ordinal);
        }
        finally
        {
            allowExitCallbacks.Set();
            GuestThreadExecution.GuestThreadExiting -= HandleGuestThreadExiting;
            GuestThreadExecution.GuestThreadExited -= HandleGuestThreadExited;
        }
    }

    [HostX64Fact]
    public async Task ExitingThreadCanRequestReapingAfterLeavingItsWorkerPath()
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
        var reapRequested = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var reaped = new TaskCompletionSource<ulong>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        void HandleGuestThreadExited(ulong threadHandle)
        {
            if (threadHandle == DetachedThreadHandle)
            {
                reapRequested.TrySetResult(backend.RequestThreadReap(threadHandle));
            }
        }

        void HandleGuestThreadReaped(ulong threadHandle)
        {
            if (threadHandle == DetachedThreadHandle)
            {
                Assert.Equal(0UL, GuestThreadExecution.CurrentGuestThreadHandle);
                reaped.TrySetResult(threadHandle);
            }
        }

        GuestThreadExecution.GuestThreadExited += HandleGuestThreadExited;
        GuestThreadExecution.GuestThreadReaped += HandleGuestThreadReaped;
        try
        {
            Assert.True(
                backend.TryStartThread(
                    creatorContext,
                    new GuestThreadStartRequest(
                        DetachedThreadHandle,
                        CodeAddress,
                        Argument: 0,
                        AttributeAddress: 0,
                        Name: "detached-lifecycle-test",
                        Priority: 700,
                        AffinityMask: 0),
                    out var startError),
                startError);
            Assert.True(await reapRequested.Task.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.Equal(
                DetachedThreadHandle,
                await reaped.Task.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.False(
                backend.TryJoinThread(
                    creatorContext,
                    DetachedThreadHandle,
                    out _,
                    out var joinError));
            Assert.Contains("unknown guest thread", joinError, StringComparison.Ordinal);
        }
        finally
        {
            GuestThreadExecution.GuestThreadExited -= HandleGuestThreadExited;
            GuestThreadExecution.GuestThreadReaped -= HandleGuestThreadReaped;
        }
    }
}
