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
    private const ulong UnwindInfoNameOffset = 0x08;
    private const ulong UnwindInfoEhFrameHeaderOffset = 0x108;
    private const ulong UnwindInfoEhFrameOffset = 0x110;
    private const ulong UnwindInfoEhFrameSizeOffset = 0x118;
    private const ulong UnwindInfoSegmentAddressOffset = 0x120;
    private const ulong UnwindInfoSegmentSizeOffset = 0x128;
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

    [Fact]
    public void GetModuleInfoForUnwindWritesDedicatedUnwindLayout()
    {
        const ulong moduleBase = 0x8000_1000;
        const ulong moduleSize = 0x9000;
        const ulong ehFrameHeader = moduleBase + 0x7800;
        const ulong ehFrame = moduleBase + 0x6000;
        const ulong ehFrameSize = 0x1800;
        _ = KernelModuleRegistry.RegisterModule(
            ModuleName,
            moduleBase,
            moduleSize,
            entryPoint: moduleBase + 0x100,
            initEntryPoint: 0,
            ehFrameHeader,
            ehFrame,
            ehFrameSize,
            isMain: false);
        var output = Enumerable.Repeat(Canary, 0x130).ToArray();
        BinaryPrimitives.WriteUInt64LittleEndian(output, 0x130);
        var memory = new FakeGuestMemory();
        memory.AddRegion(OutputAddress, output);
        var context = new CpuContext(memory, Generation.Gen5);
        context[CpuRegister.Rdi] = moduleBase + 0x400;
        context[CpuRegister.Rsi] = 0;
        context[CpuRegister.Rdx] = OutputAddress;

        Assert.Equal(
            (int)OrbisGen2Result.ORBIS_GEN2_OK,
            KernelRuntimeCompatExports.KernelGetModuleInfoForUnwind(context));
        Assert.Equal(
            ModuleName,
            Encoding.UTF8.GetString(
                output.AsSpan((int)UnwindInfoNameOffset, Encoding.UTF8.GetByteCount(ModuleName))));
        Assert.Equal(ehFrameHeader, ReadUInt64(output, UnwindInfoEhFrameHeaderOffset));
        Assert.Equal(ehFrame, ReadUInt64(output, UnwindInfoEhFrameOffset));
        Assert.Equal(ehFrameSize, ReadUInt64(output, UnwindInfoEhFrameSizeOffset));
        Assert.Equal(moduleBase, ReadUInt64(output, UnwindInfoSegmentAddressOffset));
        Assert.Equal(moduleSize, ReadUInt64(output, UnwindInfoSegmentSizeOffset));
    }

    [Fact]
    public void GetModuleInfoForUnwindRejectsShortOutputWithoutWriting()
    {
        const ulong moduleBase = 0x8000_1000;
        _ = KernelModuleRegistry.RegisterModule(
            ModuleName,
            moduleBase,
            size: 0x9000,
            entryPoint: moduleBase + 0x100,
            initEntryPoint: 0,
            ehFrameHeaderAddress: 0,
            ehFrameAddress: 0,
            ehFrameSize: 0,
            isMain: false);
        var output = Enumerable.Repeat(Canary, 0x130).ToArray();
        BinaryPrimitives.WriteUInt64LittleEndian(output, 0x12F);
        var expected = output.ToArray();
        var memory = new FakeGuestMemory();
        memory.AddRegion(OutputAddress, output);
        var context = CreateUnwindContext(memory, moduleBase, OutputAddress);

        Assert.Equal(
            (int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT,
            KernelRuntimeCompatExports.KernelGetModuleInfoForUnwind(context));
        Assert.Equal(expected, output);
    }

    [Fact]
    public void GetModuleInfoForUnwindRejectsUnknownAddressWithoutWriting()
    {
        var output = Enumerable.Repeat(Canary, 0x130).ToArray();
        BinaryPrimitives.WriteUInt64LittleEndian(output, 0x130);
        var expected = output.ToArray();
        var memory = new FakeGuestMemory();
        memory.AddRegion(OutputAddress, output);
        var context = CreateUnwindContext(memory, queriedAddress: 0xDEAD_BEEF, OutputAddress);

        Assert.Equal(
            (int)OrbisGen2Result.ORBIS_GEN2_ERROR_NOT_FOUND,
            KernelRuntimeCompatExports.KernelGetModuleInfoForUnwind(context));
        Assert.Equal(expected, output);
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

    private static CpuContext CreateUnwindContext(
        FakeGuestMemory memory,
        ulong queriedAddress,
        ulong outputAddress)
    {
        var context = new CpuContext(memory, Generation.Gen5);
        context[CpuRegister.Rdi] = queriedAddress;
        context[CpuRegister.Rsi] = 0;
        context[CpuRegister.Rdx] = outputAddress;
        return context;
    }

    private static ulong ReadUInt64(byte[] output, ulong offset) =>
        BinaryPrimitives.ReadUInt64LittleEndian(output.AsSpan(checked((int)offset)));
}
