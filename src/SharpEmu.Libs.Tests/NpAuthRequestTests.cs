// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using SharpEmu.HLE;
using SharpEmu.Libs.Np;
using Xunit;

namespace SharpEmu.Libs.Tests;

[CollectionDefinition("NP auth state", DisableParallelization = true)]
public sealed class NpAuthStateCollection;

[Collection("NP auth state")]
public sealed class NpAuthRequestTests
{
    [Fact]
    public void AsyncRequestsValidateParametersEnforceLimitAndReuseDeletedSlot()
    {
        NpLifecycle.ResetRuntimeState();
        var exports = SharpEmu.Generated.SysAbiExportRegistry.CreateExports(Generation.Gen5);
        var create = Assert.Single(exports, export => export.Nid == "N+mr7GjTvr8");
        var delete = Assert.Single(exports, export => export.Nid == "H8wG9Bk-nPc");
        var parameter = new byte[24];
        BinaryPrimitives.WriteUInt64LittleEndian(parameter, 24);
        var memory = new FakeGuestMemory();
        memory.AddRegion(0x1000, parameter);
        var context = new CpuContext(memory, Generation.Gen5);
        context[CpuRegister.Rdi] = 0x1000;

        var ids = Enumerable.Range(0, 16).Select(_ => create.Function(context)).ToArray();
        Assert.Equal(Enumerable.Range(1, 16).Select(index => 0x1000_0000 + index), ids);
        Assert.True(create.Function(context) < 0);

        context[CpuRegister.Rdi] = unchecked((ulong)ids[4]);
        Assert.Equal(0, delete.Function(context));
        Assert.True(delete.Function(context) < 0);

        context[CpuRegister.Rdi] = 0x1000;
        Assert.Equal(ids[4], create.Function(context));
        NpLifecycle.ResetRuntimeState();
    }

    [Fact]
    public void AsyncRequestRejectsNullAndWrongSizedParameters()
    {
        var export = Assert.Single(
            SharpEmu.Generated.SysAbiExportRegistry.CreateExports(Generation.Gen5),
            candidate => candidate.Nid == "N+mr7GjTvr8");
        var parameter = new byte[24];
        BinaryPrimitives.WriteUInt64LittleEndian(parameter, 16);
        var memory = new FakeGuestMemory();
        memory.AddRegion(0x1000, parameter);
        var context = new CpuContext(memory, Generation.Gen5);

        context[CpuRegister.Rdi] = 0;
        Assert.Equal(unchecked((int)0x80550301), export.Function(context));
        context[CpuRegister.Rdi] = 0x1000;
        Assert.Equal(unchecked((int)0x80550302), export.Function(context));
    }

    [Fact]
    public void AuthorizationCodeV3CompletesAsyncRequestAsSignedOut()
    {
        NpLifecycle.ResetRuntimeState();
        var exports = SharpEmu.Generated.SysAbiExportRegistry.CreateExports(Generation.Gen5);
        var create = Assert.Single(exports, export => export.Nid == "N+mr7GjTvr8");
        var authorize = Assert.Single(exports, export => export.Nid == "KI4dHLlTNl0");
        var poll = Assert.Single(exports, export => export.Nid == "gjSyfzSsDcE");
        var createParameter = new byte[24];
        BinaryPrimitives.WriteUInt64LittleEndian(createParameter, 24);
        var authParameter = new byte[32];
        BinaryPrimitives.WriteUInt64LittleEndian(authParameter, 32);
        BinaryPrimitives.WriteInt32LittleEndian(authParameter.AsSpan(8), 1);
        BinaryPrimitives.WriteUInt64LittleEndian(authParameter.AsSpan(16), 0x3000);
        BinaryPrimitives.WriteUInt64LittleEndian(authParameter.AsSpan(24), 0x4000);
        var authCode = Enumerable.Repeat((byte)0xA5, 16).ToArray();
        var result = new byte[sizeof(int)];
        var memory = new FakeGuestMemory();
        memory.AddRegion(0x1000, createParameter);
        memory.AddRegion(0x2000, authParameter);
        memory.AddRegion(0x3000, new byte[1]);
        memory.AddRegion(0x4000, "scope\0"u8.ToArray());
        memory.AddRegion(0x5000, authCode);
        memory.AddRegion(0x6000, result);
        var context = new CpuContext(memory, Generation.Gen5);
        context[CpuRegister.Rdi] = 0x1000;
        var requestId = create.Function(context);

        context[CpuRegister.Rdi] = unchecked((ulong)requestId);
        context[CpuRegister.Rsi] = 0x2000;
        context[CpuRegister.Rdx] = 0x5000;
        context[CpuRegister.Rcx] = 0;
        Assert.Equal(0, authorize.Function(context));
        Assert.All(authCode, value => Assert.Equal(0xA5, value));

        context[CpuRegister.Rdi] = unchecked((ulong)requestId);
        context[CpuRegister.Rsi] = 0x6000;
        Assert.Equal(0, poll.Function(context));
        Assert.Equal(unchecked((int)0x80550006), BinaryPrimitives.ReadInt32LittleEndian(result));
        NpLifecycle.ResetRuntimeState();
    }
}
