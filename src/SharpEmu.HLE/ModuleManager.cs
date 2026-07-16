// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Collections.Concurrent;

namespace SharpEmu.HLE;

public sealed class ModuleManager : IModuleManager
{
    private readonly ConcurrentDictionary<string, Delegate> _dispatchTable = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, ExportedFunction> _exportTable = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, ExportedFunction> _exportNameTable = new(StringComparer.Ordinal);
    private readonly object _registrationGate = new();
    private bool _isFrozen;

    public int RegisterExports(IReadOnlyList<ExportedFunction> exports)
    {
        ArgumentNullException.ThrowIfNull(exports);
        var candidates = exports.ToArray();

        lock (_registrationGate)
        {
            if (_isFrozen)
            {
                throw new InvalidOperationException("Module registration is frozen.");
            }

            var candidatesByNid = new Dictionary<string, ExportedFunction>(StringComparer.Ordinal);
            foreach (var export in candidates)
            {
                ArgumentNullException.ThrowIfNull(export);

                if (_exportTable.TryGetValue(export.Nid, out var existing))
                {
                    throw new InvalidOperationException(
                        $"NID '{export.Nid}' ({export.Name}) conflicts with the already registered " +
                        $"export {existing.LibraryName}.{existing.Name}.");
                }

                if (!candidatesByNid.TryAdd(export.Nid, export))
                {
                    var first = candidatesByNid[export.Nid];
                    throw new InvalidOperationException(
                        $"NID '{export.Nid}' is declared by both " +
                        $"{first.LibraryName}.{first.Name} and {export.LibraryName}.{export.Name}.");
                }
            }

            foreach (var export in candidates)
            {
                _dispatchTable[export.Nid] = export.Function;
                _exportTable[export.Nid] = export;
                _exportNameTable.TryAdd(export.Name, export);
            }

            return candidates.Length;
        }
    }

    public void Freeze()
    {
        lock (_registrationGate)
        {
            _isFrozen = true;
        }
    }

    public bool TryGetFunction(string nid, out Delegate function)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(nid);
        return _dispatchTable.TryGetValue(nid, out function!);
    }

    public bool TryGetExport(string nid, out ExportedFunction export)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(nid);
        return _exportTable.TryGetValue(nid, out export!);
    }

    public bool TryGetExportByName(string exportName, out ExportedFunction export)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(exportName);
        return _exportNameTable.TryGetValue(exportName, out export!);
    }

    public OrbisGen2Result Dispatch(string nid, CpuContext context)
    {
        TryDispatch(nid, context, out var result);
        return result;
    }

    public bool TryDispatch(string nid, CpuContext context, out OrbisGen2Result result)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(nid);
        ArgumentNullException.ThrowIfNull(context);

        if (!_dispatchTable.TryGetValue(nid, out var function) || !_exportTable.TryGetValue(nid, out var export))
        {
            Console.Error.WriteLine($"[HLE] NID '{nid}' not found in dispatch table.");
            context[CpuRegister.Rax] = unchecked((ulong)(int)OrbisGen2Result.ORBIS_GEN2_ERROR_NOT_FOUND);
            result = OrbisGen2Result.ORBIS_GEN2_ERROR_NOT_FOUND;
            return false;
        }

        if ((export.Target & context.TargetGeneration) == 0)
        {
            Console.Error.WriteLine($"[HLE] NID '{nid}' ({export.Name}) found but not implemented for generation {context.TargetGeneration} (targets: {export.Target}).");
            context[CpuRegister.Rax] = unchecked((ulong)(int)OrbisGen2Result.ORBIS_GEN2_ERROR_NOT_IMPLEMENTED);
            result = OrbisGen2Result.ORBIS_GEN2_ERROR_NOT_IMPLEMENTED;
            return false;
        }


        context.ClearRaxWriteFlag();
        int ret = ((SysAbiFunction)function).Invoke(context);

        if (!context.WasRaxWritten)
        {
            context[CpuRegister.Rax] = unchecked((ulong)ret);
        }

        result = (OrbisGen2Result)ret;
        return true;
    }

}
