// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Core.Cpu;
using SharpEmu.Core.Loader;
using SharpEmu.Core.Memory;
using Xunit;

namespace SharpEmu.Core.Tests;

public sealed class TrackedCpuMemoryTests
{
    [Fact]
    public void CompareForwardsToInnerMemoryWithoutRecordingMismatchAsFault()
    {
        var inner = new VirtualMemory();
        inner.Map(
            0x1000,
            0x1000,
            fileOffset: 0,
            [1, 2, 3, 4],
            ProgramHeaderFlags.Read | ProgramHeaderFlags.Write);
        var memory = new TrackedCpuMemory(inner);

        Assert.True(memory.TryCompare(0x1000, [1, 2, 3, 4]));
        Assert.False(memory.TryCompare(0x1000, [1, 2, 3, 5]));
        Assert.Null(memory.LastFailure);
    }
}
