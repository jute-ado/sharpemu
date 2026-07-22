// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Runtime.InteropServices;
using System.Text;
using SharpEmu.HLE;
using SharpEmu.Libs.Network;
using Xunit;

namespace SharpEmu.Libs.Tests;

[Collection(NetworkSessionStateCollection.Name)]
public sealed class NetworkIdentityContractTests : IDisposable
{
    private const string EtherNtostrNid = "v6M4txecCuo";
    private const string GetMacAddressNid = "6Oc0bLsIYe0";
    private const ulong MacAddress = 0x1000;
    private const ulong TextAddress = 0x2000;
    private const ulong NetCtlAddress = 0x3000;
    private const int NetErrorInvalidArgument = unchecked((int)0x80410116);
    private const int NetErrorNoSpace = unchecked((int)0x8041011C);

    public NetworkIdentityContractTests()
    {
        NetworkLifecycle.ResetRuntimeState();
    }

    public void Dispose()
    {
        NetworkLifecycle.ResetRuntimeState();
    }

    [Fact]
    public void IdentityExportsReturnOneStablePrivacyPreservingAddress()
    {
        var gen4Manager = CreateManager(Generation.Gen4);
        Assert.True(gen4Manager.TryGetExport(GetMacAddressNid, out var getMacExport));
        Assert.Equal("sceNetGetMacAddress", getMacExport.Name);
        Assert.Equal("libSceNet", getMacExport.LibraryName);
        Assert.Equal(Generation.Gen4 | Generation.Gen5, getMacExport.Target);

        var memory = new FakeGuestMemory();
        memory.AddRegion(MacAddress, new byte[6]);
        memory.AddRegion(NetCtlAddress, new byte[6]);
        var context = new CpuContext(memory, Generation.Gen5);
        var manager = CreateManager(Generation.Gen5);

        context[CpuRegister.Rdi] = MacAddress;
        context[CpuRegister.Rsi] = 0;
        Assert.True(manager.TryDispatch(GetMacAddressNid, context, out var result));
        Assert.Equal(0, (int)result);

        context[CpuRegister.Rdi] = 2;
        context[CpuRegister.Rsi] = NetCtlAddress;
        Assert.Equal(0, NetCtlExports.NetCtlGetInfo(context));

        var netAddress = ReadBytes(memory, MacAddress, 6);
        var netCtlAddress = ReadBytes(memory, NetCtlAddress, 6);
        Assert.Equal(new byte[] { 0x02, 0, 0, 0, 0, 1 }, netAddress);
        Assert.Equal(netAddress, netCtlAddress);
        Assert.Equal(0, netAddress[0] & 0x01);
        Assert.NotEqual(0, netAddress[0] & 0x02);
    }

    [Fact]
    public void EtherNtostrFormatsEveryOctetAndTerminatesTheResult()
    {
        var manager = CreateManager(Generation.Gen5);
        Assert.True(manager.TryGetExport(EtherNtostrNid, out var export));
        Assert.Equal("sceNetEtherNtostr", export.Name);
        Assert.Equal("libSceNet", export.LibraryName);
        Assert.Equal(Generation.Gen4 | Generation.Gen5, export.Target);

        var memory = new FakeGuestMemory();
        memory.AddRegion(MacAddress, new byte[] { 0x0A, 0x1B, 0x2C, 0x3D, 0x4E, 0x5F });
        memory.AddRegion(TextAddress, Enumerable.Repeat((byte)0xCC, 18).ToArray());
        var context = new CpuContext(memory, Generation.Gen5);
        context[CpuRegister.Rdi] = MacAddress;
        context[CpuRegister.Rsi] = TextAddress;
        context[CpuRegister.Rdx] = 18;

        Assert.True(manager.TryDispatch(EtherNtostrNid, context, out var result));
        Assert.Equal(0, (int)result);
        Assert.Equal(
            "0a:1b:2c:3d:4e:5f\0",
            Encoding.ASCII.GetString(ReadBytes(memory, TextAddress, 18)));
    }

    [Fact]
    public void IdentityExportsReportInvalidAndShortBuffersThroughNetErrno()
    {
        var memory = new FakeGuestMemory();
        memory.AddRegion(MacAddress, new byte[6]);
        memory.AddRegion(TextAddress, new byte[18]);
        var context = new CpuContext(memory, Generation.Gen5);
        var manager = CreateManager(Generation.Gen5);

        context[CpuRegister.Rdi] = 0;
        context[CpuRegister.Rsi] = 0;
        Assert.True(manager.TryDispatch(GetMacAddressNid, context, out var result));
        Assert.Equal(NetErrorInvalidArgument, (int)result);
        Assert.Equal(22, ReadNetErrno(context));

        context[CpuRegister.Rdi] = MacAddress;
        context[CpuRegister.Rsi] = TextAddress;
        context[CpuRegister.Rdx] = 17;
        Assert.True(manager.TryDispatch(EtherNtostrNid, context, out result));
        Assert.Equal(NetErrorNoSpace, (int)result);
        Assert.Equal(28, ReadNetErrno(context));

        context[CpuRegister.Rdi] = MacAddress + 0x100;
        context[CpuRegister.Rsi] = TextAddress;
        context[CpuRegister.Rdx] = 18;
        Assert.True(manager.TryDispatch(EtherNtostrNid, context, out result));
        Assert.Equal(NetErrorInvalidArgument, (int)result);
        Assert.Equal(22, ReadNetErrno(context));
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
}
