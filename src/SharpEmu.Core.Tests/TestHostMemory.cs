// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Runtime.InteropServices;
using SharpEmu.Core.Memory;
using SharpEmu.HLE.Host;
using SharpEmu.HLE.Host.Posix;

namespace SharpEmu.Core.Tests;

internal static class TestHostMemory
{
    public static IHostMemory Create()
    {
        if (RuntimeInformation.ProcessArchitecture == Architecture.X64)
        {
            return HostPlatform.Current.Memory;
        }

        if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
        {
            // Native guest execution is x64-only, but hosted macOS tests run as
            // arm64. Apple Silicon enforces write-xor-execute for that process,
            // so retain the real POSIX allocation/query/protection backend while
            // removing only the execute bit that these contract tests cannot use.
            return new ExecuteNeutralHostMemory(new PosixHostMemory());
        }

        return HostPlatform.Current.Memory;
    }

    public static PhysicalVirtualMemory CreatePhysicalMemory() => new(Create());

    private sealed class ExecuteNeutralHostMemory(IHostMemory inner) : IHostMemory
    {
        public ulong Allocate(ulong desiredAddress, ulong size, HostPageProtection protection) =>
            inner.Allocate(desiredAddress, size, RemoveExecute(protection));

        public ulong Reserve(ulong desiredAddress, ulong size, HostPageProtection protection) =>
            inner.Reserve(desiredAddress, size, RemoveExecute(protection));

        public bool Commit(ulong address, ulong size, HostPageProtection protection) =>
            inner.Commit(address, size, RemoveExecute(protection));

        public bool Free(ulong address) => inner.Free(address);

        public bool Protect(
            ulong address,
            ulong size,
            HostPageProtection protection,
            out uint rawOldProtection) =>
            inner.Protect(address, size, RemoveExecute(protection), out rawOldProtection);

        public bool ProtectRaw(
            ulong address,
            ulong size,
            uint rawProtection,
            out uint rawOldProtection) =>
            inner.ProtectRaw(address, size, rawProtection, out rawOldProtection);

        public bool Query(ulong address, out HostRegionInfo info) => inner.Query(address, out info);

        public void FlushInstructionCache(ulong address, ulong size) =>
            inner.FlushInstructionCache(address, size);

        private static HostPageProtection RemoveExecute(HostPageProtection protection) => protection switch
        {
            HostPageProtection.Execute => HostPageProtection.NoAccess,
            HostPageProtection.ReadExecute => HostPageProtection.ReadOnly,
            HostPageProtection.ReadWriteExecute => HostPageProtection.ReadWrite,
            HostPageProtection.ExecuteWriteCopy => HostPageProtection.ReadWrite,
            _ => protection,
        };
    }
}
