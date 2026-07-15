// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using SharpEmu.Libs.Kernel;
using Xunit;

namespace SharpEmu.Libs.Tests;

public sealed class KernelModulePathSafetyTests : IDisposable
{
    private const ulong PathAddress = 0x1000;

    public KernelModulePathSafetyTests()
    {
        KernelModuleRegistry.Reset();
    }

    [Fact]
    public void LoadStartModuleUsesNullTerminatedGuestPath()
    {
        var memory = new FakeGuestMemory();
        memory.AddRegion(PathAddress, CreateReadChunk("/app0/DreamingSarah.prx"u8));
        var context = CreateContext(memory, PathAddress);

        Assert.Equal(
            (int)OrbisGen2Result.ORBIS_GEN2_OK,
            KernelRuntimeCompatExports.KernelLoadStartModule(context));
        AssertRegisteredName(context, "DreamingSarah.prx");
    }

    [Fact]
    public void LoadStartModuleDoesNotSpliceWrappedPathFromAddressZero()
    {
        var memory = new FakeGuestMemory();
        memory.AddRegion(ulong.MaxValue, [(byte)'X']);
        memory.AddRegion(0, CreateReadChunk("Wrapped.prx"u8));
        var context = CreateContext(memory, ulong.MaxValue);

        Assert.Equal(
            (int)OrbisGen2Result.ORBIS_GEN2_OK,
            KernelRuntimeCompatExports.KernelLoadStartModule(context));
        AssertRegisteredName(context, "module.sprx");
    }

    public void Dispose()
    {
        KernelModuleRegistry.Reset();
        GC.SuppressFinalize(this);
    }

    private static CpuContext CreateContext(FakeGuestMemory memory, ulong pathAddress)
    {
        var context = new CpuContext(memory, Generation.Gen5);
        context[CpuRegister.Rdi] = pathAddress;
        return context;
    }

    private static byte[] CreateReadChunk(ReadOnlySpan<byte> value)
    {
        var chunk = new byte[64];
        value.CopyTo(chunk);
        return chunk;
    }

    private static void AssertRegisteredName(CpuContext context, string expectedName)
    {
        var handle = unchecked((int)context[CpuRegister.Rax]);
        Assert.True(KernelModuleRegistry.TryGetModuleByHandle(handle, out var module));
        Assert.Equal(expectedName, module.Name);
    }
}
