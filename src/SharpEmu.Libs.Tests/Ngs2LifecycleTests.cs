// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using SharpEmu.Libs.Ngs2;
using Xunit;

namespace SharpEmu.Libs.Tests;

public sealed class Ngs2LifecycleTests
{
    private const int InvalidOutAddress = unchecked((int)0x804A0053);
    private const int InvalidSystemHandle = unchecked((int)0x804A0230);
    private const int InvalidRackHandle = unchecked((int)0x804A0261);
    private const int InvalidVoiceHandle = unchecked((int)0x804A0300);
    private const ulong SystemOutAddress = 0x1000;
    private const ulong RackOutAddress = 0x2000;
    private const ulong VoiceOutAddress = 0x3000;

    [Fact]
    public void SystemDestructionInvalidatesOwnedRacksAndVoices()
    {
        var fixture = CreateFixture();
        try
        {
            var systemHandle = CreateSystem(fixture);
            var rackHandle = CreateRack(fixture, systemHandle);
            var voiceHandle = GetVoice(fixture, rackHandle, voiceIndex: 3);

            fixture.Context[CpuRegister.Rdi] = systemHandle;
            AssertCall(0, fixture.Context, Ngs2Exports.Ngs2SystemDestroy);
            fixture.SystemHandle = 0;

            fixture.Context[CpuRegister.Rdi] = rackHandle;
            AssertCall(
                InvalidRackHandle,
                fixture.Context,
                Ngs2Exports.Ngs2RackDestroy);

            fixture.Context[CpuRegister.Rdi] = voiceHandle;
            AssertCall(
                InvalidVoiceHandle,
                fixture.Context,
                Ngs2Exports.Ngs2VoiceControl);
        }
        finally
        {
            DestroySystemIfPresent(fixture);
        }
    }

    [Fact]
    public void RepeatedVoiceLookupReturnsStableHandle()
    {
        var fixture = CreateFixture();
        try
        {
            var systemHandle = CreateSystem(fixture);
            var rackHandle = CreateRack(fixture, systemHandle);
            var firstHandle = GetVoice(fixture, rackHandle, voiceIndex: 7);
            var secondHandle = GetVoice(fixture, rackHandle, voiceIndex: 7);

            Assert.Equal(firstHandle, secondHandle);
        }
        finally
        {
            DestroySystemIfPresent(fixture);
        }
    }

    [Fact]
    public void VoiceLookupRejectsNullOutputConsistentlyBeforeAndAfterCaching()
    {
        var fixture = CreateFixture();
        try
        {
            var systemHandle = CreateSystem(fixture);
            var rackHandle = CreateRack(fixture, systemHandle);

            fixture.Context[CpuRegister.Rdi] = rackHandle;
            fixture.Context[CpuRegister.Rsi] = 5;
            fixture.Context[CpuRegister.Rdx] = 0;
            AssertCall(
                InvalidOutAddress,
                fixture.Context,
                Ngs2Exports.Ngs2RackGetVoiceHandle);

            _ = GetVoice(fixture, rackHandle, voiceIndex: 5);

            fixture.Context[CpuRegister.Rdx] = 0;
            AssertCall(
                InvalidOutAddress,
                fixture.Context,
                Ngs2Exports.Ngs2RackGetVoiceHandle);
        }
        finally
        {
            DestroySystemIfPresent(fixture);
        }
    }

    [Fact]
    public void VoiceLookupValidatesRackBeforeOutputAddress()
    {
        var fixture = CreateFixture();
        fixture.Context[CpuRegister.Rdi] = 0;
        fixture.Context[CpuRegister.Rsi] = 0;
        fixture.Context[CpuRegister.Rdx] = 0;

        AssertCall(
            InvalidRackHandle,
            fixture.Context,
            Ngs2Exports.Ngs2RackGetVoiceHandle);
    }

    [Fact]
    public void RackCreationRejectsDestroyedSystem()
    {
        var fixture = CreateFixture();
        var systemHandle = CreateSystem(fixture);

        fixture.Context[CpuRegister.Rdi] = systemHandle;
        AssertCall(0, fixture.Context, Ngs2Exports.Ngs2SystemDestroy);
        fixture.SystemHandle = 0;

        fixture.Context[CpuRegister.Rdi] = systemHandle;
        fixture.Context[CpuRegister.Rsi] = 1;
        fixture.Context[CpuRegister.R8] = RackOutAddress;
        AssertCall(
            InvalidSystemHandle,
            fixture.Context,
            Ngs2Exports.Ngs2RackCreateWithAllocator);
    }

    [Fact]
    public void CoreLifecycleExportsHaveExactGeneratedMetadata()
    {
        var exports = SharpEmu.Generated.SysAbiExportRegistry
            .CreateExports(Generation.Gen5);

        AssertExport(
            exports,
            "mPYgU4oYpuY",
            "sceNgs2SystemCreateWithAllocator");
        AssertExport(
            exports,
            "U546k6orxQo",
            "sceNgs2RackCreateWithAllocator");
        AssertExport(
            exports,
            "MwmHz8pAdAo",
            "sceNgs2RackGetVoiceHandle");
    }

    private static Fixture CreateFixture()
    {
        var memory = new FakeGuestMemory();
        memory.AddRegion(SystemOutAddress, new byte[sizeof(ulong)]);
        memory.AddRegion(RackOutAddress, new byte[sizeof(ulong)]);
        memory.AddRegion(VoiceOutAddress, new byte[sizeof(ulong)]);
        return new Fixture(new CpuContext(memory, Generation.Gen5));
    }

    private static ulong CreateSystem(Fixture fixture)
    {
        fixture.Context[CpuRegister.Rdx] = SystemOutAddress;
        AssertCall(0, fixture.Context, Ngs2Exports.Ngs2SystemCreateWithAllocator);
        Assert.True(fixture.Context.TryReadUInt64(SystemOutAddress, out var handle));
        Assert.NotEqual(0UL, handle);
        fixture.SystemHandle = handle;
        return handle;
    }

    private static ulong CreateRack(Fixture fixture, ulong systemHandle)
    {
        fixture.Context[CpuRegister.Rdi] = systemHandle;
        fixture.Context[CpuRegister.Rsi] = 1;
        fixture.Context[CpuRegister.R8] = RackOutAddress;
        AssertCall(0, fixture.Context, Ngs2Exports.Ngs2RackCreateWithAllocator);
        Assert.True(fixture.Context.TryReadUInt64(RackOutAddress, out var handle));
        Assert.NotEqual(0UL, handle);
        return handle;
    }

    private static ulong GetVoice(Fixture fixture, ulong rackHandle, uint voiceIndex)
    {
        fixture.Context[CpuRegister.Rdi] = rackHandle;
        fixture.Context[CpuRegister.Rsi] = voiceIndex;
        fixture.Context[CpuRegister.Rdx] = VoiceOutAddress;
        AssertCall(0, fixture.Context, Ngs2Exports.Ngs2RackGetVoiceHandle);
        Assert.True(fixture.Context.TryReadUInt64(VoiceOutAddress, out var handle));
        Assert.NotEqual(0UL, handle);
        return handle;
    }

    private static void DestroySystemIfPresent(Fixture fixture)
    {
        if (fixture.SystemHandle == 0)
        {
            return;
        }

        fixture.Context[CpuRegister.Rdi] = fixture.SystemHandle;
        _ = Ngs2Exports.Ngs2SystemDestroy(fixture.Context);
        fixture.SystemHandle = 0;
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
        Assert.Equal("libSceNgs2", export.LibraryName);
    }

    private sealed record Fixture(CpuContext Context)
    {
        public ulong SystemHandle { get; set; }
    }
}
