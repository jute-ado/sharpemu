// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Reflection;
using SharpEmu.HLE;
using SharpEmu.Libs.Kernel;
using SharpEmu.Testing;
using Xunit;

namespace SharpEmu.Libs.Tests;

public sealed class HleExportCatalogTests
{
    [Theory]
    [InlineData(Generation.Gen4)]
    [InlineData(Generation.Gen5)]
    [InlineData(Generation.Gen4 | Generation.Gen5)]
    public void ExportCatalogRegistersEveryApplicableExportExactlyOnce(Generation generation)
    {
        var assembly = typeof(KernelExports).Assembly;
        var applicableExports = GetExports(assembly)
            .Where(export => (export.Attribute.Target & generation) != 0)
            .ToArray();
        var reflected = ReflectionExportDiscovery.Discover(assembly, generation);
        var generated = SharpEmu.Generated.SysAbiExportRegistry.CreateExports(generation);

        Assert.Equal(applicableExports.Length, generated.Count);
        Assert.Equal(
            reflected.Select(export => export.Nid).Order(StringComparer.Ordinal),
            generated.Select(export => export.Nid).Order(StringComparer.Ordinal));
        foreach (var export in applicableExports)
        {
            var registeredExport = Assert.Single(
                generated,
                candidate => candidate.Nid == export.Attribute.Nid);
            Assert.Equal(export.Attribute.ExportName, registeredExport.Name);
            Assert.Equal(export.Attribute.LibraryName, registeredExport.LibraryName);
        }
    }

    [Fact]
    public void ExportCatalogHasValidHandlerSignaturesAndMetadata()
    {
        var exports = GetExports(typeof(KernelExports).Assembly);

        Assert.NotEmpty(exports);
        foreach (var export in exports)
        {
            Assert.False(string.IsNullOrWhiteSpace(export.Attribute.Nid), Describe(export));
            Assert.False(string.IsNullOrWhiteSpace(export.Attribute.ExportName), Describe(export));
            Assert.False(string.IsNullOrWhiteSpace(export.Attribute.LibraryName), Describe(export));
            Assert.NotEqual(Generation.None, export.Attribute.Target);
            Assert.Equal(typeof(int), export.Method.ReturnType);

            var parameters = export.Method.GetParameters();
            Assert.True(
                parameters.Length == 0 ||
                (parameters.Length == 1 && parameters[0].ParameterType == typeof(CpuContext)),
                $"{Describe(export)} has an unsupported handler signature.");
        }
    }

    private static ExportDefinition[] GetExports(Assembly assembly) =>
        assembly.GetTypes()
            .SelectMany(type => type.GetMethods(
                BindingFlags.Public |
                BindingFlags.NonPublic |
                BindingFlags.Instance |
                BindingFlags.Static))
            .Select(method => (
                Method: method,
                Attribute: method.GetCustomAttribute<SysAbiExportAttribute>(inherit: false)))
            .Where(export => export.Attribute is not null)
            .Select(export => new ExportDefinition(export.Method, export.Attribute!))
            .ToArray();

    private static string Describe(ExportDefinition export) =>
        $"{export.Method.DeclaringType?.FullName}.{export.Method.Name}";

    private sealed record ExportDefinition(MethodInfo Method, SysAbiExportAttribute Attribute);
}
