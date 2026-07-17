// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using SharpEmu.HLE;
using SharpEmu.Libs.Kernel;
using Xunit;

namespace SharpEmu.Libs.Tests;

public sealed class KernelTlsIntegrationTests : IDisposable
{
    private const ulong DescriptorAddress = 0x1000;
    private const ulong ThreadPointer = 0x20000;

    public KernelTlsIntegrationTests() => GuestTlsTemplate.Reset();

    public void Dispose() => GuestTlsTemplate.Reset();

    [Fact]
    public void TlsGetAddrUsesRegisteredTemplateAndRejectsInvalidLookups()
    {
        var memory = CreateMemory();
        var context = new CpuContext(memory, Generation.Gen5)
        {
            FsBase = ThreadPointer,
        };
        var staticOffset = GuestTlsTemplate.RegisterModule(1, [0xA5, 0x5A], 8, 8);
        GuestTlsTemplate.SeedThreadBlock(context, ThreadPointer);

        WriteDescriptor(memory, moduleId: 1, offset: 1);
        context[CpuRegister.Rdi] = DescriptorAddress;

        Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_OK, KernelMemoryCompatExports.TlsGetAddr(context));
        Assert.Equal(ThreadPointer - staticOffset + 1, context[CpuRegister.Rax]);
        Assert.True(context.TryReadByte(context[CpuRegister.Rax], out var initializedByte));
        Assert.Equal(0x5A, initializedByte);

        WriteDescriptor(memory, moduleId: 1, offset: 8);
        Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_OK, KernelMemoryCompatExports.TlsGetAddr(context));
        Assert.Equal(0UL, context[CpuRegister.Rax]);

        WriteDescriptor(memory, moduleId: 99, offset: 0);
        Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_OK, KernelMemoryCompatExports.TlsGetAddr(context));
        Assert.Equal(0UL, context[CpuRegister.Rax]);
    }

    [Fact]
    public void TlsGetAddrUsesLazyDtvEntryForModuleRegisteredAfterThreadStartup()
    {
        var memory = CreateMemory();
        var context = new CpuContext(memory, Generation.Gen5)
        {
            FsBase = ThreadPointer,
        };
        GuestTlsTemplate.RegisterModule(1, [0x11], 4, 4);
        GuestTlsTemplate.SeedThreadBlock(context, ThreadPointer);
        GuestTlsTemplate.RegisterModule(2, [0x7A, 0x6B], 4, 0x10);
        WriteDescriptor(memory, moduleId: 2, offset: 1);
        context[CpuRegister.Rdi] = DescriptorAddress;

        Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_OK, KernelMemoryCompatExports.TlsGetAddr(context));
        var address = context[CpuRegister.Rax];

        Assert.NotEqual(0UL, address);
        Assert.Equal(0x6B, System.Runtime.InteropServices.Marshal.ReadByte(unchecked((nint)address)));
    }

    private static FakeGuestMemory CreateMemory()
    {
        var memory = new FakeGuestMemory();
        memory.AddRegion(DescriptorAddress, new byte[2 * sizeof(ulong)]);
        memory.AddRegion(
            ThreadPointer - GuestTlsTemplate.StartupStaticTlsReservation,
            new byte[checked((int)(GuestTlsTemplate.StartupStaticTlsReservation + 0x100))]);
        return memory;
    }

    private static void WriteDescriptor(FakeGuestMemory memory, ulong moduleId, ulong offset)
    {
        Span<byte> descriptor = stackalloc byte[2 * sizeof(ulong)];
        BinaryPrimitives.WriteUInt64LittleEndian(descriptor, moduleId);
        BinaryPrimitives.WriteUInt64LittleEndian(descriptor[sizeof(ulong)..], offset);
        Assert.True(memory.TryWrite(DescriptorAddress, descriptor));
    }
}
