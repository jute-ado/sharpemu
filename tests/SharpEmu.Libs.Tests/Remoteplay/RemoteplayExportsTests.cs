// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using SharpEmu.Libs.Remoteplay;
using Xunit;

namespace SharpEmu.Libs.Tests.Remoteplay;

public sealed class RemoteplayExportsTests
{
    private const ulong MemoryBase = 0x1_0000_0000;
    private const ulong StatusAddress = MemoryBase + 1;

    [Theory]
    [InlineData(
        "k1SwgkMSOM8",
        "sceRemoteplayInitialize")]
    [InlineData(
        "BOwybKVa3Do",
        "sceRemoteplayTerminate")]
    [InlineData(
        "g3PNjYKWqnQ",
        "sceRemoteplayGetConnectionStatus")]
    public void ExportMetadataIsExact(string nid, string exportName)
    {
        ExportMetadataAssert.Exact(
            nid,
            exportName,
            "libSceRemoteplay",
            Generation.Gen5);
    }

    [Fact]
    public void InitializeAndTerminateSucceedWithoutGuestMemory()
    {
        var context = new CpuContext(
            new FakeCpuMemory(MemoryBase, 0),
            Generation.Gen5)
        {
            [CpuRegister.Rdi] = 0xDEAD_BEEF,
            [CpuRegister.Rsi] = 0x1000,
        };

        Assert.Equal(0, RemoteplayExports.RemoteplayInitialize(context));
        Assert.Equal(0UL, context[CpuRegister.Rax]);
        Assert.Equal(0, RemoteplayExports.RemoteplayTerminate(context));
        Assert.Equal(0UL, context[CpuRegister.Rax]);
    }

    [Fact]
    public void ConnectionStatusWritesOnlyDisconnectedInt()
    {
        var memory = new FakeCpuMemory(MemoryBase, 6);
        Assert.True(
            memory.TryWrite(
                MemoryBase,
                [0xA5, 0xFF, 0xFF, 0xFF, 0xFF, 0x5A]));
        var context = new CpuContext(memory, Generation.Gen5)
        {
            [CpuRegister.Rdi] = 1,
            [CpuRegister.Rsi] = StatusAddress,
        };

        Assert.Equal(
            0,
            RemoteplayExports.RemoteplayGetConnectionStatus(context));
        Assert.Equal(0UL, context[CpuRegister.Rax]);

        Span<byte> result = stackalloc byte[6];
        Assert.True(memory.TryRead(MemoryBase, result));
        Assert.Equal(
            new byte[] { 0xA5, 0, 0, 0, 0, 0x5A },
            result.ToArray());
    }

    [Fact]
    public void ConnectionStatusRejectsNullOutput()
    {
        var context = new CpuContext(
            new FakeCpuMemory(MemoryBase, 0),
            Generation.Gen5)
        {
            [CpuRegister.Rdi] = 1,
            [CpuRegister.Rsi] = 0,
        };

        Assert.Equal(
            -1,
            RemoteplayExports.RemoteplayGetConnectionStatus(context));
        Assert.Equal(ulong.MaxValue, context[CpuRegister.Rax]);
    }

    [Fact]
    public void ConnectionStatusReportsUnreadableOutput()
    {
        var context = new CpuContext(
            new FakeCpuMemory(MemoryBase, 3),
            Generation.Gen5)
        {
            [CpuRegister.Rsi] = MemoryBase,
        };

        var expected =
            (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT;
        Assert.Equal(
            expected,
            RemoteplayExports.RemoteplayGetConnectionStatus(context));
        Assert.Equal(unchecked((ulong)expected), context[CpuRegister.Rax]);
    }
}
