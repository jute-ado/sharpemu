// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using System.Runtime.InteropServices;
using SharpEmu.HLE;
using SharpEmu.Libs.Kernel;
using Xunit;

namespace SharpEmu.Libs.Tests;

public sealed class LibcTimeTests
{
    private const ulong TimeAddress = 0x1000;

    [Fact]
    public void TimeWithNullDestinationReturnsCurrentUnixTime()
    {
        var context = new CpuContext(new FakeGuestMemory(), Generation.Gen5);
        var before = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        var result = KernelMemoryCompatExports.Time(context);

        var after = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_OK, result);
        Assert.InRange(unchecked((long)context[CpuRegister.Rax]), before, after);
    }

    [Fact]
    public void TimeWritesCurrentUnixTimeToGuestMemory()
    {
        var buffer = new byte[sizeof(long)];
        var memory = new FakeGuestMemory();
        memory.AddRegion(TimeAddress, buffer);
        var context = new CpuContext(memory, Generation.Gen5);
        context[CpuRegister.Rdi] = TimeAddress;
        var before = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        var result = KernelMemoryCompatExports.Time(context);

        var after = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var returned = unchecked((long)context[CpuRegister.Rax]);
        Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_OK, result);
        Assert.InRange(returned, before, after);
        Assert.Equal(returned, BinaryPrimitives.ReadInt64LittleEndian(buffer));
    }

    [Fact]
    public void TimeWritesToTrackedLibcAllocation()
    {
        var context = new CpuContext(new FakeGuestMemory(), Generation.Gen5);
        context[CpuRegister.Rdi] = sizeof(long);
        Assert.Equal(
            (int)OrbisGen2Result.ORBIS_GEN2_OK,
            KernelMemoryCompatExports.Malloc(context));
        var destination = context[CpuRegister.Rax];
        Assert.NotEqual(0UL, destination);

        try
        {
            context[CpuRegister.Rdi] = destination;

            Assert.Equal(
                (int)OrbisGen2Result.ORBIS_GEN2_OK,
                KernelMemoryCompatExports.Time(context));

            Assert.Equal(
                unchecked((long)context[CpuRegister.Rax]),
                Marshal.ReadInt64((nint)destination));
        }
        finally
        {
            context[CpuRegister.Rdi] = destination;
            _ = KernelMemoryCompatExports.Free(context);
        }
    }

    [Fact]
    public void TimeRejectsUnmappedDestination()
    {
        var context = new CpuContext(new FakeGuestMemory(), Generation.Gen5);
        context[CpuRegister.Rdi] = 0xDEAD_0000;

        var result = KernelMemoryCompatExports.Time(context);

        Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT, result);
        Assert.Equal(ulong.MaxValue, context[CpuRegister.Rax]);
    }

    [Fact]
    public void TimeExportMetadataIsExact()
    {
        ExportMetadataAssert.Exact(
            "wLlFkwG9UcQ",
            "time",
            "libc",
            Generation.Gen4 | Generation.Gen5);
    }
}
