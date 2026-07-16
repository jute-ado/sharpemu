// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Reflection;
using SharpEmu.HLE;

namespace SharpEmu.Testing;

/// <summary>
/// Reflection adapter for synthetic test assemblies. Production assemblies use
/// their compile-time generated export registry.
/// </summary>
public static class ReflectionExportDiscovery
{
    public static IReadOnlyList<ExportedFunction> Discover(
        Assembly assembly,
        Generation generation,
        ISymbolCatalog? symbolCatalog = null)
    {
        ArgumentNullException.ThrowIfNull(assembly);

        var instances = new Dictionary<Type, object>();
        var exports = new List<ExportedFunction>();
        var sourcesByNid = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var type in assembly.GetTypes().OrderBy(type => type.FullName, StringComparer.Ordinal))
        {
            foreach (var method in type.GetMethods(
                    BindingFlags.Public |
                    BindingFlags.NonPublic |
                    BindingFlags.Instance |
                    BindingFlags.Static)
                .OrderBy(method => method.MetadataToken))
            {
                var exportAttribute = method.GetCustomAttribute<SysAbiExportAttribute>(inherit: false);
                if (exportAttribute is null)
                {
                    continue;
                }

                var exportInfo = ResolveExportInfo(exportAttribute, method, generation, symbolCatalog);
                if (exportInfo is null)
                {
                    continue;
                }

                var source = $"{method.DeclaringType?.FullName}.{method.Name}";
                if (!sourcesByNid.TryAdd(exportInfo.Value.Nid, source))
                {
                    throw new InvalidOperationException(
                        $"NID '{exportInfo.Value.Nid}' is declared by both " +
                        $"{sourcesByNid[exportInfo.Value.Nid]} and {source}.");
                }

                exports.Add(new ExportedFunction(
                    exportInfo.Value.LibraryName,
                    exportInfo.Value.Nid,
                    exportInfo.Value.ExportName,
                    exportInfo.Value.Target,
                    CreateHandler(type, method, instances)));
            }
        }

        return exports;
    }

    private static SysAbiFunction CreateHandler(
        Type ownerType,
        MethodInfo method,
        IDictionary<Type, object> instances)
    {
        ValidateSignature(method);

        object? target = null;
        if (!method.IsStatic &&
            !instances.TryGetValue(ownerType, out target))
        {
            target = Activator.CreateInstance(ownerType)
                ?? throw new InvalidOperationException($"Cannot instantiate module type: {ownerType.FullName}");
            instances.Add(ownerType, target);
        }

        if (method.GetParameters().Length == 0)
        {
            var noArg = method.IsStatic
                ? (Func<int>)method.CreateDelegate(typeof(Func<int>))
                : (Func<int>)method.CreateDelegate(typeof(Func<int>), target!);
            return _ => noArg();
        }

        return method.IsStatic
            ? (SysAbiFunction)method.CreateDelegate(typeof(SysAbiFunction))
            : (SysAbiFunction)method.CreateDelegate(typeof(SysAbiFunction), target!);
    }

    private static void ValidateSignature(MethodInfo method)
    {
        if (method.ReturnType != typeof(int))
        {
            throw new InvalidOperationException(
                $"Method {method.DeclaringType?.FullName}.{method.Name} must return int.");
        }

        var parameters = method.GetParameters();
        if (parameters.Length == 0 ||
            parameters.Length == 1 && parameters[0].ParameterType == typeof(CpuContext))
        {
            return;
        }

        throw new InvalidOperationException(
            $"Method {method.DeclaringType?.FullName}.{method.Name} must accept no arguments " +
            $"or one {nameof(CpuContext)} argument.");
    }

    private static ExportInfo? ResolveExportInfo(
        SysAbiExportAttribute exportAttribute,
        MethodInfo method,
        Generation generation,
        ISymbolCatalog? symbolCatalog)
    {
        var target = exportAttribute.Target == Generation.None
            ? generation
            : exportAttribute.Target;
        if ((target & generation) == 0)
        {
            return null;
        }

        var nid = exportAttribute.Nid;
        var exportName = exportAttribute.ExportName;
        if (string.IsNullOrWhiteSpace(nid) &&
            !string.IsNullOrWhiteSpace(exportName) &&
            symbolCatalog?.TryGetByExportName(exportName, out var byName) == true)
        {
            nid = byName.Nid;
        }

        if (!string.IsNullOrWhiteSpace(nid) &&
            symbolCatalog?.TryGetByNid(nid, out var byNid) == true)
        {
            exportName = string.IsNullOrWhiteSpace(exportName) ? byNid.ExportName : exportName;
            target = exportAttribute.Target == Generation.None ? byNid.Target : target;
        }

        if (string.IsNullOrWhiteSpace(nid))
        {
            throw new InvalidOperationException(
                $"Method {method.DeclaringType?.FullName}.{method.Name} must define a NID " +
                "or match one in the symbols catalog.");
        }

        exportName = string.IsNullOrWhiteSpace(exportName) ? method.Name : exportName;
        if ((target & generation) == 0)
        {
            return null;
        }

        var libraryName = string.IsNullOrWhiteSpace(exportAttribute.LibraryName)
            ? "libKernel"
            : exportAttribute.LibraryName;
        return new ExportInfo(nid, exportName, libraryName, target);
    }

    private readonly record struct ExportInfo(
        string Nid,
        string ExportName,
        string LibraryName,
        Generation Target);
}
