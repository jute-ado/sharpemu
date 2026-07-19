// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Core.Cpu.Native;
using SharpEmu.HLE;
using System.Runtime.InteropServices;
using Xunit;

namespace SharpEmu.Libs.Tests.Cpu;

public sealed class MemcpyHleRoutingTests
{
    private const string MemcpyNid = "Q3VBxCXhUHs";
    private const string MemsetNid = "QrZZdJ8XsX0";
    private const string RdtscNid = "-2IRUCO--PM";

    [Fact]
    public void IsHlePreferredNid_PrefersHleForMemcpy_OnEveryPlatform()
    {
        Assert.True(
            DirectExecutionBackend.IsHlePreferredNid(MemcpyNid),
            $"memcpy ({MemcpyNid}) must route through HLE on every platform. It was previously " +
            "gated behind OperatingSystem.IsWindows(), which left Linux and macOS on the LLE " +
            "intrinsic stub and faulted in guest code. Do not reintroduce an OS condition here.");
    }

    [Fact]
    public void IsHlePreferredNid_PrefersHleForMemset()
    {
        Assert.True(
            DirectExecutionBackend.IsHlePreferredNid(MemsetNid),
            $"memset ({MemsetNid}) must route through HLE on every platform.");
    }

    [Fact]
    public void TryCreateNativeImportIntrinsic_DoesNotClaimMemcpy()
    {
        if (RuntimeInformation.ProcessArchitecture != Architecture.X64)
        {
            return;
        }

        using var backend = new DirectExecutionBackend(new ModuleManager());
        var claimed = backend.TryCreateNativeImportIntrinsic(MemcpyNid, out var address);

        Assert.False(
            claimed,
            $"memcpy ({MemcpyNid}) must fall through to the HLE trampoline. SetupImportStubs tries " +
            "the intrinsic stub before the trampoline, so without an IsHlePreferredNid guard here " +
            "the intrinsic claims memcpy and the HLE routing never takes effect.");
        Assert.Equal(0, address);
    }

    [Fact]
    public void TryCreateNativeImportIntrinsic_StillClaimsNonHleNids()
    {
        if (RuntimeInformation.ProcessArchitecture != Architecture.X64)
        {
            return;
        }

        using var backend = new DirectExecutionBackend(new ModuleManager());
        var claimed = backend.TryCreateNativeImportIntrinsic(RdtscNid, out var address);

        Assert.True(
            claimed,
            $"rdtsc ({RdtscNid}) has no HLE handler and must still receive an intrinsic stub. If " +
            "this fails the memcpy assertions above may be passing vacuously.");
        Assert.NotEqual(0, address);
    }
}
