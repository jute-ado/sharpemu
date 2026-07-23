// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using SharpEmu.Libs.Np;
using Xunit;

namespace SharpEmu.Libs.Tests;

public sealed class NpTrophy2InfoTests
{
    private const ulong OutputAddress = 0x1000;

    [Fact]
    public void GetTrophyInfoArrayReportsUnavailableWithoutFabricatingOutput()
    {
        var output = Enumerable.Repeat((byte)0xA5, 0x2000).ToArray();
        var memory = new FakeGuestMemory();
        memory.AddRegion(OutputAddress, output);
        var context = new CpuContext(memory, Generation.Gen5);

        // Observed Gen5 call shape: context, handle, start index, requested count,
        // optional details pointer, then the caller-owned result array.
        context[CpuRegister.Rdi] = 1;
        context[CpuRegister.Rsi] = 1;
        context[CpuRegister.Rdx] = 0;
        context[CpuRegister.Rcx] = 0xA0;
        context[CpuRegister.R8] = 0;
        context[CpuRegister.R9] = OutputAddress;

        var result = NpTrophy2Exports.NpTrophy2GetTrophyInfoArray(context);

        Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_ERROR_NOT_FOUND, result);
        Assert.Equal(unchecked((ulong)result), context[CpuRegister.Rax]);
        Assert.All(output, value => Assert.Equal(0xA5, value));
    }

    [Fact]
    public void TrophyInfoQueriesUseTheSameUnavailableContract()
    {
        var context = new CpuContext(new FakeGuestMemory(), Generation.Gen5);

        var singleResult = NpTrophy2Exports.NpTrophy2GetTrophyInfo(context);
        var arrayResult = NpTrophy2Exports.NpTrophy2GetTrophyInfoArray(context);

        Assert.Equal(singleResult, arrayResult);
        Assert.Equal(unchecked((ulong)arrayResult), context[CpuRegister.Rax]);
    }

    [Fact]
    public void GetTrophyInfoArrayExportMetadataIsExact()
    {
        ExportMetadataAssert.Exact(
            "y3zHpdZO6ME",
            "sceNpTrophy2GetTrophyInfoArray",
            "libSceNpTrophy2",
            Generation.Gen5);
    }
}
