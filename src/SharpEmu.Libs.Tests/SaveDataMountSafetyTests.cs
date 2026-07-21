// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using System.Text;
using SharpEmu.HLE;
using SharpEmu.Libs.SaveData;
using Xunit;

namespace SharpEmu.Libs.Tests;

public sealed class SaveDataMountSafetyTests
{
    private const int OrbisSaveDataErrorNotFound = unchecked((int)0x809F0008);
    private const ulong MountAddress = 0x1000;
    private const ulong DirNameAddress = 0x2000;
    private const ulong ResultAddress = 0x3000;
    private const ulong TitleIdAddress = 0x4000;
    private const int MountSize = 0x2C;
    private const int ResultSize = 0x40;

    [Fact]
    public void Mount3AcceptsCompleteStructure()
    {
        var mount = new byte[MountSize];
        BinaryPrimitives.WriteUInt64LittleEndian(mount.AsSpan(0x08), DirNameAddress);
        var memory = CreateMemory(mount, MountAddress);
        var context = CreateContext(memory, MountAddress);

        WithIsolatedSaveRoot(() =>
        {
            Assert.Equal(OrbisSaveDataErrorNotFound, SaveDataExports.SaveDataMount3(context));
            Assert.Equal(unchecked((ulong)OrbisSaveDataErrorNotFound), context[CpuRegister.Rax]);
        });
    }

    [Fact]
    public void Mount3RejectsWrappedStructure()
    {
        var wrappedFields = new byte[MountSize - sizeof(int)];
        BinaryPrimitives.WriteUInt64LittleEndian(wrappedFields.AsSpan(0x04), DirNameAddress);
        var memory = CreateMemory(wrappedFields, 0);
        memory.AddRegion(ulong.MaxValue - 3, new byte[sizeof(int)]);
        var context = CreateContext(memory, ulong.MaxValue - 3);

        WithIsolatedSaveRoot(() =>
        {
            Assert.Equal(
                (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT,
                SaveDataExports.SaveDataMount3(context));
            Assert.Equal(
                unchecked((ulong)(int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT),
                context[CpuRegister.Rax]);
        });
    }

    [Fact]
    public void TransferringMountUsesGuestTitleAndDirectorySafely()
    {
        var mount = new byte[0x18];
        BinaryPrimitives.WriteInt32LittleEndian(mount, 1000);
        BinaryPrimitives.WriteUInt64LittleEndian(mount.AsSpan(0x08), TitleIdAddress);
        BinaryPrimitives.WriteUInt64LittleEndian(mount.AsSpan(0x10), DirNameAddress);
        var memory = CreateMemory(mount, MountAddress);
        var titleId = new byte[16];
        Encoding.ASCII.GetBytes("TEST00002\0").CopyTo(titleId, 0);
        memory.AddRegion(TitleIdAddress, titleId);
        var context = CreateContext(memory, MountAddress);

        WithIsolatedSaveRoot(() =>
        {
            Assert.Equal(
                OrbisSaveDataErrorNotFound,
                SaveDataExports.SaveDataTransferringMount(context));
            Assert.Equal(unchecked((ulong)OrbisSaveDataErrorNotFound), context[CpuRegister.Rax]);
        });
    }

    private static FakeGuestMemory CreateMemory(byte[] mount, ulong mountAddress)
    {
        var dirName = new byte[32];
        Encoding.ASCII.GetBytes("save\0").CopyTo(dirName, 0);
        var memory = new FakeGuestMemory();
        memory.AddRegion(mountAddress, mount);
        memory.AddRegion(DirNameAddress, dirName);
        memory.AddRegion(ResultAddress, new byte[ResultSize]);
        return memory;
    }

    private static CpuContext CreateContext(FakeGuestMemory memory, ulong mountAddress)
    {
        var context = new CpuContext(memory, Generation.Gen5);
        context[CpuRegister.Rdi] = mountAddress;
        context[CpuRegister.Rsi] = ResultAddress;
        return context;
    }

    private static void WithIsolatedSaveRoot(Action action)
    {
        var previousRoot = Environment.GetEnvironmentVariable("SHARPEMU_SAVEDATA_DIR");
        var isolatedRoot = Path.Combine(
            Path.GetTempPath(),
            "SharpEmu.Tests",
            Guid.NewGuid().ToString("N"));

        try
        {
            Environment.SetEnvironmentVariable("SHARPEMU_SAVEDATA_DIR", isolatedRoot);
            SaveDataExports.ConfigureApplicationInfo("TEST00001");
            action();
        }
        finally
        {
            SaveDataExports.ConfigureApplicationInfo(null);
            Environment.SetEnvironmentVariable("SHARPEMU_SAVEDATA_DIR", previousRoot);
            if (Directory.Exists(isolatedRoot))
            {
                Directory.Delete(isolatedRoot, recursive: true);
            }
        }
    }
}
