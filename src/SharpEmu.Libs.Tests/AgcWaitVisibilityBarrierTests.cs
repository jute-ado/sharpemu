// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.Agc;
using SharpEmu.Libs.VideoOut;
using Xunit;

namespace SharpEmu.Libs.Tests;

public sealed class AgcWaitVisibilityBarrierTests
{
    [Theory]
    [InlineData(false, "dcb.graphics")]
    [InlineData(true, null)]
    public void OrderedActionWritebackScopeSelectsActiveOrAllCompletedQueues(
        bool publishAllCompletedQueues,
        string? expectedQueue)
    {
        Assert.Equal(
            expectedQueue,
            OrderedGuestActionWritebackPolicy.SelectQueue(
                "dcb.graphics",
                publishAllCompletedQueues));
    }

    [Fact]
    public void QueuedBarrierSignalsOnlyAfterEarlierGpuWorkBecomesVisible()
    {
        Action? queuedAction = null;
        string? queuedName = null;
        var signals = 0;

        var sequence = AgcExports.SubmitWaitVisibilityBarrier(
            (action, name) =>
            {
                queuedAction = action;
                queuedName = name;
                return 42;
            },
            () => signals++,
            0x1234,
            "acb.compute[56]",
            28);

        Assert.Equal(42, sequence);
        Assert.NotNull(queuedAction);
        Assert.Contains("0x0000000000001234", queuedName);
        Assert.Contains("acb.compute[56]", queuedName);
        Assert.Contains("submission 28", queuedName);
        Assert.Equal(0, signals);

        queuedAction();

        Assert.Equal(1, signals);
    }

    [Fact]
    public void HeadlessBarrierSignalsInlineWhenNoGpuWorkCanBeQueued()
    {
        var signals = 0;

        var sequence = AgcExports.SubmitWaitVisibilityBarrier(
            (_, _) => 0,
            () => signals++,
            0x5678,
            "dcb.graphics",
            37);

        Assert.Equal(0, sequence);
        Assert.Equal(1, signals);
    }

    [Fact]
    public void VisibilityCallbackLatchesTransientShaderValueBeforeGuestReset()
    {
        var memory = new object();
        var waiter = new GpuWaitRegistry.WaitingDcb
        {
            Memory = memory,
            WaitAddress = 0x9000,
            ReferenceValue = 1,
            Mask = uint.MaxValue,
            CompareFunction = 3,
        };

        GpuWaitRegistry.Clear();
        try
        {
            GpuWaitRegistry.Register(waiter.WaitAddress, waiter);

            Assert.True(AgcExports.ObserveWaitVisibility(
                memory,
                waiter,
                (_, _) => 1));

            // The guest immediately recycles the label to zero. The wait must
            // still consume the value observed at its ordered GPU barrier.
            var resumed = GpuWaitRegistry.CollectSatisfied(memory, (_, _) => 0);
            Assert.NotNull(resumed);
            Assert.Single(resumed);
        }
        finally
        {
            GpuWaitRegistry.Clear();
        }
    }

    [Fact]
    public void IndirectDispatchRetryExpiresAtItsBoundedDeadline()
    {
        var memory = new object();
        var waiter = new GpuWaitRegistry.WaitingDcb
        {
            Memory = memory,
            WaitAddress = 0xA000,
            RetryDeadlineTicks = 100,
        };

        GpuWaitRegistry.Clear();
        try
        {
            GpuWaitRegistry.Register(waiter.WaitAddress, waiter);

            Assert.Null(GpuWaitRegistry.CollectExpiredRetries(memory, 99));
            Assert.Equal(1, GpuWaitRegistry.CountForMemory(memory));
            var expired = GpuWaitRegistry.CollectExpiredRetries(memory, 100);
            Assert.NotNull(expired);
            Assert.Single(expired);
            Assert.Equal(0, GpuWaitRegistry.CountForMemory(memory));
        }
        finally
        {
            GpuWaitRegistry.Clear();
        }
    }

    [Fact]
    public void EmptyIndirectDispatchExpiresAtOrderedVisibilityPoint()
    {
        var memory = new object();
        var waiter = new GpuWaitRegistry.WaitingDcb
        {
            Memory = memory,
            WaitAddress = 0xB000,
            ResumeAddress = 0xC000,
            RetryDeadlineTicks = 10_000,
        };

        GpuWaitRegistry.Clear();
        try
        {
            GpuWaitRegistry.Register(waiter.WaitAddress, waiter);

            Assert.True(GpuWaitRegistry.ExpireRetryAtVisibilityPoint(
                memory,
                waiter.WaitAddress,
                waiter.ResumeAddress));
            var expired = GpuWaitRegistry.CollectExpiredRetries(memory, 1);
            Assert.NotNull(expired);
            Assert.Single(expired);
        }
        finally
        {
            GpuWaitRegistry.Clear();
        }
    }
}
