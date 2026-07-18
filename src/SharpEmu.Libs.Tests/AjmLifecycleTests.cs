// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using SharpEmu.Libs.Ajm;
using Xunit;

namespace SharpEmu.Libs.Tests;

public sealed class AjmLifecycleTests
{
    private const int InvalidParameter = unchecked((int)0x806A0001);
    private const int InvalidContext = unchecked((int)0x80930002);
    private const ulong FirstContextAddress = 0x1000;
    private const ulong SecondContextAddress = 0x2000;

    [Fact]
    public void InitializeCreatesUniqueContexts()
    {
        var context = CreateContext();
        var first = Initialize(context, FirstContextAddress);
        var second = Initialize(context, SecondContextAddress);

        try
        {
            Assert.NotEqual(0U, first);
            Assert.NotEqual(first, second);
        }
        finally
        {
            Finalize(context, first);
            Finalize(context, second);
        }
    }

    [Fact]
    public void FinalizeInvalidatesContextForModuleRegistration()
    {
        var context = CreateContext();
        var contextId = Initialize(context, FirstContextAddress);

        context[CpuRegister.Rdi] = contextId;
        context[CpuRegister.Rsi] = 0;
        context[CpuRegister.Rdx] = 0;
        AssertCall(0, context, AjmExports.AjmModuleRegister);

        Finalize(context, contextId);

        context[CpuRegister.Rdi] = contextId;
        Assert.Equal(InvalidContext, AjmExports.AjmModuleRegister(context));
    }

    [Fact]
    public void InvalidInitializeParametersDoNotModifyGuestOutput()
    {
        var context = CreateContext();
        context[CpuRegister.Rdi] = 1;
        context[CpuRegister.Rsi] = FirstContextAddress;

        Assert.Equal(InvalidParameter, AjmExports.AjmInitialize(context));
        Assert.True(context.TryReadUInt32(FirstContextAddress, out var value));
        Assert.Equal(0xA5A5_A5A5U, value);
    }

    [Fact]
    public void CoreExportsHaveExactGeneratedMetadata()
    {
        var exports = SharpEmu.Generated.SysAbiExportRegistry
            .CreateExports(Generation.Gen5);

        AssertExport(exports, "dl+4eHSzUu4", "sceAjmInitialize");
        AssertExport(exports, "MHur6qCsUus", "sceAjmFinalize");
        AssertExport(exports, "Q3dyFuwGn64", "sceAjmModuleRegister");
        AssertExport(exports, "Wi7DtlLV+KI", "sceAjmModuleUnregister");
        AssertExport(exports, "MmpF1XsQiHw", "sceAjmBatchInitialize");
    }

    private static CpuContext CreateContext()
    {
        var memory = new FakeGuestMemory();
        memory.AddRegion(FirstContextAddress, [0xA5, 0xA5, 0xA5, 0xA5]);
        memory.AddRegion(SecondContextAddress, new byte[sizeof(uint)]);
        return new CpuContext(memory, Generation.Gen5);
    }

    private static uint Initialize(CpuContext context, ulong outputAddress)
    {
        context[CpuRegister.Rdi] = 0;
        context[CpuRegister.Rsi] = outputAddress;
        AssertCall(0, context, AjmExports.AjmInitialize);
        Assert.True(context.TryReadUInt32(outputAddress, out var contextId));
        return contextId;
    }

    private static void Finalize(CpuContext context, uint contextId)
    {
        context[CpuRegister.Rdi] = contextId;
        AssertCall(0, context, AjmExports.AjmFinalize);
    }

    private static void AssertCall(
        int expected,
        CpuContext context,
        Func<CpuContext, int> export)
    {
        Assert.Equal(expected, export(context));
        Assert.Equal(unchecked((ulong)expected), context[CpuRegister.Rax]);
    }

    private static void AssertExport(
        IReadOnlyList<ExportedFunction> exports,
        string nid,
        string name)
    {
        var export = Assert.Single(exports, candidate => candidate.Nid == nid);
        Assert.Equal(name, export.Name);
        Assert.Equal("libSceAjm", export.LibraryName);
    }
}
