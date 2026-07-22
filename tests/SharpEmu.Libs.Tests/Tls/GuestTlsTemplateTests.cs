// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using Xunit;

namespace SharpEmu.Libs.Tests.Tls;

public sealed class GuestTlsTemplateTests
{
    [Fact]
    public void SeedThreadBlockRejectsMissingStaticTlsMapping()
    {
        try
        {
            GuestTlsTemplate.Reset();
            const ulong threadPointer = 0x20_000;
            var context = new CpuContext(
                new FakeCpuMemory(threadPointer, 0x100),
                Generation.Gen5)
            {
                FsBase = threadPointer,
            };
            GuestTlsTemplate.RegisterModule(1, [0xA5], 4, 4);

            var exception = Assert.Throws<InvalidOperationException>(
                () => GuestTlsTemplate.SeedThreadBlock(context, threadPointer));

            Assert.Contains("static TLS module 1", exception.Message);
        }
        finally
        {
            GuestTlsTemplate.Reset();
        }
    }

    [Fact]
    public void ResolveAddressRequiresSeededThreadBlock()
    {
        try
        {
            GuestTlsTemplate.Reset();
            const ulong threadPointer = 0x20_000;
            var context = new CpuContext(
                new FakeCpuMemory(0x10_000, 0x20_000),
                Generation.Gen5)
            {
                FsBase = threadPointer,
            };
            var staticOffset = GuestTlsTemplate.RegisterModule(1, [0xA5], 4, 4);

            Assert.Equal(0UL, GuestTlsTemplate.ResolveAddress(context, 1, 0));

            GuestTlsTemplate.SeedThreadBlock(context, threadPointer);

            Assert.Equal(
                threadPointer - staticOffset,
                GuestTlsTemplate.ResolveAddress(context, 1, 0));
        }
        finally
        {
            GuestTlsTemplate.Reset();
        }
    }

    [Fact]
    public void StartupReservationAcceptsTlsSpansLargerThanOneHostPage()
    {
        try
        {
            GuestTlsTemplate.Reset();

            var staticOffset = GuestTlsTemplate.RegisterModule(
                moduleId: 1,
                initImage: new byte[0x20],
                memorySize: 0x1870,
                alignment: 0x10);

            Assert.Equal(0x1870UL, staticOffset);
            Assert.True(staticOffset <= GuestTlsTemplate.StartupStaticTlsReservation);
        }
        finally
        {
            GuestTlsTemplate.Reset();
        }
    }
}
