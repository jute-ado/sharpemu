// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using SharpEmu.HLE;
using SharpEmu.Libs.Np;
using Xunit;

namespace SharpEmu.Libs.Tests;

[Collection("NP auth state")]
public sealed class NpManagerAsyncReachabilityTests : IDisposable
{
    private const string CreateAsyncNid = "eiqMCt9UshI";
    private const string CheckReachabilityNid = "KfGZg2y73oM";
    private const string AbortNid = "OzKvTvg3ZYU";
    private const string PollNid = "uqcPJLWL08M";
    private const ulong ParameterAddress = 0x1000;
    private const ulong ResultAddress = 0x2000;
    private const int ErrorNotInitialized = unchecked((int)0x80550002);
    private const int ErrorInvalidArgument = unchecked((int)0x80550003);
    private const int ErrorInvalidAsyncParameterSize = unchecked((int)0x80550011);
    private const int ErrorAborted = unchecked((int)0x80550012);
    private const int ErrorTooManyRequests = unchecked((int)0x80550013);
    private const int ErrorRequestNotFound = unchecked((int)0x80550014);

    private readonly FakeGuestMemory _memory = new();
    private readonly CpuContext _ctx;

    public NpManagerAsyncReachabilityTests()
    {
        NpLifecycle.ResetRuntimeState();
        _memory.AddRegion(ParameterAddress, new byte[0x18]);
        _memory.AddRegion(ResultAddress, new byte[sizeof(int)]);
        _ctx = new CpuContext(_memory, Generation.Gen5);
        WriteParameter(size: 0x18, affinity: 3, priority: 700);
    }

    public void Dispose()
    {
        NpLifecycle.ResetRuntimeState();
        GC.SuppressFinalize(this);
    }

    [Fact]
    public void Gen5ExportsAsyncRequestSurfaceWithExactMetadata()
    {
        var gen5 = SharpEmu.Generated.SysAbiExportRegistry.CreateExports(
            Generation.Gen5);
        var gen4 = SharpEmu.Generated.SysAbiExportRegistry.CreateExports(
            Generation.Gen4);

        AssertExport(gen5, CreateAsyncNid, "sceNpCreateAsyncRequest");
        AssertExport(gen5, CheckReachabilityNid, "sceNpCheckNpReachability");
        AssertExport(gen5, AbortNid, "sceNpAbortRequest");
        AssertExport(gen5, PollNid, "sceNpPollAsync");
        Assert.DoesNotContain(gen4, export => export.Nid == CreateAsyncNid);
        Assert.DoesNotContain(gen4, export => export.Nid == CheckReachabilityNid);
        Assert.DoesNotContain(gen4, export => export.Nid == AbortNid);
        Assert.DoesNotContain(gen4, export => export.Nid == PollNid);
    }

    [Fact]
    public void InitializationWinsOverInvalidArguments()
    {
        NpManagerRequestRegistry.ShutdownForTests();
        _ctx[CpuRegister.Rdi] = 0;
        _ctx[CpuRegister.Rsi] = unchecked((ulong)-1L);

        AssertResult(ErrorNotInitialized, NpManagerExports.NpCreateAsyncRequest);
        AssertResult(ErrorNotInitialized, NpManagerExports.NpCheckNpReachability);
        AssertResult(ErrorNotInitialized, NpManagerExports.NpAbortRequest);
        AssertResult(ErrorNotInitialized, NpManagerExports.NpDeleteRequest);
        AssertResult(ErrorNotInitialized, NpManagerExports.NpPollAsync);
    }

    [Fact]
    public void CreateAsyncValidatesParameterAndPreservesFields()
    {
        _ctx[CpuRegister.Rdi] = 0;
        AssertResult(ErrorInvalidArgument, NpManagerExports.NpCreateAsyncRequest);

        WriteParameter(size: 0x17, affinity: 3, priority: 700);
        _ctx[CpuRegister.Rdi] = ParameterAddress;
        AssertResult(
            ErrorInvalidAsyncParameterSize,
            NpManagerExports.NpCreateAsyncRequest);

        WriteParameter(size: 0x18, affinity: 0x8000_0000_0000_0001, priority: 321);
        var requestId = NpManagerExports.NpCreateAsyncRequest(_ctx);
        Assert.Equal(1, requestId);
        Assert.True(
            NpManagerRequestRegistry.TryGetSnapshotForTests(
                requestId,
                out var snapshot));
        Assert.True(snapshot.IsAsync);
        Assert.Equal(321u, snapshot.Priority);
        Assert.Equal(0x8000_0000_0000_0001UL, snapshot.Affinity);
        Assert.False(snapshot.OperationAssigned);
    }

    [Fact]
    public void ReachabilityCompletesCreatedRequestAndPollWritesResult()
    {
        var requestId = CreateAsyncRequest();

        _ctx[CpuRegister.Rdi] = unchecked((ulong)requestId);
        _ctx[CpuRegister.Rsi] = unchecked((ulong)-1L);
        AssertResult(ErrorInvalidArgument, NpManagerExports.NpCheckNpReachability);
        Assert.True(
            NpManagerRequestRegistry.TryGetSnapshotForTests(
                requestId,
                out var unstarted));
        Assert.False(unstarted.OperationAssigned);

        _ctx[CpuRegister.Rsi] = 0x1000_0000;
        AssertResult(0, NpManagerExports.NpCheckNpReachability);
        Assert.True(
            NpManagerRequestRegistry.WaitForCompletionForTests(
                requestId,
                TimeSpan.FromSeconds(2)));

        WriteResult(unchecked((int)0x7BAD_CAFE));
        _ctx[CpuRegister.Rdi] = unchecked((ulong)requestId);
        _ctx[CpuRegister.Rsi] = ResultAddress;
        AssertResult(0, NpManagerExports.NpPollAsync);
        Assert.Equal(0, ReadResult());

        _ctx[CpuRegister.Rsi] = 0x1000_0000;
        AssertResult(
            ErrorInvalidArgument,
            NpManagerExports.NpCheckNpReachability);
    }

    [Fact]
    public void AbortedRequestCompletesWithFirmwareAbortResult()
    {
        var requestId = CreateAsyncRequest();
        _ctx[CpuRegister.Rdi] = unchecked((ulong)requestId);
        AssertResult(0, NpManagerExports.NpAbortRequest);

        _ctx[CpuRegister.Rsi] = 0x1000_0000;
        AssertResult(0, NpManagerExports.NpCheckNpReachability);
        Assert.True(
            NpManagerRequestRegistry.WaitForCompletionForTests(
                requestId,
                TimeSpan.FromSeconds(2)));

        _ctx[CpuRegister.Rsi] = ResultAddress;
        AssertResult(0, NpManagerExports.NpPollAsync);
        Assert.Equal(ErrorAborted, ReadResult());
    }

    [Fact]
    public void PollLeavesOutputUntouchedWhileRequestIsPending()
    {
        var requestId = CreateAsyncRequest();
        WriteResult(unchecked((int)0x7BAD_CAFE));
        _ctx[CpuRegister.Rdi] = unchecked((ulong)requestId);
        _ctx[CpuRegister.Rsi] = ResultAddress;

        AssertResult(1, NpManagerExports.NpPollAsync);
        Assert.Equal(unchecked((int)0x7BAD_CAFE), ReadResult());
    }

    [Fact]
    public void SyncAndAsyncRequestsShareIdsWithoutColliding()
    {
        AssertResult(1, NpManagerExports.NpCreateRequest);
        Assert.Equal(2, CreateAsyncRequest());

        _ctx[CpuRegister.Rdi] = 2;
        AssertResult(0, NpManagerExports.NpDeleteRequest);
        _ctx[CpuRegister.Rdi] = 2;
        AssertResult(ErrorRequestNotFound, NpManagerExports.NpDeleteRequest);
        Assert.Equal(2, CreateAsyncRequest());

        _ctx[CpuRegister.Rdi] = 1;
        _ctx[CpuRegister.Rdx] = ResultAddress;
        AssertResult(
            unchecked((int)0x80550006),
            NpManagerExports.NpGetAccountAge);
    }

    [Fact]
    public void AsyncCapacityDoesNotEraseExistingSynchronousRequests()
    {
        AssertResult(1, NpManagerExports.NpCreateRequest);
        var asyncIds = Enumerable.Range(0, 32)
            .Select(_ => CreateAsyncRequest())
            .ToArray();

        Assert.Equal(Enumerable.Range(2, 32), asyncIds);
        AssertResult(ErrorTooManyRequests, NpManagerExports.NpCreateAsyncRequest);

        _ctx[CpuRegister.Rdi] = 1;
        _ctx[CpuRegister.Rdx] = ResultAddress;
        AssertResult(
            unchecked((int)0x80550006),
            NpManagerExports.NpGetAccountAge);
    }

    [Fact]
    public void RuntimeResetAbortsRequestsAndRestartsBothAllocators()
    {
        AssertResult(1, NpManagerExports.NpCreateRequest);
        Assert.Equal(2, CreateAsyncRequest());

        NpLifecycle.ResetRuntimeState();

        _ctx[CpuRegister.Rdi] = 1;
        AssertResult(ErrorRequestNotFound, NpManagerExports.NpDeleteRequest);
        AssertResult(1, NpManagerExports.NpCreateRequest);
        Assert.Equal(2, CreateAsyncRequest());
    }

    private int CreateAsyncRequest()
    {
        _ctx[CpuRegister.Rdi] = ParameterAddress;
        return NpManagerExports.NpCreateAsyncRequest(_ctx);
    }

    private void WriteParameter(ulong size, ulong affinity, uint priority)
    {
        Span<byte> parameter = stackalloc byte[0x18];
        BinaryPrimitives.WriteUInt64LittleEndian(parameter, size);
        BinaryPrimitives.WriteUInt64LittleEndian(parameter[0x08..], affinity);
        BinaryPrimitives.WriteUInt32LittleEndian(parameter[0x10..], priority);
        Assert.True(_memory.TryWrite(ParameterAddress, parameter));
    }

    private void WriteResult(int result)
    {
        Span<byte> bytes = stackalloc byte[sizeof(int)];
        BinaryPrimitives.WriteInt32LittleEndian(bytes, result);
        Assert.True(_memory.TryWrite(ResultAddress, bytes));
    }

    private int ReadResult()
    {
        Span<byte> bytes = stackalloc byte[sizeof(int)];
        Assert.True(_memory.TryRead(ResultAddress, bytes));
        return BinaryPrimitives.ReadInt32LittleEndian(bytes);
    }

    private void AssertResult(int expected, Func<CpuContext, int> export)
    {
        Assert.Equal(expected, export(_ctx));
        Assert.Equal(unchecked((ulong)expected), _ctx[CpuRegister.Rax]);
    }

    private static void AssertExport(
        IReadOnlyList<ExportedFunction> exports,
        string nid,
        string name)
    {
        var export = Assert.Single(exports, candidate => candidate.Nid == nid);
        Assert.Equal(name, export.Name);
        Assert.Equal("libSceNpManager", export.LibraryName);
        Assert.Equal(typeof(NpManagerExports), export.Function.Method.DeclaringType);
    }
}
