// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using SharpEmu.Libs.Pad;
using Xunit;

namespace SharpEmu.Libs.Tests;

public sealed class BluetoothHidCompatibilityTests
{
    private const int BluetoothHidUnavailable = unchecked((int)0x80960001);

    [Fact]
    public void StubExportsSucceedWithoutHostBluetoothPassthrough()
    {
        WithUnavailableMode(null, () =>
        {
            var context = CreateContext();

            Assert.Equal(0, BluetoothHidExports.BluetoothHidInit(context));
            Assert.Equal(0, BluetoothHidExports.BluetoothHidRegisterCallback(context));
            Assert.Equal(0, BluetoothHidExports.BluetoothHidRegisterDevice(context));
            Assert.Equal(0UL, context[CpuRegister.Rax]);
        });
    }

    [Fact]
    public void ExplicitUnavailableModeIsAppliedConsistently()
    {
        WithUnavailableMode("1", () =>
        {
            var context = CreateContext();

            Assert.Equal(BluetoothHidUnavailable, BluetoothHidExports.BluetoothHidInit(context));
            Assert.Equal(BluetoothHidUnavailable, BluetoothHidExports.BluetoothHidRegisterCallback(context));
            Assert.Equal(BluetoothHidUnavailable, BluetoothHidExports.BluetoothHidRegisterDevice(context));
            Assert.Equal(unchecked((ulong)BluetoothHidUnavailable), context[CpuRegister.Rax]);
        });
    }

    private static CpuContext CreateContext() =>
        new(new FakeGuestMemory(), Generation.Gen5);

    private static void WithUnavailableMode(string? value, Action action)
    {
        var previous = Environment.GetEnvironmentVariable("SHARPEMU_BTHID_UNAVAILABLE");
        try
        {
            Environment.SetEnvironmentVariable("SHARPEMU_BTHID_UNAVAILABLE", value);
            action();
        }
        finally
        {
            Environment.SetEnvironmentVariable("SHARPEMU_BTHID_UNAVAILABLE", previous);
        }
    }
}
