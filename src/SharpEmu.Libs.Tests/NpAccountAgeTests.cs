// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using SharpEmu.Libs.Np;
using Xunit;

namespace SharpEmu.Libs.Tests;

[Collection("NP auth state")]
public sealed class NpAccountAgeTests : IDisposable
{
    private const string AccountAgeNid = "+4DegjBqV1g";
    private const int InvalidArgument = unchecked((int)0x80550003);
    private const int SignedOut = unchecked((int)0x80550006);
    private const int RequestNotFound = unchecked((int)0x80550014);

    public NpAccountAgeTests()
    {
        NpLifecycle.ResetRuntimeState();
    }

    public void Dispose()
    {
        NpLifecycle.ResetRuntimeState();
        GC.SuppressFinalize(this);
    }

    [Theory]
    [InlineData(Generation.Gen4)]
    [InlineData(Generation.Gen5)]
    public void AccountAgeHasExactMetadata(Generation generation)
    {
        var export = Assert.Single(
            SharpEmu.Generated.SysAbiExportRegistry.CreateExports(generation),
            candidate => candidate.Nid == AccountAgeNid);

        Assert.Equal("sceNpGetAccountAge", export.Name);
        Assert.Equal("libSceNpManager", export.LibraryName);
    }

    [Fact]
    public void AccountAgeWritesOfflineDefaultWithoutTouchingCanaries()
    {
        var (memory, context, create, accountAge, _) = CreateFixture();
        var output = new byte[] { 0xA5, 0xA5, 0xA5 };
        memory.AddRegion(0x1000, output);
        var requestId = create.Function(context);
        context[CpuRegister.Rdi] = unchecked((ulong)requestId);
        context[CpuRegister.Rsi] = 1000;
        context[CpuRegister.Rdx] = 0x1001;

        Assert.Equal(SignedOut, accountAge.Function(context));
        Assert.Equal(new byte[] { 0xA5, 0, 0xA5 }, output);
    }

    [Fact]
    public void AccountAgeRejectsInvalidArgumentsWithoutMutatingOutput()
    {
        var (memory, context, create, accountAge, _) = CreateFixture();
        var output = new byte[] { 0xA5 };
        memory.AddRegion(0x1000, output);

        context[CpuRegister.Rdi] = 0;
        context[CpuRegister.Rdx] = 0x1000;
        Assert.Equal(InvalidArgument, accountAge.Function(context));
        Assert.Equal(0xA5, output[0]);

        context[CpuRegister.Rdi] = unchecked((ulong)create.Function(context));
        context[CpuRegister.Rdx] = 0;
        Assert.Equal(InvalidArgument, accountAge.Function(context));
        Assert.Equal(0xA5, output[0]);
    }

    [Fact]
    public void AccountAgeRejectsStaleAndUnwritableRequestsWithoutMutation()
    {
        var (memory, context, create, accountAge, delete) = CreateFixture();
        var output = new byte[] { 0xA5 };
        memory.AddRegion(0x1000, output);
        var requestId = create.Function(context);
        context[CpuRegister.Rdi] = unchecked((ulong)requestId);
        Assert.Equal(0, delete.Function(context));

        context[CpuRegister.Rdi] = unchecked((ulong)requestId);
        context[CpuRegister.Rdx] = 0x1000;
        Assert.Equal(RequestNotFound, accountAge.Function(context));
        Assert.Equal(0xA5, output[0]);

        requestId = create.Function(context);
        context[CpuRegister.Rdi] = unchecked((ulong)requestId);
        context[CpuRegister.Rdx] = 0x2000;
        Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT, accountAge.Function(context));
        Assert.Equal(0xA5, output[0]);
    }

    private static (
        FakeGuestMemory Memory,
        CpuContext Context,
        ExportedFunction Create,
        ExportedFunction AccountAge,
        ExportedFunction Delete) CreateFixture()
    {
        var exports = SharpEmu.Generated.SysAbiExportRegistry.CreateExports(Generation.Gen5);
        var memory = new FakeGuestMemory();
        return (
            memory,
            new CpuContext(memory, Generation.Gen5),
            Assert.Single(exports, candidate => candidate.Nid == "GpLQDNKICac"),
            Assert.Single(exports, candidate => candidate.Nid == AccountAgeNid),
            Assert.Single(exports, candidate => candidate.Nid == "S7QTn72PrDw"));
    }
}
