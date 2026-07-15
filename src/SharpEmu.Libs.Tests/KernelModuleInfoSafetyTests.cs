// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using System.Text;
using SharpEmu.HLE;
using SharpEmu.Libs.Kernel;
using Xunit;

namespace SharpEmu.Libs.Tests;

public sealed class KernelModuleInfoSafetyTests : IDisposable
{
    private const ulong OutputAddress = 0x1000;
    private const ulong ModuleInfoNameOffset = 0x10;
    private const ulong ModuleInfoHandleOffset = 0x108;
    private const string ModuleName = "DreamingSarah.prx";
    private const byte Canary = 0xA5;

    public KernelModuleInfoSafetyTests()
    {
        KernelModuleRegistry.Reset();
    }

    [Fact]
    public void GetModuleInfoWritesNameAndHandleAtExpectedOffsets()
    {
        var handle = KernelModuleRegistry.RegisterSyntheticModule(ModuleName, isSystemModule: false);
        var output = Enumerable.Repeat(Canary, 0x120).ToArray();
        var memory = new FakeGuestMemory();
        memory.AddRegion(OutputAddress, output);
        var context = CreateContext(memory, handle, OutputAddress);

        Assert.Equal(
            (int)OrbisGen2Result.ORBIS_GEN2_OK,
            KernelRuntimeCompatExports.KernelGetModuleInfo(context));
        Assert.Equal(
            handle,
            BinaryPrimitives.ReadInt32LittleEndian(output.AsSpan((int)ModuleInfoHandleOffset)));
        Assert.Equal(
            ModuleName,
            Encoding.UTF8.GetString(
                output.AsSpan((int)ModuleInfoNameOffset, Encoding.UTF8.GetByteCount(ModuleName))));
    }

    [Fact]
    public void GetModuleInfoRejectsWrappedHandleFieldWithoutWriting()
    {
        var handle = KernelModuleRegistry.RegisterSyntheticModule(ModuleName, isSystemModule: false);
        var wrappedDestination = Enumerable.Repeat(Canary, sizeof(int)).ToArray();
        var memory = new FakeGuestMemory();
        memory.AddRegion(0, wrappedDestination);
        var context = CreateContext(
            memory,
            handle,
            ulong.MaxValue - ModuleInfoHandleOffset + 1);

        Assert.Equal(
            (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT,
            KernelRuntimeCompatExports.KernelGetModuleInfo(context));
        Assert.All(wrappedDestination, value => Assert.Equal(Canary, value));
    }

    public void Dispose()
    {
        KernelModuleRegistry.Reset();
        GC.SuppressFinalize(this);
    }

    private static CpuContext CreateContext(FakeGuestMemory memory, int handle, ulong outputAddress)
    {
        var context = new CpuContext(memory, Generation.Gen5);
        context[CpuRegister.Rdi] = unchecked((ulong)handle);
        context[CpuRegister.Rsi] = outputAddress;
        return context;
    }
}
