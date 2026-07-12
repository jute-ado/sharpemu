// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Text;
using SharpEmu.HLE;
using SharpEmu.Libs.Kernel;
using Xunit;

namespace SharpEmu.Libs.Tests;

public sealed class FormattedOutputTests
{
    private const ulong DestinationAddress = 0x1000;
    private const ulong FormatAddress = 0x2000;
    private const ulong QueryRegionAddress = 0x0000_0042_0000_0000;

    [Fact]
    public void SnprintfDoesNotPartiallyWriteWhenTerminatorIsOutOfRange()
    {
        var destination = Enumerable.Repeat((byte)0xCC, 5).ToArray();
        var context = CreateContext(destination, Encoding.UTF8.GetBytes("hello\0"));
        context[CpuRegister.Rdi] = DestinationAddress;
        context[CpuRegister.Rsi] = 6;
        context[CpuRegister.Rdx] = FormatAddress;

        var result = KernelMemoryCompatExports.Snprintf(context);

        Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT, result);
        Assert.All(destination, value => Assert.Equal(0xCC, value));
    }

    [Fact]
    public void SwprintfDoesNotPartiallyWriteWhenTerminatorIsOutOfRange()
    {
        var destination = Enumerable.Repeat((byte)0xCC, 10).ToArray();
        var context = CreateContext(destination, Encoding.Unicode.GetBytes("hello\0"));
        context[CpuRegister.Rdi] = DestinationAddress;
        context[CpuRegister.Rsi] = 6;
        context[CpuRegister.Rdx] = FormatAddress;

        var result = KernelMemoryCompatExports.Swprintf(context);

        Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT, result);
        Assert.All(destination, value => Assert.Equal(0xCC, value));
    }

    [Fact]
    public void SnprintfWritesContentAndTerminatorTogether()
    {
        var destination = Enumerable.Repeat((byte)0xCC, 6).ToArray();
        var context = CreateContext(destination, Encoding.UTF8.GetBytes("hello\0"));
        context[CpuRegister.Rdi] = DestinationAddress;
        context[CpuRegister.Rsi] = 6;
        context[CpuRegister.Rdx] = FormatAddress;

        var result = KernelMemoryCompatExports.Snprintf(context);

        Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_OK, result);
        Assert.Equal(Encoding.UTF8.GetBytes("hello\0"), destination);
        Assert.Equal(5UL, context[CpuRegister.Rax]);
    }

    [Fact]
    public void QueryMemoryProtectionValidatesAllOutputsBeforeWritingAny()
    {
        KernelMemoryCompatExports.RegisterReservedVirtualRange(
            QueryRegionAddress,
            0x1000);
        var startOutput = Enumerable.Repeat((byte)0xCC, sizeof(ulong)).ToArray();
        var memory = new FakeGuestMemory();
        memory.AddRegion(DestinationAddress, startOutput);
        var context = new CpuContext(memory, Generation.Gen5);
        context[CpuRegister.Rdi] = QueryRegionAddress + 1;
        context[CpuRegister.Rsi] = DestinationAddress;
        context[CpuRegister.Rdx] = 0xDEAD_0000;
        context[CpuRegister.Rcx] = 0;

        var result = KernelMemoryCompatExports.KernelQueryMemoryProtection(context);

        Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT, result);
        Assert.All(startOutput, value => Assert.Equal(0xCC, value));
    }

    private static CpuContext CreateContext(byte[] destination, byte[] format)
    {
        var memory = new FakeGuestMemory();
        memory.AddRegion(DestinationAddress, destination);
        memory.AddRegion(FormatAddress, format);
        return new CpuContext(memory, Generation.Gen5);
    }
}
