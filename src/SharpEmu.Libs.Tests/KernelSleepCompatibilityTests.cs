// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using System.Diagnostics;
using SharpEmu.HLE;
using SharpEmu.Libs.Kernel;
using Xunit;

namespace SharpEmu.Libs.Tests;

public sealed class KernelSleepCompatibilityTests
{
    private const ulong RequestAddress = 0x1000;
    private const ulong RemainAddress = 0x2000;

    [Fact]
    public void UsleepBlocksInPlaceUntilDeadline()
    {
        GuestThreadBlocking.BeginExecution();
        var context = new CpuContext(new FakeGuestMemory(), Generation.Gen5);
        context[CpuRegister.Rdi] = 20_000;
        var previousGuestThread = GuestThreadExecution.EnterGuestThread(0x1234);
        try
        {
            var stopwatch = Stopwatch.StartNew();

            Assert.Equal(
                (int)OrbisGen2Result.ORBIS_GEN2_OK,
                KernelRuntimeCompatExports.KernelUsleep(context));

            Assert.True(stopwatch.Elapsed >= TimeSpan.FromMilliseconds(10));
            Assert.Null(GuestThreadBlocking.DescribeBlock(0x1234));
        }
        finally
        {
            GuestThreadExecution.RestoreGuestThread(previousGuestThread);
        }
    }

    [Fact]
    public void NanosleepBlocksInPlaceAndClearsRemainder()
    {
        GuestThreadBlocking.BeginExecution();
        var memory = new FakeGuestMemory();
        var request = new byte[16];
        var remain = new byte[16];
        BinaryPrimitives.WriteInt64LittleEndian(
            request.AsSpan(sizeof(long)),
            20_000_000);
        remain.AsSpan().Fill(0xA5);
        memory.AddRegion(RequestAddress, request);
        memory.AddRegion(RemainAddress, remain);
        var context = new CpuContext(memory, Generation.Gen5);
        context[CpuRegister.Rdi] = RequestAddress;
        context[CpuRegister.Rsi] = RemainAddress;
        var previousGuestThread = GuestThreadExecution.EnterGuestThread(0x5678);
        try
        {
            var stopwatch = Stopwatch.StartNew();

            Assert.Equal(
                (int)OrbisGen2Result.ORBIS_GEN2_OK,
                KernelRuntimeCompatExports.KernelNanosleep(context));

            Assert.True(stopwatch.Elapsed >= TimeSpan.FromMilliseconds(10));
            Assert.All(remain, value => Assert.Equal(0, value));
            Assert.Null(GuestThreadBlocking.DescribeBlock(0x5678));
        }
        finally
        {
            GuestThreadExecution.RestoreGuestThread(previousGuestThread);
        }
    }

    [Fact]
    public async Task ShutdownUnwindsLongSleep()
    {
        GuestThreadBlocking.BeginExecution();
        var context = new CpuContext(new FakeGuestMemory(), Generation.Gen5);
        context[CpuRegister.Rdi] = ulong.MaxValue;
        var waitTask = Task.Run(() =>
        {
            var previous = GuestThreadExecution.EnterGuestThread(0x9ABC);
            try
            {
                return KernelRuntimeCompatExports.KernelUsleep(context);
            }
            finally
            {
                GuestThreadExecution.RestoreGuestThread(previous);
            }
        });

        Assert.True(SpinWait.SpinUntil(
            () => GuestThreadBlocking.DescribeBlock(0x9ABC) is not null,
            TimeSpan.FromSeconds(1)));
        GuestThreadBlocking.RequestShutdown();

        Assert.Equal(
            (int)OrbisGen2Result.ORBIS_GEN2_OK,
            await waitTask.WaitAsync(TimeSpan.FromSeconds(1)));
        Assert.Null(GuestThreadBlocking.DescribeBlock(0x9ABC));
        GuestThreadBlocking.BeginExecution();
    }
}
