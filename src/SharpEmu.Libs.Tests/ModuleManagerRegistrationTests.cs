// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using Xunit;

namespace SharpEmu.Libs.Tests;

public sealed class ModuleManagerRegistrationTests
{
    [Fact]
    public void DuplicateNidsRejectTheWholeBatch()
    {
        var manager = new ModuleManager();
        var exports = new[]
        {
            CreateExport("Unique", "unique-nid"),
            CreateExport("FirstDuplicate", "duplicate-nid"),
            CreateExport("SecondDuplicate", "duplicate-nid"),
        };

        var exception = Assert.Throws<InvalidOperationException>(
            () => manager.RegisterExports(exports));

        Assert.Contains("duplicate-nid", exception.Message, StringComparison.Ordinal);
        Assert.Contains("FirstDuplicate", exception.Message, StringComparison.Ordinal);
        Assert.Contains("SecondDuplicate", exception.Message, StringComparison.Ordinal);
        Assert.False(manager.TryGetExport("unique-nid", out _));
        Assert.False(manager.TryGetExport("duplicate-nid", out _));
    }

    [Fact]
    public void NullExportRejectsTheWholeBatch()
    {
        var manager = new ModuleManager();
        ExportedFunction[] exports =
        [
            CreateExport("Valid", "valid-nid"),
            null!,
        ];

        Assert.Throws<ArgumentNullException>(() => manager.RegisterExports(exports));

        Assert.False(manager.TryGetExport("valid-nid", out _));
    }

    [Fact]
    public void ExistingNidConflictDoesNotCommitOtherExports()
    {
        var manager = new ModuleManager();
        Assert.Equal(
            1,
            manager.RegisterExports([CreateExport("Original", "shared-nid")]));

        var exception = Assert.Throws<InvalidOperationException>(
            () => manager.RegisterExports(
            [
                CreateExport("Unrelated", "unrelated-nid"),
                CreateExport("Conflict", "shared-nid"),
            ]));

        Assert.Contains("shared-nid", exception.Message, StringComparison.Ordinal);
        Assert.True(manager.TryGetExport("shared-nid", out var original));
        Assert.Equal("Original", original.Name);
        Assert.False(manager.TryGetExport("unrelated-nid", out _));
    }

    [Fact]
    public void RepeatedRegistrationIsRejectedWithoutMutation()
    {
        var manager = new ModuleManager();
        ExportedFunction[] exports = [CreateExport("Export", "export-nid")];

        Assert.Equal(1, manager.RegisterExports(exports));
        Assert.Throws<InvalidOperationException>(() => manager.RegisterExports(exports));
        Assert.True(manager.TryGetExport("export-nid", out var registered));
        Assert.Equal("Export", registered.Name);
    }

    private static ExportedFunction CreateExport(string name, string nid) =>
        new("libSynthetic", nid, name, Generation.Gen5, static _ => 0);
}
