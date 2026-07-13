// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using System.Reflection;
using System.Runtime.InteropServices;
using SharpEmu.HLE;
using SharpEmu.Libs.Kernel;
using Xunit;

namespace SharpEmu.Libs.Tests;

public sealed class KernelIsStackTests
{
    private const ulong StackStart = 0x7000;
    private const ulong StackSize = 0x2000;
    private const ulong StartOutAddress = 0x1000;
    private const ulong EndOutAddress = 0x2000;

    [Fact]
    public void KernelIsStackReturnsRegisteredStackBounds()
    {
        var (context, startOut, endOut) = CreateContext(StackStart + 0x123);

        var result = KernelMemoryCompatExports.KernelIsStack(context);

        Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_OK, result);
        Assert.Equal(0UL, context[CpuRegister.Rax]);
        Assert.Equal(StackStart, BinaryPrimitives.ReadUInt64LittleEndian(startOut));
        Assert.Equal(StackStart + StackSize, BinaryPrimitives.ReadUInt64LittleEndian(endOut));
    }

    [Fact]
    public void KernelIsStackWritesZeroBoundsForMappedNonStackMemory()
    {
        var (context, startOut, endOut) = CreateContext(0x3000);

        var result = KernelMemoryCompatExports.KernelIsStack(context);

        Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_OK, result);
        Assert.Equal(0UL, BinaryPrimitives.ReadUInt64LittleEndian(startOut));
        Assert.Equal(0UL, BinaryPrimitives.ReadUInt64LittleEndian(endOut));
    }

    [Fact]
    public void KernelIsStackAllowsNullOutputPointers()
    {
        var (context, _, _) = CreateContext(StackStart);
        context[CpuRegister.Rsi] = 0;
        context[CpuRegister.Rdx] = 0;

        var result = KernelMemoryCompatExports.KernelIsStack(context);

        Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_OK, result);
        Assert.Equal(0UL, context[CpuRegister.Rax]);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(0xDEAD_0000)]
    public void KernelIsStackRejectsUnmappedAddress(ulong address)
    {
        var (context, _, _) = CreateContext(address);

        var result = KernelMemoryCompatExports.KernelIsStack(context);

        Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT, result);
        Assert.Equal(
            unchecked((ulong)(int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT),
            context[CpuRegister.Rax]);
    }

    [Fact]
    public void KernelIsStackRejectsUnreadableOutputPointer()
    {
        var (context, _, _) = CreateContext(StackStart);
        context[CpuRegister.Rsi] = 0xDEAD_0000;

        var result = KernelMemoryCompatExports.KernelIsStack(context);

        Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT, result);
        Assert.Equal(
            unchecked((ulong)(int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT),
            context[CpuRegister.Rax]);
    }

    [Fact]
    public void KernelIsStackWritesTrackedLibcOutputPointers()
    {
        var (context, _, _) = CreateContext(StackStart + 8);
        var outputs = Allocate(context, sizeof(ulong) * 2);

        try
        {
            context[CpuRegister.Rdi] = StackStart + 8;
            context[CpuRegister.Rsi] = outputs;
            context[CpuRegister.Rdx] = outputs + sizeof(ulong);

            Assert.Equal(
                (int)OrbisGen2Result.ORBIS_GEN2_OK,
                KernelMemoryCompatExports.KernelIsStack(context));
            Assert.Equal(unchecked((long)StackStart), Marshal.ReadInt64((nint)outputs));
            Assert.Equal(
                unchecked((long)(StackStart + StackSize)),
                Marshal.ReadInt64((nint)(outputs + sizeof(ulong))));
        }
        finally
        {
            context[CpuRegister.Rdi] = outputs;
            _ = KernelMemoryCompatExports.Free(context);
        }
    }

    [Fact]
    public void KernelIsStackExportMetadataIsExact()
    {
        var method = typeof(KernelMemoryCompatExports).GetMethod(
            nameof(KernelMemoryCompatExports.KernelIsStack),
            BindingFlags.Public | BindingFlags.Static);
        var attribute = method?.GetCustomAttribute<SysAbiExportAttribute>();

        Assert.NotNull(attribute);
        Assert.Equal("yDBwVAolDgg", attribute.Nid);
        Assert.Equal("sceKernelIsStack", attribute.ExportName);
        Assert.Equal("libKernel", attribute.LibraryName);
        Assert.Equal(Generation.Gen4 | Generation.Gen5, attribute.Target);
    }

    private static (CpuContext Context, byte[] StartOut, byte[] EndOut) CreateContext(ulong address)
    {
        var startOut = Enumerable.Repeat((byte)0xCC, sizeof(ulong)).ToArray();
        var endOut = Enumerable.Repeat((byte)0xCC, sizeof(ulong)).ToArray();
        var memory = new FakeGuestMemory();
        memory.AddRegion(0x3000, new byte[0x100]);
        memory.AddRegion(StackStart, new byte[StackSize]);
        memory.AddRegion(StartOutAddress, startOut);
        memory.AddRegion(EndOutAddress, endOut);
        memory.RegisterStackRange(StackStart, StackSize);
        var context = new CpuContext(memory, Generation.Gen5);
        context[CpuRegister.Rdi] = address;
        context[CpuRegister.Rsi] = StartOutAddress;
        context[CpuRegister.Rdx] = EndOutAddress;
        return (context, startOut, endOut);
    }

    private static ulong Allocate(CpuContext context, ulong size)
    {
        context[CpuRegister.Rdi] = size;
        Assert.Equal(
            (int)OrbisGen2Result.ORBIS_GEN2_OK,
            KernelMemoryCompatExports.Malloc(context));
        Assert.NotEqual(0UL, context[CpuRegister.Rax]);
        return context[CpuRegister.Rax];
    }
}
