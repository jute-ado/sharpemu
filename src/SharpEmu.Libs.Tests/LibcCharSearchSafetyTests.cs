// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using SharpEmu.Libs.Kernel;
using Xunit;

namespace SharpEmu.Libs.Tests;

public sealed class LibcCharSearchSafetyTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CharacterSearchRejectsAddressWrapInsteadOfContinuingAtZero(bool reverse)
    {
        var memory = new FakeGuestMemory();
        memory.AddRegion(ulong.MaxValue, [(byte)'A']);
        memory.AddRegion(0, [(byte)'B', 0]);
        var context = CreateContext(memory, 'B');

        var result = reverse
            ? KernelMemoryCompatExports.Strrchr(context)
            : KernelMemoryCompatExports.Strchr(context);

        Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT, result);
    }

    [Theory]
    [InlineData('A')]
    [InlineData(0)]
    public void StrchrCanMatchFinalAddressSpaceByte(int needle)
    {
        var memory = new FakeGuestMemory();
        memory.AddRegion(ulong.MaxValue, [(byte)needle]);
        var context = CreateContext(memory, needle);

        Assert.Equal(0, KernelMemoryCompatExports.Strchr(context));
        Assert.Equal(ulong.MaxValue, context[CpuRegister.Rax]);
    }

    [Fact]
    public void StrrchrRequiresTerminatorAfterFinalByteMatch()
    {
        var memory = new FakeGuestMemory();
        memory.AddRegion(ulong.MaxValue, [(byte)'A']);
        var context = CreateContext(memory, 'A');

        Assert.Equal(
            (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT,
            KernelMemoryCompatExports.Strrchr(context));
    }

    private static CpuContext CreateContext(FakeGuestMemory memory, int needle)
    {
        var context = new CpuContext(memory, Generation.Gen5);
        context[CpuRegister.Rdi] = ulong.MaxValue;
        context[CpuRegister.Rsi] = unchecked((byte)needle);
        return context;
    }
}
