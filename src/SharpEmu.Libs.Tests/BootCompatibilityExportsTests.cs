// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using System.Text;
using SharpEmu.HLE;
using SharpEmu.Libs.Kernel;
using SharpEmu.Libs.SaveData;
using Xunit;

namespace SharpEmu.Libs.Tests;

public sealed class BootCompatibilityExportsTests
{
    private const ulong PathAddress = 0x1000;
    private const ulong MissingPathAddress = 0x1800;
    private const ulong StatAddress = 0x2000;
    private const ulong TlsAddress = 0x3000;

    [Fact]
    public void GetProcessIdReturnsHostProcessIdInBothReturnChannels()
    {
        var context = new CpuContext(new FakeGuestMemory(), Generation.Gen5);

        var result = KernelRuntimeCompatExports.GetProcessId(context);

        Assert.Equal(Environment.ProcessId, result);
        Assert.Equal(unchecked((uint)Environment.ProcessId), context[CpuRegister.Rax]);
    }

    [Fact]
    public void PosixStatReturnsZeroForFilesAndSetsEnoentForMissingPaths()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "SharpEmu.Tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var hostPath = Path.Combine(root, "boot.dat");
        File.WriteAllBytes(hostPath, [0x11, 0x22, 0x33]);
        var mount = "/boot-contract-" + Guid.NewGuid().ToString("N");
        KernelMemoryCompatExports.RegisterGuestPathMount(mount, root);

        var memory = new FakeGuestMemory();
        var stat = new byte[120];
        var tls = new byte[0x100];
        memory.AddRegion(PathAddress, Encoding.UTF8.GetBytes(mount + "/boot.dat\0"));
        memory.AddRegion(MissingPathAddress, Encoding.UTF8.GetBytes(mount + "/missing.dat\0"));
        memory.AddRegion(StatAddress, stat);
        memory.AddRegion(TlsAddress, tls);
        var context = new CpuContext(memory, Generation.Gen5)
        {
            FsBase = TlsAddress,
        };

        try
        {
            context[CpuRegister.Rdi] = PathAddress;
            context[CpuRegister.Rsi] = StatAddress;
            Assert.Equal(0, KernelMemoryCompatExports.PosixStat(context));
            Assert.Equal(0UL, context[CpuRegister.Rax]);
            Assert.Contains(stat, value => value != 0);

            context[CpuRegister.Rdi] = MissingPathAddress;
            Assert.Equal(-1, KernelMemoryCompatExports.PosixStat(context));
            Assert.Equal(ulong.MaxValue, context[CpuRegister.Rax]);
            Assert.Equal(2, BinaryPrimitives.ReadInt32LittleEndian(tls.AsSpan(0x40)));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void SaveDataTransactionResourcesAreUniqueAndResetPerApplication()
    {
        var context = new CpuContext(new FakeGuestMemory(), Generation.Gen5);
        try
        {
            SaveDataExports.ConfigureApplicationInfo("TEST00001");
            context[CpuRegister.Rdi] = 0x1000;
            Assert.Equal(1, SaveDataExports.SaveDataCreateTransactionResource(context));
            Assert.Equal(1UL, context[CpuRegister.Rax]);
            Assert.Equal(2, SaveDataExports.SaveDataCreateTransactionResource(context));
            Assert.Equal(2UL, context[CpuRegister.Rax]);

            context[CpuRegister.Rdi] = 1;
            Assert.Equal(0, SaveDataExports.SaveDataDeleteTransactionResource(context));
            Assert.Equal(0UL, context[CpuRegister.Rax]);

            SaveDataExports.ConfigureApplicationInfo("TEST00002");
            Assert.Equal(1, SaveDataExports.SaveDataCreateTransactionResource(context));
            Assert.Equal(1UL, context[CpuRegister.Rax]);
        }
        finally
        {
            SaveDataExports.ConfigureApplicationInfo(null);
        }
    }
}
