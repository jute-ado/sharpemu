// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using SharpEmu.Libs.Np;
using Xunit;

namespace SharpEmu.Libs.Tests;

[Collection("NP auth state")]
public sealed class NpManagerRequestTests : IDisposable
{
    private const string CreateRequestNid = "GpLQDNKICac";
    private const string DeleteRequestNid = "S7QTn72PrDw";
    private const int RequestMaximum = unchecked((int)0x80550013);
    private const int RequestNotFound = unchecked((int)0x80550014);

    public NpManagerRequestTests()
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
    public void CreateRequestHasExactMetadata(Generation generation)
    {
        var export = Assert.Single(
            SharpEmu.Generated.SysAbiExportRegistry.CreateExports(generation),
            candidate => candidate.Nid == CreateRequestNid);

        Assert.Equal("sceNpCreateRequest", export.Name);
        Assert.Equal("libSceNpManager", export.LibraryName);
    }

    [Fact]
    public void CreateRequestAllocatesOneBasedIdsUpToTheConsoleLimit()
    {
        var (context, create, _) = CreateFixture();

        var requestIds = Enumerable.Range(0, 128)
            .Select(_ => create.Function(context))
            .ToArray();

        Assert.Equal(Enumerable.Range(1, 128), requestIds);
        Assert.Equal(RequestMaximum, create.Function(context));
    }

    [Fact]
    public void DeleteRequestRejectsUnknownIdsAndReusesTheLowestFreeSlot()
    {
        var (context, create, delete) = CreateFixture();
        var first = create.Function(context);
        var second = create.Function(context);

        context[CpuRegister.Rdi] = 0;
        Assert.Equal(RequestNotFound, delete.Function(context));
        context[CpuRegister.Rdi] = 129;
        Assert.Equal(RequestNotFound, delete.Function(context));

        context[CpuRegister.Rdi] = unchecked((ulong)first);
        Assert.Equal(0, delete.Function(context));
        Assert.Equal(RequestNotFound, delete.Function(context));
        Assert.Equal(first, create.Function(context));
        Assert.Equal(2, second);
    }

    [Fact]
    public void RuntimeResetDropsRequestsAndRestartsAllocation()
    {
        var (context, create, delete) = CreateFixture();
        Assert.Equal(1, create.Function(context));
        Assert.Equal(2, create.Function(context));

        NpLifecycle.ResetRuntimeState();

        context[CpuRegister.Rdi] = 1;
        Assert.Equal(RequestNotFound, delete.Function(context));
        Assert.Equal(1, create.Function(context));
    }

    private static (CpuContext Context, ExportedFunction Create, ExportedFunction Delete) CreateFixture()
    {
        var exports = SharpEmu.Generated.SysAbiExportRegistry.CreateExports(Generation.Gen5);
        var create = Assert.Single(exports, candidate => candidate.Nid == CreateRequestNid);
        var delete = Assert.Single(exports, candidate => candidate.Nid == DeleteRequestNid);
        return (new CpuContext(new FakeGuestMemory(), Generation.Gen5), create, delete);
    }
}
