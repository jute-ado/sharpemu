// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using Xunit;

namespace SharpEmu.Libs.Tests;

public sealed class LibcSearchAndConversionRegistrationTests
{
    [Theory]
    [InlineData("NesIgTmfF0Q", "bsearch")]
    [InlineData("5OqszGpy7Mg", "strtoull")]
    public void Gen5LibcSearchAndConversionExportsAreRegistered(string nid, string name)
    {
        var manager = new ModuleManager();
        manager.RegisterExports(SharpEmu.Generated.SysAbiExportRegistry.CreateExports(Generation.Gen5));

        Assert.True(manager.TryGetExport(nid, out var export));
        Assert.Equal(name, export.Name);
        Assert.Equal("libc", export.LibraryName);
        Assert.Equal(Generation.Gen5, export.Target);
    }
}
