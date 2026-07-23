// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Runtime.InteropServices;
using System.Text;
using SharpEmu.HLE;
using SharpEmu.Libs.Network;
using Xunit;

namespace SharpEmu.Libs.Tests;

[Collection(NetworkSessionStateCollection.Name)]
public sealed class NetworkInetPtonTests : IDisposable
{
    private const string InetPtonNid = "8Kcp5d-q1Uo";
    private const ulong SourceAddress = 0x1000;
    private const ulong DestinationAddress = 0x2000;
    private const int AfInet = 2;
    private const int AfInet6 = 28;
    private const int NetErrorInvalidArgument = unchecked((int)0x80410116);
    private const int NetErrorAddressFamilyNotSupported = unchecked((int)0x8041012F);

    public NetworkInetPtonTests()
    {
        NetworkLifecycle.ResetRuntimeState();
    }

    public void Dispose()
    {
        NetworkLifecycle.ResetRuntimeState();
    }

    [Fact]
    public void ExportMetadataMatchesTheGen4AndGen5Abi()
    {
        foreach (var generation in new[] { Generation.Gen4, Generation.Gen5 })
        {
            var manager = CreateManager(generation);

            Assert.True(manager.TryGetExport(InetPtonNid, out var export));
            Assert.Equal("sceNetInetPton", export.Name);
            Assert.Equal("libSceNet", export.LibraryName);
            Assert.Equal(Generation.Gen4 | Generation.Gen5, export.Target);
        }
    }

    [Fact]
    public void ConvertsStrictIpv4DottedDecimalToNetworkOrder()
    {
        var (manager, context, memory) = CreateContext("192.0.2.128", 5);
        context[CpuRegister.Rdi] = AfInet;

        Assert.True(manager.TryDispatch(InetPtonNid, context, out var result));
        Assert.Equal(1, (int)result);
        Assert.Equal(1UL, context[CpuRegister.Rax]);
        Assert.Equal(
            new byte[] { 192, 0, 2, 128, 0xCC },
            ReadBytes(memory, DestinationAddress, 5));
    }

    [Fact]
    public void ConvertsCompressedIpv6ToSixteenNetworkOrderBytes()
    {
        var (manager, context, memory) = CreateContext("2001:db8::1", 17);
        context[CpuRegister.Rdi] = AfInet6;

        Assert.True(manager.TryDispatch(InetPtonNid, context, out var result));
        Assert.Equal(1, (int)result);
        Assert.Equal(1UL, context[CpuRegister.Rax]);
        Assert.Equal(
            new byte[]
            {
                0x20, 0x01, 0x0D, 0xB8,
                0, 0, 0, 0,
                0, 0, 0, 0,
                0, 0, 0, 1,
                0xCC,
            },
            ReadBytes(memory, DestinationAddress, 17));
    }

    [Theory]
    [InlineData("127.1")]
    [InlineData("0x7f.0.0.1")]
    [InlineData("1.2.3")]
    [InlineData("1.2.3.4.5")]
    [InlineData("256.0.0.1")]
    [InlineData("+1.2.3.4")]
    [InlineData("1.2.3.4 ")]
    public void RejectsNonDottedDecimalIpv4WithoutWritingTheDestination(string source)
    {
        var (manager, context, memory) = CreateContext(source, 4);
        context[CpuRegister.Rdi] = AfInet;

        Assert.True(manager.TryDispatch(InetPtonNid, context, out var result));
        Assert.Equal(0, (int)result);
        Assert.Equal(0UL, context[CpuRegister.Rax]);
        Assert.Equal(
            Enumerable.Repeat((byte)0xCC, 4),
            ReadBytes(memory, DestinationAddress, 4));
    }

    [Theory]
    [InlineData("[::1]")]
    [InlineData("fe80::1%3")]
    [InlineData("2001:db8:::1")]
    [InlineData("192.0.2.1")]
    public void RejectsNonPresentationIpv6WithoutWritingTheDestination(string source)
    {
        var (manager, context, memory) = CreateContext(source, 16);
        context[CpuRegister.Rdi] = AfInet6;

        Assert.True(manager.TryDispatch(InetPtonNid, context, out var result));
        Assert.Equal(0, (int)result);
        Assert.Equal(0UL, context[CpuRegister.Rax]);
        Assert.Equal(
            Enumerable.Repeat((byte)0xCC, 16),
            ReadBytes(memory, DestinationAddress, 16));
    }

    [Fact]
    public void UnsupportedAddressFamilyReportsNetErrorAndErrnoWithoutWriting()
    {
        var (manager, context, memory) = CreateContext("192.0.2.1", 16);
        context[CpuRegister.Rdi] = 1234;

        Assert.True(manager.TryDispatch(InetPtonNid, context, out var result));
        Assert.Equal(NetErrorAddressFamilyNotSupported, (int)result);
        Assert.Equal(
            unchecked((ulong)NetErrorAddressFamilyNotSupported),
            context[CpuRegister.Rax]);
        Assert.Equal(47, ReadNetErrno(context));
        Assert.Equal(
            Enumerable.Repeat((byte)0xCC, 16),
            ReadBytes(memory, DestinationAddress, 16));
    }

    [Fact]
    public void InvalidGuestPointersReportInvalidArgumentAndDoNotPartiallyWrite()
    {
        var manager = CreateManager(Generation.Gen5);
        var memory = new FakeGuestMemory();
        memory.AddRegion(SourceAddress, Terminated("192.0.2.1"));
        memory.AddRegion(DestinationAddress, Enumerable.Repeat((byte)0xCC, 4).ToArray());
        var context = new CpuContext(memory, Generation.Gen5);
        context[CpuRegister.Rdi] = AfInet;
        context[CpuRegister.Rsi] = SourceAddress;
        context[CpuRegister.Rdx] = DestinationAddress;

        context[CpuRegister.Rsi] = 0;
        Assert.True(manager.TryDispatch(InetPtonNid, context, out var result));
        Assert.Equal(NetErrorInvalidArgument, (int)result);
        Assert.Equal(22, ReadNetErrno(context));

        context[CpuRegister.Rsi] = SourceAddress;
        context[CpuRegister.Rdx] = DestinationAddress + 4;
        Assert.True(manager.TryDispatch(InetPtonNid, context, out result));
        Assert.Equal(NetErrorInvalidArgument, (int)result);
        Assert.Equal(22, ReadNetErrno(context));
        Assert.Equal(
            Enumerable.Repeat((byte)0xCC, 4),
            ReadBytes(memory, DestinationAddress, 4));
    }

    private static (
        ModuleManager Manager,
        CpuContext Context,
        FakeGuestMemory Memory) CreateContext(string source, int destinationLength)
    {
        var manager = CreateManager(Generation.Gen5);
        var memory = new FakeGuestMemory();
        memory.AddRegion(SourceAddress, Terminated(source));
        memory.AddRegion(
            DestinationAddress,
            Enumerable.Repeat((byte)0xCC, destinationLength).ToArray());
        var context = new CpuContext(memory, Generation.Gen5);
        context[CpuRegister.Rsi] = SourceAddress;
        context[CpuRegister.Rdx] = DestinationAddress;
        return (manager, context, memory);
    }

    private static ModuleManager CreateManager(Generation generation)
    {
        var manager = new ModuleManager();
        manager.RegisterExports(
            SharpEmu.Generated.SysAbiExportRegistry.CreateExports(generation));
        return manager;
    }

    private static byte[] ReadBytes(FakeGuestMemory memory, ulong address, int length)
    {
        var bytes = new byte[length];
        Assert.True(memory.TryRead(address, bytes));
        return bytes;
    }

    private static int ReadNetErrno(CpuContext context)
    {
        Assert.Equal(0, NetExports.NetErrnoLoc(context));
        return Marshal.ReadInt32(unchecked((nint)context[CpuRegister.Rax]));
    }

    private static byte[] Terminated(string value)
    {
        return Encoding.UTF8.GetBytes(value + '\0');
    }
}
