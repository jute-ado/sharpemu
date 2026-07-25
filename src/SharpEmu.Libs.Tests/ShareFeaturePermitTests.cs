// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Reflection;
using SharpEmu.HLE;
using SharpEmu.Libs.Share;
using Xunit;

namespace SharpEmu.Libs.Tests;

public sealed class ShareFeaturePermitTests
{
    private const int ShareErrorInvalidParameter = unchecked((int)0x8196_0002);

    [Theory]
    [InlineData(1u)]
    [InlineData(0x3Fu)]
    [InlineData(uint.MaxValue)]
    public void FeaturePermitAcceptsEveryNonzeroFeatureMask(uint featureFlags)
    {
        var context = CreateContext(featureFlags);

        Assert.Equal(0, ShareExports.ShareFeaturePermit(context));
        Assert.Equal(0UL, context[CpuRegister.Rax]);
    }

    [Fact]
    public void FeaturePermitRejectsEmptyFeatureMask()
    {
        var context = CreateContext(0);

        Assert.Equal(ShareErrorInvalidParameter, ShareExports.ShareFeaturePermit(context));
        Assert.Equal(unchecked((ulong)ShareErrorInvalidParameter), context[CpuRegister.Rax]);
    }

    [Fact]
    public void FeaturePermitHasGen5ShareLibraryMetadata()
    {
        var method = typeof(ShareExports).GetMethod(
            nameof(ShareExports.ShareFeaturePermit),
            BindingFlags.Public | BindingFlags.Static);
        var export = Assert.Single(method!.GetCustomAttributes<SysAbiExportAttribute>());

        Assert.Equal("YBiIdcDPrxs", export.Nid);
        Assert.Equal("sceShareFeaturePermit", export.ExportName);
        Assert.Equal(Generation.Gen5, export.Target);
        Assert.Equal("libSceShare", export.LibraryName);
    }

    private static CpuContext CreateContext(uint featureFlags)
    {
        var context = new CpuContext(new FakeGuestMemory(), Generation.Gen5);
        context[CpuRegister.Rdi] = featureFlags;
        return context;
    }
}
