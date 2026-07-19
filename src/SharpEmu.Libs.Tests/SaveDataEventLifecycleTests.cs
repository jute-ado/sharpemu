// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using System.Text;
using SharpEmu.HLE;
using SharpEmu.Libs.SaveData;
using Xunit;

namespace SharpEmu.Libs.Tests;

public sealed class SaveDataEventLifecycleTests
{
    private const int OrbisSaveDataErrorBusy = unchecked((int)0x809F0003);
    private const int OrbisSaveDataErrorNotFound = unchecked((int)0x809F0008);
    private const ulong SetupAddress = 0x1000;
    private const ulong SyncAddress = 0x2000;
    private const ulong EventAddress = 0x3000;
    private const ulong UnmappedEventAddress = 0x4000;
    private const ulong DirNameAddress = 0x5000;
    private const ulong MountParamAddress = 0x6000;
    private const ulong MountResultAddress = 0x7000;

    [Fact]
    public void SyncEventUsesExactAbiLayoutAndZeroesReservedBytes()
    {
        WithIsolatedSaveRoot(() =>
        {
            var memory = new FakeGuestMemory();
            memory.AddRegion(EventAddress, Enumerable.Repeat((byte)0xCC, 0x68).ToArray());
            var context = new CpuContext(memory, Generation.Gen5);
            QueueMemorySyncEvent(context, memory, userId: 7);

            context[CpuRegister.Rsi] = EventAddress;
            Assert.Equal(0, SaveDataExports.SaveDataGetEventResult(context));

            var actual = new byte[0x68];
            Assert.True(memory.TryRead(EventAddress, actual));
            var expected = new byte[0x68];
            BinaryPrimitives.WriteUInt32LittleEndian(expected, 3);
            BinaryPrimitives.WriteInt32LittleEndian(expected.AsSpan(0x08), 7);
            Encoding.ASCII.GetBytes("TEST00001", expected.AsSpan(0x10, 10));
            Assert.Equal(expected, actual);
        });
    }

    [Fact]
    public void GetEventResultMemoryFaultLeavesEventQueuedForRetry()
    {
        WithIsolatedSaveRoot(() =>
        {
            var memory = new FakeGuestMemory();
            var context = new CpuContext(memory, Generation.Gen5);
            QueueMemorySyncEvent(context, memory, userId: 3);

            context[CpuRegister.Rsi] = UnmappedEventAddress;
            Assert.Equal(
                (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT,
                SaveDataExports.SaveDataGetEventResult(context));

            memory.AddRegion(EventAddress, new byte[0x68]);
            context[CpuRegister.Rsi] = EventAddress;
            Assert.Equal(0, SaveDataExports.SaveDataGetEventResult(context));
            Assert.True(context.TryReadUInt32(EventAddress, out var eventType));
            Assert.Equal(3U, eventType);
        });
    }

    [Fact]
    public void TerminateRefusesMountedStateThenClearsPendingEvents()
    {
        WithIsolatedSaveRoot(() =>
        {
            var memory = new FakeGuestMemory();
            memory.AddRegion(EventAddress, new byte[0x68]);
            memory.AddRegion(DirNameAddress, FixedAscii("slot00", 32));
            var context = new CpuContext(memory, Generation.Gen5);
            QueueMemorySyncEvent(context, memory, userId: 1);
            context[CpuRegister.Rdi] = 0;
            Assert.Equal(1, SaveDataExports.SaveDataCreateTransactionResource(context));
            MountSave(context, memory);

            Assert.Equal(OrbisSaveDataErrorBusy, SaveDataExports.SaveDataTerminate(context));
            context[CpuRegister.Rdi] = 0;
            Assert.Equal(2, SaveDataExports.SaveDataCreateTransactionResource(context));
            context[CpuRegister.Rsi] = EventAddress;
            Assert.Equal(0, SaveDataExports.SaveDataGetEventResult(context));

            QueueMemorySyncEvent(context, memory, userId: 1);
            context[CpuRegister.Rdi] = MountResultAddress;
            Assert.Equal(0, SaveDataExports.SaveDataUmount2(context));
            Assert.Equal(0, SaveDataExports.SaveDataTerminate(context));
            context[CpuRegister.Rdi] = 0;
            Assert.Equal(1, SaveDataExports.SaveDataCreateTransactionResource(context));

            context[CpuRegister.Rsi] = EventAddress;
            Assert.Equal(
                OrbisSaveDataErrorNotFound,
                SaveDataExports.SaveDataGetEventResult(context));
        });
    }

    [Fact]
    public void TerminateExportMetadataIsExact()
    {
        ExportMetadataAssert.Exact(
            "yKDy8S5yLA0",
            "sceSaveDataTerminate",
            "libSceSaveData",
            Generation.Gen4 | Generation.Gen5);
    }

    private static void QueueMemorySyncEvent(
        CpuContext context,
        FakeGuestMemory memory,
        int userId)
    {
        var setup = new byte[0x10];
        BinaryPrimitives.WriteInt32LittleEndian(setup.AsSpan(0x04), userId);
        BinaryPrimitives.WriteUInt64LittleEndian(setup.AsSpan(0x08), 64);
        var sync = new byte[0x04];
        BinaryPrimitives.WriteInt32LittleEndian(sync, userId);
        memory.AddRegion(SetupAddress, setup);
        memory.AddRegion(SyncAddress, sync);

        context[CpuRegister.Rdi] = SetupAddress;
        context[CpuRegister.Rsi] = 0;
        Assert.Equal(0, SaveDataExports.SaveDataSetupSaveDataMemory2(context));
        context[CpuRegister.Rdi] = SyncAddress;
        Assert.Equal(0, SaveDataExports.SaveDataSyncSaveDataMemory(context));
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

    private static byte[] FixedAscii(string value, int size)
    {
        var buffer = new byte[size];
        Encoding.ASCII.GetBytes(value, buffer);
        return buffer;
    }
}
