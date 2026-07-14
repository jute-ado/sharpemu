// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Reflection;
using System.Reflection.Emit;
using SharpEmu.HLE;
using Xunit;

namespace SharpEmu.Libs.Tests;

public sealed class ModuleManagerRegistrationTests
{
    [Fact]
    public void DuplicateNidsRejectTheWholeAssembly()
    {
        var assembly = CreateExportAssembly(
            new ExportSpec("Unique", "unique-nid"),
            new ExportSpec("FirstDuplicate", "duplicate-nid"),
            new ExportSpec("SecondDuplicate", "duplicate-nid"));
        var manager = new ModuleManager();

        var exception = Assert.Throws<InvalidOperationException>(
            () => manager.RegisterFromAssembly(assembly, Generation.Gen5));

        Assert.Contains("duplicate-nid", exception.Message, StringComparison.Ordinal);
        Assert.Contains("FirstDuplicate", exception.Message, StringComparison.Ordinal);
        Assert.Contains("SecondDuplicate", exception.Message, StringComparison.Ordinal);
        Assert.False(manager.TryGetExport("unique-nid", out _));
        Assert.False(manager.TryGetExport("duplicate-nid", out _));
    }

    [Fact]
    public void InvalidHandlerRejectsTheWholeAssemblyAndCanBeRetried()
    {
        var assembly = CreateExportAssembly(
            new ExportSpec("Valid", "valid-nid"),
            new ExportSpec("Invalid", "invalid-nid", typeof(string)));
        var manager = new ModuleManager();

        var first = Assert.Throws<InvalidOperationException>(
            () => manager.RegisterFromAssembly(assembly, Generation.Gen5));
        var second = Assert.Throws<InvalidOperationException>(
            () => manager.RegisterFromAssembly(assembly, Generation.Gen5));

        Assert.Contains("Invalid", first.Message, StringComparison.Ordinal);
        Assert.Equal(first.Message, second.Message);
        Assert.False(manager.TryGetExport("valid-nid", out _));
        Assert.False(manager.TryGetExport("invalid-nid", out _));
    }

    [Fact]
    public void ExistingNidConflictDoesNotCommitOtherExports()
    {
        var originalAssembly = CreateExportAssembly(new ExportSpec("Original", "shared-nid"));
        var conflictingAssembly = CreateExportAssembly(
            new ExportSpec("Unrelated", "unrelated-nid"),
            new ExportSpec("Conflict", "shared-nid"));
        var manager = new ModuleManager();
        Assert.Equal(1, manager.RegisterFromAssembly(originalAssembly, Generation.Gen5));

        var exception = Assert.Throws<InvalidOperationException>(
            () => manager.RegisterFromAssembly(conflictingAssembly, Generation.Gen5));

        Assert.Contains("shared-nid", exception.Message, StringComparison.Ordinal);
        Assert.True(manager.TryGetExport("shared-nid", out var original));
        Assert.Equal("Original", original.Name);
        Assert.False(manager.TryGetExport("unrelated-nid", out _));
    }

    [Fact]
    public void SuccessfulAssemblyIsRegisteredOnlyOnce()
    {
        var assembly = CreateExportAssembly(new ExportSpec("Export", "export-nid"));
        var manager = new ModuleManager();

        Assert.Equal(1, manager.RegisterFromAssembly(assembly, Generation.Gen5));
        Assert.Equal(0, manager.RegisterFromAssembly(assembly, Generation.Gen5));
    }

    private static Assembly CreateExportAssembly(params ExportSpec[] exports)
    {
        var assemblyName = new AssemblyName($"SharpEmu.DynamicExports.{Guid.NewGuid():N}");
        var assembly = AssemblyBuilder.DefineDynamicAssembly(assemblyName, AssemblyBuilderAccess.Run);
        var module = assembly.DefineDynamicModule(assemblyName.Name!);
        var type = module.DefineType(
            "SyntheticExports",
            TypeAttributes.Public | TypeAttributes.Abstract | TypeAttributes.Sealed);

        var attributeConstructor = typeof(SysAbiExportAttribute).GetConstructor(Type.EmptyTypes)!;
        var attributeProperties = new[]
        {
            typeof(SysAbiExportAttribute).GetProperty(nameof(SysAbiExportAttribute.Nid))!,
            typeof(SysAbiExportAttribute).GetProperty(nameof(SysAbiExportAttribute.ExportName))!,
            typeof(SysAbiExportAttribute).GetProperty(nameof(SysAbiExportAttribute.LibraryName))!,
            typeof(SysAbiExportAttribute).GetProperty(nameof(SysAbiExportAttribute.Target))!,
        };

        foreach (var export in exports)
        {
            var method = type.DefineMethod(
                export.MethodName,
                MethodAttributes.Public | MethodAttributes.Static,
                export.ReturnType,
                new[] { typeof(CpuContext) });
            var il = method.GetILGenerator();
            if (export.ReturnType == typeof(int))
            {
                il.Emit(OpCodes.Ldc_I4_0);
            }
            else
            {
                il.Emit(OpCodes.Ldstr, string.Empty);
            }

            il.Emit(OpCodes.Ret);
            method.SetCustomAttribute(new CustomAttributeBuilder(
                attributeConstructor,
                Array.Empty<object>(),
                attributeProperties,
                new object[] { export.Nid, export.MethodName, "libSynthetic", Generation.Gen5 }));
        }

        _ = type.CreateType();
        return assembly;
    }

    private sealed record ExportSpec(string MethodName, string Nid, Type? ResultType = null)
    {
        public Type ReturnType { get; } = ResultType ?? typeof(int);
    }
}
