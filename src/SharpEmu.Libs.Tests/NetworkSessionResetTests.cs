// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using SharpEmu.HLE;
using SharpEmu.Libs.Network;
using Xunit;

namespace SharpEmu.Libs.Tests;

[CollectionDefinition(NetworkSessionStateCollection.Name, DisableParallelization = true)]
public sealed class NetworkSessionStateCollection
{
    public const string Name = "Network session state";
}

[Collection(NetworkSessionStateCollection.Name)]
public sealed class NetworkSessionResetTests
{
    private const ulong NameAddress = 0x1000;
    private const ulong SocketAddress = 0x2000;
    private const ulong CallbackIdAddress = 0x3000;

    [Fact]
    public void ResetRuntimeStateReleasesResourcesAndRestartsAllIds()
    {
        var memory = new FakeGuestMemory();
        memory.AddRegion(NameAddress, "session\0"u8.ToArray());
        memory.AddRegion(SocketAddress, new byte[16]);
        memory.AddRegion(CallbackIdAddress, new byte[sizeof(uint)]);
        var context = new CpuContext(memory, Generation.Gen5);
        var port = ReserveUdpPort();
        WriteIpv4SocketAddress(memory, port);

        NetworkLifecycle.ResetRuntimeState();
        try
        {
            var pool = CreatePool(context);
            var resolver = CreateResolver(context, pool);
            var socket = CreateUdpSocket(context);
            Bind(context, socket);
            var ssl = CreateSslContext(context);
            var http = CreateHttpContext(context, pool, ssl);
            var template = CreateHttpTemplate(context, http);
            var http2 = CreateHttp2Context(context, pool, ssl);
            Assert.Equal(0u, RegisterNetCtlCallback(context));

            Assert.Equal(1, pool);
            Assert.Equal(0x2001, resolver);
            Assert.Equal(0x4001, socket);
            Assert.Equal(1, ssl);
            Assert.Equal(1, http);
            Assert.Equal(0x1001, template);
            Assert.Equal(1, http2);

            NetworkLifecycle.ResetRuntimeState();

            context[CpuRegister.Rdi] = unchecked((ulong)socket);
            Assert.NotEqual(0, NetExports.NetSocketClose(context));
            context[CpuRegister.Rdi] = unchecked((ulong)pool);
            Assert.NotEqual(0, NetExports.NetPoolDestroy(context));
            context[CpuRegister.Rdi] = unchecked((ulong)resolver);
            Assert.NotEqual(0, NetExports.NetResolverDestroy(context));
            context[CpuRegister.Rdi] = unchecked((ulong)template);
            Assert.NotEqual(0, HttpExports.HttpDeleteTemplate(context));
            context[CpuRegister.Rdi] = unchecked((ulong)http);
            Assert.NotEqual(0, HttpExports.HttpTerm(context));
            context[CpuRegister.Rdi] = unchecked((ulong)http2);
            Assert.NotEqual(0, Http2Exports.Http2Term(context));
            context[CpuRegister.Rdi] = unchecked((ulong)ssl);
            Assert.NotEqual(0, SslExports.SslTerm(context));

            var nextPool = CreatePool(context);
            var nextResolver = CreateResolver(context, nextPool);
            var nextSocket = CreateUdpSocket(context);
            Bind(context, nextSocket);
            var nextSsl = CreateSslContext(context);
            var nextHttp = CreateHttpContext(context, nextPool, nextSsl);
            Assert.Equal(1, nextPool);
            Assert.Equal(0x2001, nextResolver);
            Assert.Equal(0x4001, nextSocket);
            Assert.Equal(1, nextSsl);
            Assert.Equal(1, nextHttp);
            Assert.Equal(0x1001, CreateHttpTemplate(context, nextHttp));
            Assert.Equal(1, CreateHttp2Context(context, nextPool, nextSsl));
            Assert.Equal(0u, RegisterNetCtlCallback(context));
        }
        finally
        {
            NetworkLifecycle.ResetRuntimeState();
        }
    }

    private static int CreatePool(CpuContext context)
    {
        context[CpuRegister.Rdi] = NameAddress;
        context[CpuRegister.Rsi] = 0x4000;
        context[CpuRegister.Rdx] = 0;
        Assert.Equal(0, NetExports.NetPoolCreate(context));
        return unchecked((int)context[CpuRegister.Rax]);
    }

    private static int CreateResolver(CpuContext context, int pool)
    {
        context[CpuRegister.Rdi] = NameAddress;
        context[CpuRegister.Rsi] = unchecked((ulong)pool);
        context[CpuRegister.Rdx] = 0;
        Assert.Equal(0, NetExports.NetResolverCreate(context));
        return unchecked((int)context[CpuRegister.Rax]);
    }

    private static int CreateUdpSocket(CpuContext context)
    {
        context[CpuRegister.Rdi] = NameAddress;
        context[CpuRegister.Rsi] = 2;
        context[CpuRegister.Rdx] = 2;
        context[CpuRegister.Rcx] = 17;
        Assert.Equal(0, NetExports.NetSocket(context));
        return unchecked((int)context[CpuRegister.Rax]);
    }

    private static void Bind(CpuContext context, int socket)
    {
        context[CpuRegister.Rdi] = unchecked((ulong)socket);
        context[CpuRegister.Rsi] = SocketAddress;
        context[CpuRegister.Rdx] = 16;
        Assert.Equal(0, NetExports.NetBind(context));
    }

    private static int CreateSslContext(CpuContext context)
    {
        context[CpuRegister.Rdi] = 0x4000;
        Assert.Equal(0, SslExports.SslInit(context));
        return unchecked((int)context[CpuRegister.Rax]);
    }

    private static int CreateHttpContext(CpuContext context, int pool, int ssl)
    {
        context[CpuRegister.Rdi] = unchecked((ulong)pool);
        context[CpuRegister.Rsi] = unchecked((ulong)ssl);
        context[CpuRegister.Rdx] = 0x4000;
        Assert.Equal(0, HttpExports.HttpInit(context));
        return unchecked((int)context[CpuRegister.Rax]);
    }

    private static int CreateHttpTemplate(CpuContext context, int http)
    {
        context[CpuRegister.Rdi] = unchecked((ulong)http);
        context[CpuRegister.Rsi] = NameAddress;
        context[CpuRegister.Rdx] = 1;
        context[CpuRegister.Rcx] = 0;
        Assert.Equal(0, HttpExports.HttpCreateTemplate(context));
        return unchecked((int)context[CpuRegister.Rax]);
    }

    private static int CreateHttp2Context(CpuContext context, int pool, int ssl)
    {
        context[CpuRegister.Rdi] = unchecked((ulong)pool);
        context[CpuRegister.Rsi] = unchecked((ulong)ssl);
        context[CpuRegister.Rdx] = 0x4000;
        context[CpuRegister.Rcx] = 4;
        Assert.Equal(0, Http2Exports.Http2Init(context));
        return unchecked((int)context[CpuRegister.Rax]);
    }

    private static uint RegisterNetCtlCallback(CpuContext context)
    {
        context[CpuRegister.Rdi] = 0x1234_0000;
        context[CpuRegister.Rsi] = 0x5678_0000;
        context[CpuRegister.Rdx] = CallbackIdAddress;
        Assert.Equal(0, NetCtlExports.NetCtlRegisterCallback(context));
        Assert.True(context.TryReadUInt32(CallbackIdAddress, out var id));
        return id;
    }

    private static int ReserveUdpPort()
    {
        using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        socket.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        return ((IPEndPoint)socket.LocalEndPoint!).Port;
    }

    private static void WriteIpv4SocketAddress(FakeGuestMemory memory, int port)
    {
        var address = new byte[16];
        address[1] = 2;
        BinaryPrimitives.WriteUInt16BigEndian(address.AsSpan(2), checked((ushort)port));
        IPAddress.Loopback.GetAddressBytes().CopyTo(address, 4);
        Assert.True(memory.TryWrite(SocketAddress, address));
    }
}
