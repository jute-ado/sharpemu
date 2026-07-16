// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using SharpEmu.HLE;
using SharpEmu.Libs.Mouse;
using Xunit;

namespace SharpEmu.Libs.Tests;

public sealed class MouseLifecycleTests
{
    private const int PrimaryUserId = 0x10000000;
    private const int MouseErrorInvalidArgument = unchecked((int)0x80020002);
    private const int MouseErrorInvalidHandle = unchecked((int)0x80020003);
    private const int MouseErrorAlreadyOpened = unchecked((int)0x80020008);
    private const ulong DataAddress = 0x1000;

    [Fact]
    public void OpenCloseAndReopenFollowHandleLifecycle()
    {
        var context = CreateContext();
        Initialize(context);

        Assert.Equal(0, Open(context, index: 0));
        AssertCall(MouseErrorAlreadyOpened, context, MouseExports.MouseOpen);

        context[CpuRegister.Rdi] = 0;
        AssertCall(0, context, MouseExports.MouseClose);
        Assert.Equal(0, Open(context, index: 0));

        context[CpuRegister.Rdi] = 0;
        AssertCall(0, context, MouseExports.MouseClose);
    }

    [Fact]
    public void OpenValidatesUserTypeAndIndex()
    {
        var context = CreateContext();
        Initialize(context);

        context[CpuRegister.Rdi] = 42;
        context[CpuRegister.Rsi] = 0;
        context[CpuRegister.Rdx] = 0;
        AssertCall(MouseErrorInvalidArgument, context, MouseExports.MouseOpen);

        context[CpuRegister.Rdi] = PrimaryUserId;
        context[CpuRegister.Rsi] = 1;
        AssertCall(MouseErrorInvalidArgument, context, MouseExports.MouseOpen);

        context[CpuRegister.Rsi] = 0;
        context[CpuRegister.Rdx] = 2;
        AssertCall(MouseErrorInvalidArgument, context, MouseExports.MouseOpen);
    }

    [Fact]
    public void ReadWritesOneNeutralTimestampedSample()
    {
        var output = new byte[0x28];
        var context = CreateContext(output);
        Initialize(context);
        var handle = Open(context, index: 0);

        context[CpuRegister.Rdi] = unchecked((ulong)handle);
        context[CpuRegister.Rsi] = DataAddress;
        context[CpuRegister.Rdx] = 1;
        AssertCall(1, context, MouseExports.MouseRead);

        Assert.NotEqual(0UL, BinaryPrimitives.ReadUInt64LittleEndian(output));
        Assert.Equal(0, output[0x08]);

        context[CpuRegister.Rdi] = unchecked((ulong)handle);
        AssertCall(0, context, MouseExports.MouseClose);
    }

    [Fact]
    public void ClosedHandleCannotBeRead()
    {
        var context = CreateContext(new byte[0x28]);
        Initialize(context);
        var handle = Open(context, index: 0);

        context[CpuRegister.Rdi] = unchecked((ulong)handle);
        AssertCall(0, context, MouseExports.MouseClose);

        context[CpuRegister.Rsi] = DataAddress;
        context[CpuRegister.Rdx] = 1;
        AssertCall(MouseErrorInvalidHandle, context, MouseExports.MouseRead);
    }

    [Fact]
    public async Task ConcurrentOpenClaimsHandleExactlyOnce()
    {
        var initializer = CreateContext();
        Initialize(initializer);

        var calls = Enumerable.Range(0, 16)
            .Select(_ => Task.Run(() =>
            {
                var context = CreateContext();
                return Open(context, index: 1);
            }))
            .ToArray();
        var results = await Task.WhenAll(calls);

        Assert.Single(results, result => result == 1);
        Assert.Equal(15, results.Count(result => result == MouseErrorAlreadyOpened));

        initializer[CpuRegister.Rdi] = 1;
        AssertCall(0, initializer, MouseExports.MouseClose);
    }

    [Fact]
    public void CoreExportsHaveExactGeneratedMetadata()
    {
        var exports = SharpEmu.Generated.SysAbiExportRegistry
            .CreateExports(Generation.Gen5);

        AssertExport(exports, "Qs0wWulgl7U", "sceMouseInit");
        AssertExport(exports, "RaqxZIf6DvE", "sceMouseOpen");
        AssertExport(exports, "x8qnXqh-tiM", "sceMouseRead");
        AssertExport(exports, "cAnT0Rw-IwU", "sceMouseClose");
    }

    private static CpuContext CreateContext(byte[]? output = null)
    {
        var memory = new FakeGuestMemory();
        if (output is not null)
        {
            memory.AddRegion(DataAddress, output);
        }

        return new CpuContext(memory, Generation.Gen5);
    }

    private static void Initialize(CpuContext context) =>
        AssertCall(0, context, MouseExports.MouseInit);

    private static int Open(CpuContext context, int index)
    {
        context[CpuRegister.Rdi] = PrimaryUserId;
        context[CpuRegister.Rsi] = 0;
        context[CpuRegister.Rdx] = unchecked((ulong)index);
        return Call(context, MouseExports.MouseOpen);
    }

    private static int Call(CpuContext context, Func<CpuContext, int> export)
    {
        var result = export(context);
        Assert.Equal(unchecked((ulong)result), context[CpuRegister.Rax]);
        return result;
    }

    private static void AssertCall(
        int expected,
        CpuContext context,
        Func<CpuContext, int> export) =>
        Assert.Equal(expected, Call(context, export));

    private static void AssertExport(
        IReadOnlyList<ExportedFunction> exports,
        string nid,
        string name)
    {
        var export = Assert.Single(exports, candidate => candidate.Nid == nid);
        Assert.Equal(name, export.Name);
        Assert.Equal("libSceMouse", export.LibraryName);
    }
}
