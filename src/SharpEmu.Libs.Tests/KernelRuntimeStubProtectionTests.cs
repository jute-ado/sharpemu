// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE.Host;
using SharpEmu.Libs.Kernel;
using System.Runtime.InteropServices;
using Xunit;

namespace SharpEmu.Libs.Tests;

public sealed class KernelRuntimeStubProtectionTests
{
    [Fact]
    public unsafe void RdtscStubIsFinalizedFromWritableToExecutableMemory()
    {
        using var hostMemory = new RecordingHostMemory();

        var stubAddress = KernelRuntimeCompatExports.CreateRdtscStub(hostMemory);

        Assert.NotEqual(0, stubAddress);
        Assert.Equal(
            [(0uL, 16uL, HostPageProtection.ReadWrite)],
            hostMemory.AllocationRequests);
        Assert.Equal(
            [(unchecked((ulong)stubAddress), 16uL, HostPageProtection.ReadExecute)],
            hostMemory.ProtectionRequests);
        Assert.Equal(
            [(unchecked((ulong)stubAddress), 10uL)],
            hostMemory.FlushedRanges);
        Assert.Equal(
            [0x0F, 0x31, 0x48, 0xC1, 0xE2, 0x20, 0x48, 0x09, 0xD0, 0xC3],
            new ReadOnlySpan<byte>((void*)stubAddress, 10).ToArray());
        Assert.Empty(hostMemory.FreedAddresses);
    }

    [Fact]
    public unsafe void RdtscStubAllocationIsReleasedWhenExecutableProtectionFails()
    {
        using var hostMemory = new RecordingHostMemory
        {
            ProtectResult = false,
        };

        var stubAddress = KernelRuntimeCompatExports.CreateRdtscStub(hostMemory);

        Assert.Equal(0, stubAddress);
        Assert.Single(hostMemory.AllocationRequests);
        Assert.Single(hostMemory.ProtectionRequests);
        Assert.Empty(hostMemory.FlushedRanges);
        Assert.Single(hostMemory.FreedAddresses);
    }

    private sealed unsafe class RecordingHostMemory : IHostMemory, IDisposable
    {
        private readonly HashSet<ulong> _allocations = [];

        public bool ProtectResult { get; init; } = true;

        public List<(ulong Address, ulong Size, HostPageProtection Protection)> AllocationRequests { get; } = [];

        public List<(ulong Address, ulong Size, HostPageProtection Protection)> ProtectionRequests { get; } = [];

        public List<(ulong Address, ulong Size)> FlushedRanges { get; } = [];

        public List<ulong> FreedAddresses { get; } = [];

        public ulong Allocate(ulong desiredAddress, ulong size, HostPageProtection protection)
        {
            AllocationRequests.Add((desiredAddress, size, protection));
            var address = unchecked((ulong)NativeMemory.Alloc((nuint)size));
            _allocations.Add(address);
            return address;
        }

        public ulong Reserve(ulong desiredAddress, ulong size, HostPageProtection protection) => 0;

        public bool Commit(ulong address, ulong size, HostPageProtection protection) => false;

        public bool Free(ulong address)
        {
            FreedAddresses.Add(address);
            if (!_allocations.Remove(address))
            {
                return false;
            }

            NativeMemory.Free((void*)address);
            return true;
        }

        public bool Protect(
            ulong address,
            ulong size,
            HostPageProtection protection,
            out uint rawOldProtection)
        {
            ProtectionRequests.Add((address, size, protection));
            rawOldProtection = 0;
            return ProtectResult;
        }

        public bool ProtectRaw(
            ulong address,
            ulong size,
            uint rawProtection,
            out uint rawOldProtection)
        {
            rawOldProtection = 0;
            return false;
        }

        public bool Query(ulong address, out HostRegionInfo info)
        {
            info = default;
            return false;
        }

        public void FlushInstructionCache(ulong address, ulong size)
        {
            FlushedRanges.Add((address, size));
        }

        public void Dispose()
        {
            foreach (var address in _allocations)
            {
                NativeMemory.Free((void*)address);
            }

            _allocations.Clear();
        }
    }
}
