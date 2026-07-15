// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using SharpEmu.Libs.SaveData;
using Xunit;

namespace SharpEmu.Libs.Tests;

public sealed class SaveDataSearchSafetyTests
{
    private const ulong CondAddress = 0x1000;
    private const ulong ResultAddress = 0x2000;
    private const int SearchCondSize = 0x20;
    private const int SearchResultSize = 0x28;

    [Fact]
    public void DirNameSearchAcceptsCompleteStructures()
    {
        var memory = new FakeGuestMemory();
        memory.AddRegion(CondAddress, new byte[SearchCondSize]);
        memory.AddRegion(ResultAddress, new byte[SearchResultSize]);
        var context = CreateContext(memory, CondAddress, ResultAddress);

        WithIsolatedSaveRoot(() =>
        {
            Assert.Equal(0, SaveDataExports.SaveDataDirNameSearch(context));
            Assert.Equal(0ul, context[CpuRegister.Rax]);
        });
    }

    [Fact]
    public void DirNameSearchRejectsWrappedConditionStructure()
    {
        var memory = new FakeGuestMemory();
        memory.AddRegion(ulong.MaxValue - 3, new byte[sizeof(int)]);
        memory.AddRegion(0, new byte[SearchCondSize - sizeof(int)]);
        memory.AddRegion(ResultAddress, new byte[SearchResultSize]);
        var context = CreateContext(memory, ulong.MaxValue - 3, ResultAddress);

        WithIsolatedSaveRoot(() =>
        {
            Assert.Equal(
                (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT,
                SaveDataExports.SaveDataDirNameSearch(context));
            Assert.Equal(
                unchecked((ulong)(int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT),
                context[CpuRegister.Rax]);
        });
    }

    [Fact]
    public void DirNameSearchRejectsWrappedResultStructure()
    {
        var memory = new FakeGuestMemory();
        memory.AddRegion(CondAddress, new byte[SearchCondSize]);
        memory.AddRegion(ulong.MaxValue - 7, new byte[sizeof(ulong)]);
        memory.AddRegion(0, new byte[SearchResultSize - sizeof(ulong)]);
        var context = CreateContext(memory, CondAddress, ulong.MaxValue - 7);

        WithIsolatedSaveRoot(() =>
        {
            Assert.Equal(
                (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT,
                SaveDataExports.SaveDataDirNameSearch(context));
            Assert.Equal(
                unchecked((ulong)(int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT),
                context[CpuRegister.Rax]);
        });
    }

    private static CpuContext CreateContext(
        FakeGuestMemory memory,
        ulong condAddress,
        ulong resultAddress)
    {
        var context = new CpuContext(memory, Generation.Gen5);
        context[CpuRegister.Rdi] = condAddress;
        context[CpuRegister.Rsi] = resultAddress;
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
