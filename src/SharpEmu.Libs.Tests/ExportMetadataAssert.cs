// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using Xunit;

namespace SharpEmu.Libs.Tests;

internal static class ExportMetadataAssert
{
    public static void Exact(
        string nid,
        string name,
        string libraryName,
        Generation target)
    {
        var exports = SharpEmu.Generated.SysAbiExportRegistry.CreateExports(target);
        var export = Assert.Single(exports, candidate => candidate.Nid == nid);

        Assert.Equal(name, export.Name);
        Assert.Equal(libraryName, export.LibraryName);
        Assert.Equal(target, export.Target);
    }
}
