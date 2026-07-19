// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using System.Text;
using SharpEmu.HLE;
using SharpEmu.Libs.SaveData;
using Xunit;

namespace SharpEmu.Libs.Tests;

public sealed class SaveDataMutationTests
{
    private const int OrbisSaveDataErrorBusy = unchecked((int)0x809F0003);
    private const int OrbisSaveDataErrorNotMounted = unchecked((int)0x809F0004);
    private const int OrbisSaveDataErrorParameter = unchecked((int)0x809F0000);
    private const int SaveDataParamSize = 0x530;
    private const ulong ParamAddress = 0x2000;
    private const ulong DeleteAddress = 0x3000;
    private const ulong DirNameAddress = 0x4000;
    private const ulong SearchCondAddress = 0x5000;
    private const ulong SearchResultAddress = 0x6000;
    private const ulong ResultDirNameAddress = 0x7000;
    private const ulong ResultParamAddress = 0x8000;
    private const ulong MountParamAddress = 0x9000;
    private const ulong MountResultAddress = 0xA000;
    private const ulong UntrackedMountPointAddress = 0xB000;
    private const ulong IconAddress = 0xC000;
    private const ulong IconBufferAddress = 0xD000;

    [Fact]
    public void SetParamPersistsMetadataReturnedByDirectorySearch()
    {
        WithIsolatedSaveRoot((isolatedRoot, savePath) =>
        {
            var param = new byte[SaveDataParamSize];
            WriteAscii(param.AsSpan(0x00, 0x80), "Dreaming Sarah");
            WriteAscii(param.AsSpan(0x80, 0x80), "New Game");
            WriteAscii(param.AsSpan(0x100, 0x400), "Reached the first playable room");
            BinaryPrimitives.WriteUInt32LittleEndian(param.AsSpan(0x500), 42);
            BinaryPrimitives.WriteInt64LittleEndian(param.AsSpan(0x508), 1_725_000_000);

            var searchCond = new byte[0x20];
            BinaryPrimitives.WriteInt32LittleEndian(searchCond, 0);
            var searchResult = new byte[0x28];
            BinaryPrimitives.WriteUInt64LittleEndian(
                searchResult.AsSpan(0x08),
                ResultDirNameAddress);
            BinaryPrimitives.WriteUInt32LittleEndian(searchResult.AsSpan(0x10), 1);
            BinaryPrimitives.WriteUInt64LittleEndian(
                searchResult.AsSpan(0x18),
                ResultParamAddress);

            var resultDirName = new byte[32];
            var resultParam = new byte[SaveDataParamSize];
            var memory = new FakeGuestMemory();
            memory.AddRegion(ParamAddress, param);
            memory.AddRegion(DirNameAddress, FixedAscii("slot00", 32));
            memory.AddRegion(SearchCondAddress, searchCond);
            memory.AddRegion(SearchResultAddress, searchResult);
            memory.AddRegion(ResultDirNameAddress, resultDirName);
            memory.AddRegion(ResultParamAddress, resultParam);
            var context = new CpuContext(memory, Generation.Gen5);

            MountSave(context, memory);
            context[CpuRegister.Rdi] = MountResultAddress;
            context[CpuRegister.Rsi] = 0;
            context[CpuRegister.Rdx] = ParamAddress;
            context[CpuRegister.Rcx] = SaveDataParamSize;
            Assert.Equal(0, SaveDataExports.SaveDataSetParam(context));

            context[CpuRegister.Rdi] = SearchCondAddress;
            context[CpuRegister.Rsi] = SearchResultAddress;
            Assert.Equal(0, SaveDataExports.SaveDataDirNameSearch(context));

            Assert.Equal("slot00", ReadAscii(resultDirName));
            Assert.Equal(param, resultParam);
            Assert.True(File.Exists(
                Path.Combine(savePath, "sce_sys", "param.bin")));
            Assert.StartsWith(
                Path.GetFullPath(isolatedRoot),
                Path.GetFullPath(savePath),
                StringComparison.OrdinalIgnoreCase);
        });
    }

    [Fact]
    public void SetParamUpdatesOneFieldWithoutDiscardingStoredMetadata()
    {
        WithIsolatedSaveRoot((_, savePath) =>
        {
            var allParam = Enumerable.Repeat((byte)0x5A, SaveDataParamSize).ToArray();
            var replacementTitle = FixedAscii("Updated title", 0x80);
            var memory = new FakeGuestMemory();
            memory.AddRegion(ParamAddress, allParam);
            memory.AddRegion(ParamAddress + 0x1000, replacementTitle);
            memory.AddRegion(DirNameAddress, FixedAscii("slot00", 32));
            var context = new CpuContext(memory, Generation.Gen5);

            MountSave(context, memory);
            context[CpuRegister.Rdi] = MountResultAddress;
            context[CpuRegister.Rsi] = 0;
            context[CpuRegister.Rdx] = ParamAddress;
            context[CpuRegister.Rcx] = SaveDataParamSize;
            Assert.Equal(0, SaveDataExports.SaveDataSetParam(context));

            context[CpuRegister.Rsi] = 1;
            context[CpuRegister.Rdx] = ParamAddress + 0x1000;
            context[CpuRegister.Rcx] = unchecked((ulong)replacementTitle.Length);
            Assert.Equal(0, SaveDataExports.SaveDataSetParam(context));

            var stored = File.ReadAllBytes(
                Path.Combine(savePath, "sce_sys", "param.bin"));
            Assert.Equal(replacementTitle, stored[..replacementTitle.Length]);
            Assert.All(stored[replacementTitle.Length..], value => Assert.Equal(0x5A, value));
        });
    }

    [Fact]
    public void SetParamRejectsUntrackedMountsAndWideInvalidParameterTypes()
    {
        WithIsolatedSaveRoot((_, _) =>
        {
            var memory = new FakeGuestMemory();
            memory.AddRegion(DirNameAddress, FixedAscii("slot00", 32));
            memory.AddRegion(ParamAddress, new byte[SaveDataParamSize]);
            memory.AddRegion(
                UntrackedMountPointAddress,
                FixedAscii("/untracked", 16));
            var context = new CpuContext(memory, Generation.Gen5);

            context[CpuRegister.Rdi] = UntrackedMountPointAddress;
            context[CpuRegister.Rsi] = 0;
            context[CpuRegister.Rdx] = ParamAddress;
            context[CpuRegister.Rcx] = SaveDataParamSize;
            Assert.Equal(
                OrbisSaveDataErrorNotMounted,
                SaveDataExports.SaveDataSetParam(context));

            MountSave(context, memory);
            context[CpuRegister.Rdi] = MountResultAddress;
            context[CpuRegister.Rsi] = 1UL << 32;
            context[CpuRegister.Rdx] = ParamAddress;
            context[CpuRegister.Rcx] = SaveDataParamSize;
            Assert.Equal(
                OrbisSaveDataErrorParameter,
                SaveDataExports.SaveDataSetParam(context));
        });
    }

    [Fact]
    public void DeleteRejectsMountedSaveThenRemovesItAfterUnmount()
    {
        WithIsolatedSaveRoot((_, savePath) =>
        {
            File.WriteAllText(Path.Combine(savePath, "progress.dat"), "owned test data");
            var delete = new byte[0x40];
            BinaryPrimitives.WriteInt32LittleEndian(delete, 0);
            BinaryPrimitives.WriteUInt64LittleEndian(delete.AsSpan(0x10), DirNameAddress);
            var memory = new FakeGuestMemory();
            memory.AddRegion(DeleteAddress, delete);
            memory.AddRegion(DirNameAddress, FixedAscii("slot00", 32));
            var context = new CpuContext(memory, Generation.Gen5);
            context[CpuRegister.Rdi] = DeleteAddress;

            MountSave(context, memory);
            context[CpuRegister.Rdi] = DeleteAddress;
            Assert.Equal(
                OrbisSaveDataErrorBusy,
                SaveDataExports.SaveDataDelete(context));
            Assert.True(Directory.Exists(savePath));

            context[CpuRegister.Rdi] = MountResultAddress;
            Assert.Equal(0, SaveDataExports.SaveDataUmount2(context));
            context[CpuRegister.Rdi] = DeleteAddress;
            Assert.Equal(0, SaveDataExports.SaveDataDelete(context));
            Assert.False(Directory.Exists(savePath));
            Assert.Equal(0, SaveDataExports.SaveDataDelete(context));
        });
    }

    [Fact]
    public void SaveIconPersistsOnlyGuestDataSizeInMountedSaveMetadata()
    {
        WithIsolatedSaveRoot((_, savePath) =>
        {
            var payload = new byte[] { 0x89, 0x50, 0x4E, 0x47, 0xAA, 0xBB };
            var memory = new FakeGuestMemory();
            memory.AddRegion(DirNameAddress, FixedAscii("slot00", 32));
            memory.AddRegion(
                IconAddress,
                CreateIcon(IconBufferAddress, bufferSize: 6, dataSize: 4));
            memory.AddRegion(IconBufferAddress, payload);
            var context = new CpuContext(memory, Generation.Gen5);

            MountSave(context, memory);
            context[CpuRegister.Rdi] = MountResultAddress;
            context[CpuRegister.Rsi] = IconAddress;

            Assert.Equal(0, SaveDataExports.SaveDataSaveIcon(context));
            Assert.Equal(
                payload[..4],
                File.ReadAllBytes(Path.Combine(savePath, "sce_sys", "icon0.png")));
        });
    }

    [Fact]
    public void SaveIconRejectsNullPointersAndUnmountedSave()
    {
        WithIsolatedSaveRoot((_, _) =>
        {
            var memory = new FakeGuestMemory();
            memory.AddRegion(DirNameAddress, FixedAscii("slot00", 32));
            memory.AddRegion(
                IconAddress,
                CreateIcon(IconBufferAddress, bufferSize: 4, dataSize: 4));
            memory.AddRegion(IconBufferAddress, new byte[4]);
            memory.AddRegion(UntrackedMountPointAddress, FixedAscii("/untracked", 16));
            var context = new CpuContext(memory, Generation.Gen5);

            context[CpuRegister.Rdi] = 0;
            context[CpuRegister.Rsi] = IconAddress;
            Assert.Equal(
                OrbisSaveDataErrorParameter,
                SaveDataExports.SaveDataSaveIcon(context));

            context[CpuRegister.Rdi] = UntrackedMountPointAddress;
            context[CpuRegister.Rsi] = 0;
            Assert.Equal(
                OrbisSaveDataErrorParameter,
                SaveDataExports.SaveDataSaveIcon(context));

            context[CpuRegister.Rsi] = IconAddress;
            Assert.Equal(
                OrbisSaveDataErrorNotMounted,
                SaveDataExports.SaveDataSaveIcon(context));
        });
    }

    [Fact]
    public void SaveIconGuestMemoryFailureDoesNotReplaceExistingIcon()
    {
        WithIsolatedSaveRoot((_, savePath) =>
        {
            var iconPath = Path.Combine(savePath, "sce_sys", "icon0.png");
            Directory.CreateDirectory(Path.GetDirectoryName(iconPath)!);
            File.WriteAllBytes(iconPath, [1, 2, 3]);
            var memory = new FakeGuestMemory();
            memory.AddRegion(DirNameAddress, FixedAscii("slot00", 32));
            memory.AddRegion(
                IconAddress,
                CreateIcon(IconBufferAddress, bufferSize: 4, dataSize: 4));
            memory.AddRegion(IconBufferAddress, new byte[2]);
            var context = new CpuContext(memory, Generation.Gen5);

            MountSave(context, memory);
            context[CpuRegister.Rdi] = MountResultAddress;
            context[CpuRegister.Rsi] = IconAddress;

            Assert.Equal(
                (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT,
                SaveDataExports.SaveDataSaveIcon(context));
            Assert.Equal([1, 2, 3], File.ReadAllBytes(iconPath));
        });
    }

    [Theory]
    [InlineData(".")]
    [InlineData("..")]
    public void DeleteRejectsDirectoryAliasesThatCouldEscapeSaveSlot(string dirName)
    {
        WithIsolatedSaveRoot((_, _) =>
        {
            var delete = new byte[0x40];
            BinaryPrimitives.WriteInt32LittleEndian(delete, 0);
            BinaryPrimitives.WriteUInt64LittleEndian(delete.AsSpan(0x10), DirNameAddress);
            var memory = new FakeGuestMemory();
            memory.AddRegion(DeleteAddress, delete);
            memory.AddRegion(DirNameAddress, FixedAscii(dirName, 32));
            var context = new CpuContext(memory, Generation.Gen5);
            context[CpuRegister.Rdi] = DeleteAddress;

            Assert.Equal(
                OrbisSaveDataErrorParameter,
                SaveDataExports.SaveDataDelete(context));
        });
    }

    [Theory]
    [InlineData(
        "85zul--eGXs",
        "sceSaveDataSetParam")]
    [InlineData(
        "S1GkePI17zQ",
        "sceSaveDataDelete")]
    [InlineData(
        "c88Yy54Mx0w",
        "sceSaveDataSaveIcon")]
    public void MutationExportMetadataIsExact(
        string nid,
        string exportName)
    {
        ExportMetadataAssert.Exact(
            nid,
            exportName,
            "libSceSaveData",
            Generation.Gen4 | Generation.Gen5);
    }

    private static void MountSave(CpuContext context, FakeGuestMemory memory)
    {
        var mount = new byte[0x2C];
        BinaryPrimitives.WriteInt32LittleEndian(mount, 0);
        BinaryPrimitives.WriteUInt64LittleEndian(mount.AsSpan(0x08), DirNameAddress);
        BinaryPrimitives.WriteUInt32LittleEndian(mount.AsSpan(0x20), 1u << 5);
        memory.AddRegion(MountParamAddress, mount);
        memory.AddRegion(MountResultAddress, new byte[0x40]);
        context[CpuRegister.Rdi] = MountParamAddress;
        context[CpuRegister.Rsi] = MountResultAddress;
        Assert.Equal(0, SaveDataExports.SaveDataMount3(context));
    }

    private static void WithIsolatedSaveRoot(Action<string, string> action)
    {
        var previousRoot = Environment.GetEnvironmentVariable("SHARPEMU_SAVEDATA_DIR");
        var isolatedRoot = Path.Combine(
            Path.GetTempPath(),
            "SharpEmu.Tests",
            Guid.NewGuid().ToString("N"));
        var savePath = Path.Combine(isolatedRoot, "0", "TEST00001", "slot00");

        try
        {
            Environment.SetEnvironmentVariable("SHARPEMU_SAVEDATA_DIR", isolatedRoot);
            SaveDataExports.ConfigureApplicationInfo("TEST00001");
            Directory.CreateDirectory(savePath);
            action(isolatedRoot, savePath);
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

    private static byte[] FixedAscii(string value, int size)
    {
        var buffer = new byte[size];
        WriteAscii(buffer, value);
        return buffer;
    }

    private static byte[] CreateIcon(
        ulong bufferAddress,
        ulong bufferSize,
        ulong dataSize)
    {
        var icon = new byte[0x38];
        BinaryPrimitives.WriteUInt64LittleEndian(icon, bufferAddress);
        BinaryPrimitives.WriteUInt64LittleEndian(icon.AsSpan(0x08), bufferSize);
        BinaryPrimitives.WriteUInt64LittleEndian(icon.AsSpan(0x10), dataSize);
        return icon;
    }

    private static void WriteAscii(Span<byte> destination, string value)
    {
        var count = Math.Min(value.Length, Math.Max(0, destination.Length - 1));
        Encoding.ASCII.GetBytes(value.AsSpan(0, count), destination);
    }

    private static string ReadAscii(ReadOnlySpan<byte> value)
    {
        var terminator = value.IndexOf((byte)0);
        return Encoding.ASCII.GetString(terminator < 0 ? value : value[..terminator]);
    }
}
