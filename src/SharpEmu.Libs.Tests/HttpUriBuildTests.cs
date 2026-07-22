// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using System.Text;
using SharpEmu.HLE;
using Xunit;

namespace SharpEmu.Libs.Tests;

public sealed class HttpUriBuildTests
{
    private const string UriBuildNid = "5LZA+KPISVA";
    private const ulong ElementAddress = 0x1000;
    private const ulong SchemeAddress = 0x2000;
    private const ulong UsernameAddress = 0x3000;
    private const ulong PasswordAddress = 0x4000;
    private const ulong HostnameAddress = 0x5000;
    private const ulong PathAddress = 0x6000;
    private const ulong QueryAddress = 0x7000;
    private const ulong FragmentAddress = 0x8000;
    private const ulong OutputAddress = 0x9000;
    private const ulong RequiredAddress = 0xA000;
    private const int HttpErrorOutOfMemory = unchecked((int)0x80431022);
    private const int HttpErrorInvalidValue = unchecked((int)0x804311FE);
    private const int HttpErrorInvalidUrl = unchecked((int)0x80433060);

    [Fact]
    public void UriBuildCombinesEveryElementAndReportsExactSize()
    {
        const string expected = "https://user:pass@example.com:8443/a/b?x=1#frag";
        var fixture = CreateFixture(
            opaque: false,
            scheme: "https",
            username: "user",
            password: "pass",
            hostname: "example.com",
            path: "/a/b",
            query: "?x=1",
            fragment: "#frag",
            port: 8443,
            outputSize: 128);
        var manager = CreateManager(Generation.Gen5);

        Assert.True(manager.TryGetExport(UriBuildNid, out var export));
        Assert.Equal("sceHttpUriBuild", export.Name);
        Assert.Equal("libSceHttp", export.LibraryName);
        Assert.Equal(Generation.Gen4 | Generation.Gen5, export.Target);

        SetArguments(fixture.Context, OutputAddress, RequiredAddress, 128, 0xFF);
        Assert.True(manager.TryDispatch(UriBuildNid, fixture.Context, out var result));
        Assert.Equal(0, (int)result);

        var expectedBytes = Encoding.UTF8.GetBytes(expected + '\0');
        Assert.Equal((ulong)expectedBytes.Length, ReadRequired(fixture.Memory));
        Assert.Equal(expectedBytes, ReadBytes(fixture.Memory, OutputAddress, expectedBytes.Length));
        Assert.All(
            ReadBytes(fixture.Memory, OutputAddress + (ulong)expectedBytes.Length, 128 - expectedBytes.Length),
            value => Assert.Equal(0xA5, value));
    }

    [Fact]
    public void UriBuildSupportsSizeQueriesOptionMasksAndDefaultPorts()
    {
        const string expected = "https://example.com/foo";
        var fixture = CreateFixture(
            opaque: false,
            scheme: "https",
            username: "ignored",
            password: "ignored",
            hostname: "example.com",
            path: "/foo",
            query: "?ignored=1",
            fragment: "#ignored",
            port: 443,
            outputSize: 64);
        var manager = CreateManager(Generation.Gen5);
        const ulong schemeHostPortPath = 0x01 | 0x02 | 0x04 | 0x08;

        SetArguments(fixture.Context, 0, RequiredAddress, 0, schemeHostPortPath);
        Assert.True(manager.TryDispatch(UriBuildNid, fixture.Context, out var result));
        Assert.Equal(0, (int)result);
        Assert.Equal((ulong)expected.Length + 1, ReadRequired(fixture.Memory));

        SetArguments(fixture.Context, OutputAddress, RequiredAddress, 64, schemeHostPortPath);
        Assert.True(manager.TryDispatch(UriBuildNid, fixture.Context, out result));
        Assert.Equal(0, (int)result);
        Assert.Equal(
            expected + '\0',
            Encoding.UTF8.GetString(ReadBytes(fixture.Memory, OutputAddress, expected.Length + 1)));
    }

    [Fact]
    public void UriBuildRejectsInvalidInputsAndNeverPartiallyWritesShortOutput()
    {
        const string expected = "http://example.com/path";
        var fixture = CreateFixture(
            opaque: false,
            scheme: "http",
            username: string.Empty,
            password: string.Empty,
            hostname: "example.com",
            path: "/path",
            query: string.Empty,
            fragment: string.Empty,
            port: 80,
            outputSize: 4);
        var manager = CreateManager(Generation.Gen5);

        SetArguments(fixture.Context, OutputAddress, RequiredAddress, 4, 0xFF);
        fixture.Context[CpuRegister.Rcx] = 0;
        Assert.True(manager.TryDispatch(UriBuildNid, fixture.Context, out var result));
        Assert.Equal(HttpErrorInvalidUrl, (int)result);

        SetArguments(fixture.Context, 0, 0, 0, 0xFF);
        Assert.True(manager.TryDispatch(UriBuildNid, fixture.Context, out result));
        Assert.Equal(HttpErrorInvalidValue, (int)result);

        SetArguments(fixture.Context, OutputAddress, RequiredAddress, 4, 0xFF);
        Assert.True(manager.TryDispatch(UriBuildNid, fixture.Context, out result));
        Assert.Equal(HttpErrorOutOfMemory, (int)result);
        Assert.Equal((ulong)expected.Length + 1, ReadRequired(fixture.Memory));
        Assert.All(ReadBytes(fixture.Memory, OutputAddress, 4), value => Assert.Equal(0xA5, value));
    }

    private static UriBuildFixture CreateFixture(
        bool opaque,
        string scheme,
        string username,
        string password,
        string hostname,
        string path,
        string query,
        string fragment,
        ushort port,
        int outputSize)
    {
        var element = new byte[80];
        element[0] = opaque ? (byte)1 : (byte)0;
        WritePointer(element, 8, SchemeAddress);
        WritePointer(element, 16, UsernameAddress);
        WritePointer(element, 24, PasswordAddress);
        WritePointer(element, 32, HostnameAddress);
        WritePointer(element, 40, PathAddress);
        WritePointer(element, 48, QueryAddress);
        WritePointer(element, 56, FragmentAddress);
        BinaryPrimitives.WriteUInt16LittleEndian(element.AsSpan(64), port);

        var memory = new FakeGuestMemory();
        memory.AddRegion(ElementAddress, element);
        AddString(memory, SchemeAddress, scheme);
        AddString(memory, UsernameAddress, username);
        AddString(memory, PasswordAddress, password);
        AddString(memory, HostnameAddress, hostname);
        AddString(memory, PathAddress, path);
        AddString(memory, QueryAddress, query);
        AddString(memory, FragmentAddress, fragment);
        memory.AddRegion(OutputAddress, Enumerable.Repeat((byte)0xA5, outputSize).ToArray());
        memory.AddRegion(RequiredAddress, new byte[sizeof(ulong)]);
        return new UriBuildFixture(memory, new CpuContext(memory, Generation.Gen5));
    }

    private static void SetArguments(
        CpuContext context,
        ulong outputAddress,
        ulong requiredAddress,
        ulong preparedSize,
        ulong option)
    {
        context[CpuRegister.Rdi] = outputAddress;
        context[CpuRegister.Rsi] = requiredAddress;
        context[CpuRegister.Rdx] = preparedSize;
        context[CpuRegister.Rcx] = ElementAddress;
        context[CpuRegister.R8] = option;
    }

    private static ModuleManager CreateManager(Generation generation)
    {
        var manager = new ModuleManager();
        manager.RegisterExports(
            SharpEmu.Generated.SysAbiExportRegistry.CreateExports(generation));
        return manager;
    }

    private static void AddString(FakeGuestMemory memory, ulong address, string value)
        => memory.AddRegion(address, Encoding.UTF8.GetBytes(value + '\0'));

    private static void WritePointer(byte[] element, int offset, ulong value)
        => BinaryPrimitives.WriteUInt64LittleEndian(element.AsSpan(offset), value);

    private static ulong ReadRequired(FakeGuestMemory memory)
        => BinaryPrimitives.ReadUInt64LittleEndian(ReadBytes(memory, RequiredAddress, sizeof(ulong)));

    private static byte[] ReadBytes(FakeGuestMemory memory, ulong address, int length)
    {
        var bytes = new byte[length];
        Assert.True(memory.TryRead(address, bytes));
        return bytes;
    }

    private sealed record UriBuildFixture(FakeGuestMemory Memory, CpuContext Context);
}
