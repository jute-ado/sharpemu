// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Core.Cpu.Native;
using Xunit;

namespace SharpEmu.Core.Tests;

public sealed class NativeGuestWorkerSchedulingTests
{
    [Fact]
    public void ReusedWorkerAppliesEveryGuestPriorityIncludingNormal()
    {
        var applied = new List<ThreadPriority>();

        NativeGuestWorkerScheduling.ApplyPriority(800, applied.Add);
        NativeGuestWorkerScheduling.ApplyPriority(700, applied.Add);
        NativeGuestWorkerScheduling.ApplyPriority(400, applied.Add);

        Assert.Equal(
            [ThreadPriority.Lowest, ThreadPriority.Normal, ThreadPriority.Highest],
            applied);
    }
}
