// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using SharpEmu.HLE;
using SharpEmu.Libs.Kernel;
using Xunit;

namespace SharpEmu.Libs.Tests;

public sealed class KernelModuleListSafetyTests : IDisposable
{
    private const ulong HandlesAddress = 0x1000;
    private const ulong CountAddress = 0x2000;
    private const byte Canary = 0xA5;

    public KernelModuleListSafetyTests()
    {
        KernelModuleRegistry.Reset();
    }

    [Fact]
    public void GetModuleListWritesRegisteredHandlesAndCount()
    {
        var firstHandle = RegisterModule("DreamingSarah.prx");
        var secondHandle = RegisterModule("libDreamingSarah.prx");
        var handles = new byte[2 * sizeof(int)];
        var count = new byte[sizeof(ulong)];
        var memory = new FakeGuestMemory();
        memory.AddRegion(HandlesAddress, handles);
        memory.AddRegion(CountAddress, count);
        var context = CreateContext(memory, HandlesAddress);

        Assert.Equal(
            (int)OrbisGen2Result.ORBIS_GEN2_OK,
            KernelRuntimeCompatExports.KernelGetModuleList2(context));
        Assert.Equal(2uL, BinaryPrimitives.ReadUInt64LittleEndian(count));
        Assert.Equal(firstHandle, BinaryPrimitives.ReadInt32LittleEndian(handles));
        Assert.Equal(secondHandle, BinaryPrimitives.ReadInt32LittleEndian(handles.AsSpan(sizeof(int))));
    }

    [Fact]
    public void GetModuleListRejectsWrappedHandleArrayBeforeWriting()
    {
        _ = RegisterModule("DreamingSarah.prx");
        _ = RegisterModule("libDreamingSarah.prx");
        var finalAddressHandle = Enumerable.Repeat(Canary, sizeof(int)).ToArray();
        var wrappedHandle = Enumerable.Repeat(Canary, sizeof(int)).ToArray();
        var count = new byte[sizeof(ulong)];
        var memory = new FakeGuestMemory();
        memory.AddRegion(ulong.MaxValue - 3, finalAddressHandle);
        memory.AddRegion(0, wrappedHandle);
        memory.AddRegion(CountAddress, count);
        var context = CreateContext(memory, ulong.MaxValue - 3);

        Assert.Equal(
            (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT,
            KernelRuntimeCompatExports.KernelGetModuleList2(context));
        Assert.Equal(2uL, BinaryPrimitives.ReadUInt64LittleEndian(count));
        Assert.All(finalAddressHandle, value => Assert.Equal(Canary, value));
        Assert.All(wrappedHandle, value => Assert.Equal(Canary, value));
    }

    public void Dispose()
    {
        KernelModuleRegistry.Reset();
        GC.SuppressFinalize(this);
    }

    private static int RegisterModule(string name)
        => KernelModuleRegistry.RegisterSyntheticModule(name, isSystemModule: false);

    private static CpuContext CreateContext(FakeGuestMemory memory, ulong handlesAddress)
    {
        var context = new CpuContext(memory, Generation.Gen5);
        context[CpuRegister.Rdi] = handlesAddress;
        context[CpuRegister.Rsi] = 2;
        context[CpuRegister.Rdx] = CountAddress;
        return context;
    }
}
