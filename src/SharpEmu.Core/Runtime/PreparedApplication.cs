// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Core.Loader;
using SharpEmu.HLE;
using System.Collections.ObjectModel;

namespace SharpEmu.Core.Runtime;

public sealed class PreparedApplication
{
    internal PreparedApplication(
        SelfImage mainImage,
        IReadOnlyList<PreparedModule> modules,
        IReadOnlyList<SkippedModule> skippedModules,
        IReadOnlyList<ModuleLoadFailure> moduleLoadFailures,
        Generation generation,
        IReadOnlyDictionary<ulong, string> importStubs,
        IReadOnlyDictionary<string, ulong> runtimeSymbols,
        string processImageName)
    {
        MainImage = mainImage ?? throw new ArgumentNullException(nameof(mainImage));
        ArgumentNullException.ThrowIfNull(modules);
        ArgumentNullException.ThrowIfNull(skippedModules);
        ArgumentNullException.ThrowIfNull(moduleLoadFailures);
        ArgumentNullException.ThrowIfNull(importStubs);
        ArgumentNullException.ThrowIfNull(runtimeSymbols);

        Modules = modules.ToArray();
        SkippedModules = skippedModules.ToArray();
        ModuleLoadFailures = moduleLoadFailures.ToArray();
        Generation = generation;
        ImportStubs = new ReadOnlyDictionary<ulong, string>(new Dictionary<ulong, string>(importStubs));
        RuntimeSymbols = new ReadOnlyDictionary<string, ulong>(
            new Dictionary<string, ulong>(runtimeSymbols, StringComparer.Ordinal));
        ProcessImageName = processImageName ?? throw new ArgumentNullException(nameof(processImageName));
    }

    public SelfImage MainImage { get; }

    public IReadOnlyList<PreparedModule> Modules { get; }

    public IReadOnlyList<SkippedModule> SkippedModules { get; }

    public IReadOnlyList<ModuleLoadFailure> ModuleLoadFailures { get; }

    internal Generation Generation { get; }

    internal IReadOnlyDictionary<ulong, string> ImportStubs { get; }

    internal IReadOnlyDictionary<string, ulong> RuntimeSymbols { get; }

    internal string ProcessImageName { get; }
}

public sealed record PreparedModule(string Path, SelfImage Image);

public sealed record SkippedModule(string Path, string Reason);

public sealed record ModuleLoadFailure(string Path, string ErrorType, string Message);
