// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using System.Text;
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

        WithIsolatedSaveRoot(_ =>
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

        WithIsolatedSaveRoot(_ =>
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

        WithIsolatedSaveRoot(_ =>
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
    public void DirNameSearchWritesCompleteOutputArray()
    {
        const ulong dirNamesAddress = 0x3000;
        var result = new byte[SearchResultSize];
        BinaryPrimitives.WriteUInt64LittleEndian(result.AsSpan(0x08), dirNamesAddress);
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(0x10), 2);
        var dirNames = new byte[64];
        var memory = new FakeGuestMemory();
        memory.AddRegion(CondAddress, new byte[SearchCondSize]);
        memory.AddRegion(ResultAddress, result);
        memory.AddRegion(dirNamesAddress, dirNames);
        var context = CreateContext(memory, CondAddress, ResultAddress);

        WithIsolatedSaveRoot(isolatedRoot =>
        {
            CreateSaveDirectories(isolatedRoot);

            Assert.Equal(0, SaveDataExports.SaveDataDirNameSearch(context));
            Assert.Equal(2u, BinaryPrimitives.ReadUInt32LittleEndian(result));
            Assert.Equal(2u, BinaryPrimitives.ReadUInt32LittleEndian(result.AsSpan(0x14)));
            Assert.Equal("Alpha", ReadAscii(dirNames.AsSpan(0, 32)));
            Assert.Equal("Beta", ReadAscii(dirNames.AsSpan(32, 32)));
        });
    }

    [Fact]
    public void DirNameSearchRejectsWrappedOutputArrayBeforeMutation()
    {
        var result = new byte[SearchResultSize];
        BinaryPrimitives.WriteUInt64LittleEndian(result.AsSpan(0x08), ulong.MaxValue - 31);
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(0x10), 2);
        var highOutput = Enumerable.Repeat((byte)0xA5, 32).ToArray();
        var lowOutput = Enumerable.Repeat((byte)0x5A, 32).ToArray();
        var memory = new FakeGuestMemory();
        memory.AddRegion(CondAddress, new byte[SearchCondSize]);
        memory.AddRegion(ResultAddress, result);
        memory.AddRegion(ulong.MaxValue - 31, highOutput);
        memory.AddRegion(0, lowOutput);
        var context = CreateContext(memory, CondAddress, ResultAddress);

        WithIsolatedSaveRoot(isolatedRoot =>
        {
            CreateSaveDirectories(isolatedRoot);

            Assert.Equal(
                (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT,
                SaveDataExports.SaveDataDirNameSearch(context));
            Assert.Equal(0u, BinaryPrimitives.ReadUInt32LittleEndian(result));
            Assert.Equal(0u, BinaryPrimitives.ReadUInt32LittleEndian(result.AsSpan(0x14)));
            Assert.All(highOutput, value => Assert.Equal(0xA5, value));
            Assert.All(lowOutput, value => Assert.Equal(0x5A, value));
        });
    }

    [Theory]
    [InlineData(0x18, 0x530)]
    [InlineData(0x20, 0x30)]
    public void DirNameSearchRejectsWrappedOptionalOutputArraysBeforeMutation(
        int resultPointerOffset,
        int elementSize)
    {
        const ulong dirNamesAddress = 0x3000;
        var outputAddress = ulong.MaxValue - (ulong)elementSize + 1;
        var result = new byte[SearchResultSize];
        BinaryPrimitives.WriteUInt64LittleEndian(result.AsSpan(0x08), dirNamesAddress);
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(0x10), 2);
        BinaryPrimitives.WriteUInt64LittleEndian(result.AsSpan(resultPointerOffset), outputAddress);
        var dirNames = Enumerable.Repeat((byte)0xCC, 64).ToArray();
        var highOutput = Enumerable.Repeat((byte)0xA5, elementSize).ToArray();
        var lowOutput = Enumerable.Repeat((byte)0x5A, elementSize).ToArray();
        var memory = new FakeGuestMemory();
        memory.AddRegion(CondAddress, new byte[SearchCondSize]);
        memory.AddRegion(ResultAddress, result);
        memory.AddRegion(dirNamesAddress, dirNames);
        memory.AddRegion(outputAddress, highOutput);
        memory.AddRegion(0, lowOutput);
        var context = CreateContext(memory, CondAddress, ResultAddress);

        WithIsolatedSaveRoot(isolatedRoot =>
        {
            CreateSaveDirectories(isolatedRoot);

            Assert.Equal(
                (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT,
                SaveDataExports.SaveDataDirNameSearch(context));
            Assert.Equal(0u, BinaryPrimitives.ReadUInt32LittleEndian(result));
            Assert.Equal(0u, BinaryPrimitives.ReadUInt32LittleEndian(result.AsSpan(0x14)));
            Assert.All(dirNames, value => Assert.Equal(0xCC, value));
            Assert.All(highOutput, value => Assert.Equal(0xA5, value));
            Assert.All(lowOutput, value => Assert.Equal(0x5A, value));
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

    private static void CreateSaveDirectories(string isolatedRoot)
    {
        var titleRoot = Path.Combine(isolatedRoot, "0", "TEST00001");
        Directory.CreateDirectory(Path.Combine(titleRoot, "Beta"));
        Directory.CreateDirectory(Path.Combine(titleRoot, "Alpha"));
    }

    private static string ReadAscii(ReadOnlySpan<byte> value)
    {
        var terminator = value.IndexOf((byte)0);
        return Encoding.ASCII.GetString(terminator < 0 ? value : value[..terminator]);
    }

    private static void WithIsolatedSaveRoot(Action<string> action)
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
            action(isolatedRoot);
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
