// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using SharpEmu.Libs.Kernel;
using Xunit;

namespace SharpEmu.Libs.Tests;

public sealed class KernelSocketAddressParsingTests
{
    private const ulong SourceAddress = 0x1000;
    private const ulong DestinationAddress = 0x2000;
    private const byte Canary = 0xA5;

    [Fact]
    public void InetPtonParsesIpv4AddressIntoNetworkOrderBytes()
    {
        var destination = Enumerable.Repeat(Canary, 6).ToArray();
        var memory = new FakeGuestMemory();
        memory.AddRegion(SourceAddress, "127.0.0.1\0"u8.ToArray());
        memory.AddRegion(DestinationAddress, destination);
        var context = CreateContext(memory, SourceAddress);

        Assert.Equal(0, KernelSocketCompatExports.InetPton(context));
        Assert.Equal(1uL, context[CpuRegister.Rax]);
        Assert.Equal([127, 0, 0, 1, Canary, Canary], destination);
    }

    [Fact]
    public void InetPtonRejectsSourceAddressWrapWithoutChangingDestination()
    {
        var destination = Enumerable.Repeat(Canary, 4).ToArray();
        var memory = new FakeGuestMemory();
        memory.AddRegion(ulong.MaxValue, [(byte)'1']);
        memory.AddRegion(0, "27.0.0.1\0"u8.ToArray());
        memory.AddRegion(DestinationAddress, destination);
        var context = CreateContext(memory, ulong.MaxValue);

        Assert.Equal(0, KernelSocketCompatExports.InetPton(context));
        Assert.Equal(0uL, context[CpuRegister.Rax]);
        Assert.All(destination, value => Assert.Equal(Canary, value));
    }

    private static CpuContext CreateContext(FakeGuestMemory memory, ulong sourceAddress)
    {
        var context = new CpuContext(memory, Generation.Gen5);
        context[CpuRegister.Rdi] = 2;
        context[CpuRegister.Rsi] = sourceAddress;
        context[CpuRegister.Rdx] = DestinationAddress;
        return context;
    }
}
