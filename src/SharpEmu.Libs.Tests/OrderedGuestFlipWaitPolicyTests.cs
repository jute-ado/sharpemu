// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.VideoOut;
using Xunit;

namespace SharpEmu.Libs.Tests;

public sealed class OrderedGuestFlipWaitPolicyTests
{
    [Theory]
    [InlineData(0, 0, 0)]
    [InlineData(12, 0, 12)]
    [InlineData(0, 27, 27)]
    [InlineData(12, 27, 27)]
    [InlineData(41, 27, 41)]
    public void WaitDependsOnBothQueueAndReferencedFlip(
        long queueDependency,
        long referencedFlipSequence,
        long expected)
    {
        Assert.Equal(
            expected,
            OrderedGuestFlipWaitPolicy.ResolveRequiredSequence(
                queueDependency,
                referencedFlipSequence));
    }

    [Theory]
    [InlineData(0, 0, false, true)]
    [InlineData(27, 26, false, false)]
    [InlineData(27, 27, false, true)]
    [InlineData(27, 12, true, true)]
    public void WaitRunsOnlyAfterRequiredWorkCompletes(
        long requiredSequence,
        long contiguousCompletedSequence,
        bool completedOutOfOrder,
        bool expected)
    {
        IReadOnlySet<long> outOfOrder = completedOutOfOrder
            ? new HashSet<long> { requiredSequence }
            : new HashSet<long>();

        Assert.Equal(
            expected,
            OrderedGuestFlipWaitPolicy.IsRequiredSequenceCompleted(
                requiredSequence,
                contiguousCompletedSequence,
                outOfOrder));
    }
}
