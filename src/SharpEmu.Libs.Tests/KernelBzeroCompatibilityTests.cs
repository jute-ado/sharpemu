// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Reflection;
using SharpEmu.HLE;
using SharpEmu.Libs.Kernel;
using Xunit;

namespace SharpEmu.Libs.Tests;

public sealed class KernelBzeroCompatibilityTests
{
    private const ulong BufferAddress = 0x1000;
    private const byte Canary = 0xA5;

    [Fact]
    public void BzeroDoesNotTruncateLarge64BitLengthToNoOp()
    {
        var context = new CpuContext(new FakeGuestMemory(), Generation.Gen5);
        context[CpuRegister.Rdi] = BufferAddress;
        context[CpuRegister.Rsi] = 1UL << 63;

        var result = KernelSocketCompatExports.Bzero(context);

        AssertError(result, context, OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
    }

    [Fact]
    public void BzeroStreamsMultipleChunksAndPreservesAdjacentBytes()
    {
        const int zeroLength = 40_000;
        var memory = new FakeGuestMemory();
        var allocation = new byte[zeroLength + 16];
        Array.Fill(allocation, Canary);
        memory.AddRegion(BufferAddress, allocation);
        var context = new CpuContext(memory, Generation.Gen5);
        context[CpuRegister.Rdi] = BufferAddress + 8;
        context[CpuRegister.Rsi] = zeroLength;

        Assert.Equal(0, KernelSocketCompatExports.Bzero(context));
        Assert.Equal(0UL, context[CpuRegister.Rax]);
        Assert.All(allocation.AsSpan(0, 8).ToArray(), value => Assert.Equal(Canary, value));
        Assert.All(allocation.AsSpan(8, zeroLength).ToArray(), value => Assert.Equal(0, value));
        Assert.All(allocation.AsSpan(zeroLength + 8).ToArray(), value => Assert.Equal(Canary, value));
    }

    [Fact]
    public void BzeroAllowsZeroLengthWithNullAddress()
    {
        var context = new CpuContext(new FakeGuestMemory(), Generation.Gen5);

        Assert.Equal(0, KernelSocketCompatExports.Bzero(context));
        Assert.Equal(0UL, context[CpuRegister.Rax]);
    }

    [Fact]
    public void BzeroRejectsNullAddressForNonemptyRange()
    {
        var context = new CpuContext(new FakeGuestMemory(), Generation.Gen5);
        context[CpuRegister.Rsi] = 1;

        AssertError(
            KernelSocketCompatExports.Bzero(context),
            context,
            OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
    }

    [Fact]
    public void BzeroRejectsRangeCrossingAddressSpaceCeilingBeforeWriting()
    {
        var memory = new FakeGuestMemory();
        var backing = new[] { Canary, Canary };
        memory.AddRegion(ulong.MaxValue, backing);
        var context = new CpuContext(memory, Generation.Gen5);
        context[CpuRegister.Rdi] = ulong.MaxValue;
        context[CpuRegister.Rsi] = 2;

        AssertError(
            KernelSocketCompatExports.Bzero(context),
            context,
            OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        Assert.All(backing, value => Assert.Equal(Canary, value));
    }

    [Fact]
    public void BzeroReportsUnmappedRangeWithoutThrowing()
    {
        var context = new CpuContext(new FakeGuestMemory(), Generation.Gen5);
        context[CpuRegister.Rdi] = BufferAddress;
        context[CpuRegister.Rsi] = int.MaxValue;

        AssertError(
            KernelSocketCompatExports.Bzero(context),
            context,
            OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
    }

    [Fact]
    public void BzeroExportMetadataIsExact()
    {
        var method = typeof(KernelSocketCompatExports).GetMethod(
            nameof(KernelSocketCompatExports.Bzero),
            BindingFlags.Public | BindingFlags.Static);
        var attribute = method?.GetCustomAttribute<SysAbiExportAttribute>();

        Assert.NotNull(attribute);
        Assert.Equal("9oiX1kyeedA", attribute.Nid);
        Assert.Equal("bzero", attribute.ExportName);
        Assert.Equal("libKernel", attribute.LibraryName);
        Assert.Equal(Generation.Gen4 | Generation.Gen5, attribute.Target);
    }

    private static void AssertError(
        int result,
        CpuContext context,
        OrbisGen2Result expected)
    {
        Assert.Equal((int)expected, result);
        Assert.Equal(unchecked((ulong)(int)expected), context[CpuRegister.Rax]);
    }
}
