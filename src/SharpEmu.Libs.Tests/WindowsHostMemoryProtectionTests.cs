// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE.Host;
using SharpEmu.HLE.Host.Windows;
using Xunit;

namespace SharpEmu.Libs.Tests;

public sealed class WindowsHostMemoryProtectionTests
{
    [Fact]
    public void GuardModifierIsNeverReportedAsAccessible()
    {
        const uint pageReadWrite = 0x04;
        const uint pageGuard = 0x100;

        Assert.Equal(
            HostPageProtection.NoAccess,
            WindowsHostMemory.ToHostProtection(pageReadWrite | pageGuard));
    }

    [Fact]
    public void NonAccessModifierPreservesBaseProtection()
    {
        const uint pageReadWrite = 0x04;
        const uint pageNoCache = 0x200;

        Assert.Equal(
            HostPageProtection.ReadWrite,
            WindowsHostMemory.ToHostProtection(pageReadWrite | pageNoCache));
    }
}
