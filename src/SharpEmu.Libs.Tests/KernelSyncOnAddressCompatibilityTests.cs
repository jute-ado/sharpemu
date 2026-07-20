// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Diagnostics;
using SharpEmu.HLE;
using SharpEmu.Libs.Kernel;
using Xunit;

namespace SharpEmu.Libs.Tests;

public sealed class KernelSyncOnAddressCompatibilityTests : IDisposable
{
    private const ulong MemoryBase = 0x1_0000_0000;
    private const ulong WaitAddress = MemoryBase + 0x100;
    private const ulong TimeoutAddress = MemoryBase + 0x200;

    public KernelSyncOnAddressCompatibilityTests() =>
        GuestThreadBlocking.BeginExecution();

    public void Dispose()
    {
        GuestThreadBlocking.RequestShutdown();
        GuestThreadBlocking.BeginExecution();
    }

    [Fact]
    public void WaitReturnsImmediatelyWhenValueDoesNotMatch()
    {
        var memory = CreateMemory(value: 2);
        var context = CreateWaitContext(memory, expected: 1);
        var stopwatch = Stopwatch.StartNew();

        var result =
            KernelSyncOnAddressCompatExports.SyncOnAddressWait(context);

        Assert.Equal(0, result);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task MatchingWaitBlocksUntilWake()
    {
        var memory = CreateMemory(value: 1);
        var waitContext = CreateWaitContext(memory, expected: 1);
        var waitTask = Task.Run(
            () => KernelSyncOnAddressCompatExports
                .SyncOnAddressWait(waitContext));
        Assert.True(
            SpinWait.SpinUntil(
                () => KernelSyncOnAddressCompatExports
                    .GetWaitingCountForTests(WaitAddress) == 1,
                TimeSpan.FromSeconds(2)));
        Assert.False(waitTask.IsCompleted);

        Assert.True(
            memory.TryWrite(
                WaitAddress,
                BitConverter.GetBytes(2u)));
        var wakeContext = new CpuContext(memory, Generation.Gen5);
        wakeContext[CpuRegister.Rdi] = WaitAddress;
        wakeContext[CpuRegister.Rsi] = 1;

        Assert.Equal(
            0,
            KernelSyncOnAddressCompatExports
                .SyncOnAddressWake(wakeContext));
        Assert.Equal(
            0,
            await waitTask.WaitAsync(TimeSpan.FromSeconds(2)));
    }

    [Fact]
    public async Task WakeOneReleasesExactlyOneRegisteredWaiter()
    {
        var memory = CreateMemory(value: 7);
        var first = Task.Run(
            () => KernelSyncOnAddressCompatExports.SyncOnAddressWait(
                CreateWaitContext(memory, expected: 7)));
        var second = Task.Run(
            () => KernelSyncOnAddressCompatExports.SyncOnAddressWait(
                CreateWaitContext(memory, expected: 7)));
        Assert.True(
            SpinWait.SpinUntil(
                () => KernelSyncOnAddressCompatExports
                    .GetWaitingCountForTests(WaitAddress) == 2,
                TimeSpan.FromSeconds(2)));

        var wakeContext = new CpuContext(memory, Generation.Gen5);
        wakeContext[CpuRegister.Rdi] = WaitAddress;
        wakeContext[CpuRegister.Rsi] = 1;
        Assert.Equal(
            0,
            KernelSyncOnAddressCompatExports
                .SyncOnAddressWake(wakeContext));
        Assert.True(
            SpinWait.SpinUntil(
                () => first.IsCompleted != second.IsCompleted,
                TimeSpan.FromSeconds(2)));

        wakeContext[CpuRegister.Rsi] = 0;
        Assert.Equal(
            0,
            KernelSyncOnAddressCompatExports
                .SyncOnAddressWake(wakeContext));
        var results = await Task.WhenAll(first, second)
            .WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(
            new[] { 0, 0 },
            results);
    }

    [Fact]
    public void TimedWaitReturnsTimedOutAndConsumesTimeout()
    {
        var memory = CreateMemory(value: 5);
        Assert.True(
            memory.TryWrite(
                TimeoutAddress,
                BitConverter.GetBytes(30_000u)));
        var context = CreateWaitContext(memory, expected: 5);
        context[CpuRegister.Rdx] = TimeoutAddress;
        var stopwatch = Stopwatch.StartNew();

        var result =
            KernelSyncOnAddressCompatExports.SyncOnAddressWait(context);

        Assert.Equal(
            (int)OrbisGen2Result.ORBIS_GEN2_ERROR_TIMED_OUT,
            result);
        Assert.InRange(
            stopwatch.Elapsed,
            TimeSpan.FromMilliseconds(20),
            TimeSpan.FromSeconds(2));
        Assert.True(
            KernelMemoryCompatExports.TryReadUInt32Compat(
                context,
                TimeoutAddress,
                out var remaining));
        Assert.Equal(0u, remaining);
    }

    [Theory]
    [InlineData(0UL)]
    [InlineData(WaitAddress + 1)]
    public void WaitAndWakeRejectInvalidAddresses(ulong address)
    {
        var memory = CreateMemory(value: 1);
        var context = new CpuContext(memory, Generation.Gen5);
        context[CpuRegister.Rdi] = address;

        Assert.Equal(
            (int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT,
            KernelSyncOnAddressCompatExports.SyncOnAddressWait(context));
        Assert.Equal(
            (int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT,
            KernelSyncOnAddressCompatExports.SyncOnAddressWake(context));
    }

    [Fact]
    public void ExportMetadataMatchesObservedKernelNids()
    {
        var exports = SharpEmu.Generated.SysAbiExportRegistry
            .CreateExports(Generation.Gen5);

        var wait = Assert.Single(
            exports,
            export => export.Nid == "Hc4CaR6JBL0");
        Assert.Equal("sceKernelSyncOnAddressWait", wait.Name);
        Assert.Equal("libKernel", wait.LibraryName);

        var wake = Assert.Single(
            exports,
            export => export.Nid == "q2y-wDIVWZA");
        Assert.Equal("sceKernelSyncOnAddressWake", wake.Name);
        Assert.Equal("libKernel", wake.LibraryName);
    }

    private static FakeCpuMemory CreateMemory(uint value)
    {
        var memory = new FakeCpuMemory(MemoryBase, 0x1000);
        Assert.True(
            memory.TryWrite(
                WaitAddress,
                BitConverter.GetBytes(value)));
        return memory;
    }

    private static CpuContext CreateWaitContext(
        FakeCpuMemory memory,
        uint expected)
    {
        var context = new CpuContext(memory, Generation.Gen5);
        context[CpuRegister.Rdi] = WaitAddress;
        context[CpuRegister.Rsi] = expected;
        return context;
    }
}
