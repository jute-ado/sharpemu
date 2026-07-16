// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.HLE;

public interface IModuleManager
{
    /// <summary>Registers pre-built exports (the compile-time generated registry).</summary>
    int RegisterExports(IReadOnlyList<ExportedFunction> exports);

    /// <summary>
    /// Completes registration. Managed HLE callbacks execute on the native worker's
    /// host stack, so freezing does not need to discover or pre-JIT handler assemblies.
    /// </summary>
    void Freeze();

    bool TryGetFunction(string nid, out Delegate function);

    bool TryGetExport(string nid, out ExportedFunction export);

    bool TryGetExportByName(string exportName, out ExportedFunction export);

    bool TryDispatch(string nid, CpuContext context, out OrbisGen2Result result);

    OrbisGen2Result Dispatch(string nid, CpuContext context);
}
