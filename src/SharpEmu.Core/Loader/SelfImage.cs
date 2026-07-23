// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Core.Memory;

namespace SharpEmu.Core.Loader;

public sealed class SelfImage
{
    private readonly ulong _imageBase;

    public SelfImage(
        bool isSelf,
        ElfHeader elfHeader,
        IReadOnlyList<ProgramHeader> programHeaders,
        IReadOnlyList<VirtualMemoryRegion> mappedRegions,
        IReadOnlyDictionary<ulong, string>? importStubs = null,
        IReadOnlyDictionary<string, ulong>? runtimeSymbols = null,
        IReadOnlyList<ImportedSymbolRelocation>? importedRelocations = null,
        IReadOnlyList<ulong>? preInitializerFunctions = null,
        IReadOnlyList<ulong>? initializerFunctions = null,
        ulong initFunctionEntryPoint = 0,
        ulong imageBase = 0,
        ulong procParamAddress = 0,
        string? title = null,
        string? titleId = null,
        string? version = null,
        string? contentId = null,
        IReadOnlyList<uint>? unsupportedRelocationTypes = null,
        ulong ehFrameHeaderAddress = 0,
        ulong ehFrameAddress = 0,
        ulong ehFrameSize = 0,
        IReadOnlyDictionary<string, ulong>? runtimeDataSymbols = null,
        ulong? flexibleMemorySize = null)
    {
        ArgumentNullException.ThrowIfNull(programHeaders);
        ArgumentNullException.ThrowIfNull(mappedRegions);

        IsSelf = isSelf;
        ElfHeader = elfHeader;
        ProgramHeaders = programHeaders;
        MappedRegions = mappedRegions;
        ImportStubs = importStubs ?? new Dictionary<ulong, string>();
        RuntimeSymbols = runtimeSymbols ?? new Dictionary<string, ulong>(StringComparer.Ordinal);
        RuntimeDataSymbols = runtimeDataSymbols ?? new Dictionary<string, ulong>(StringComparer.Ordinal);
        ImportedRelocations = importedRelocations ?? Array.Empty<ImportedSymbolRelocation>();
        PreInitializerFunctions = preInitializerFunctions ?? Array.Empty<ulong>();
        InitializerFunctions = initializerFunctions ?? Array.Empty<ulong>();
        InitFunctionEntryPoint = initFunctionEntryPoint;
        _imageBase = imageBase;
        ProcParamAddress = procParamAddress;
        Title = title;
        TitleId = titleId;
        Version = version;
        ContentId = contentId;
        FlexibleMemorySize = flexibleMemorySize;
        EhFrameHeaderAddress = ehFrameHeaderAddress;
        EhFrameAddress = ehFrameAddress;
        EhFrameSize = ehFrameSize;
        if (unsupportedRelocationTypes is null || unsupportedRelocationTypes.Count == 0)
        {
            UnsupportedRelocationTypes = Array.Empty<uint>();
        }
        else
        {
            var relocationTypes = new uint[unsupportedRelocationTypes.Count];
            for (var index = 0; index < relocationTypes.Length; index++)
            {
                relocationTypes[index] = unsupportedRelocationTypes[index];
            }
            UnsupportedRelocationTypes = Array.AsReadOnly(relocationTypes);
        }
    }

    public bool IsSelf { get; }

    public ElfHeader ElfHeader { get; }

    public IReadOnlyList<ProgramHeader> ProgramHeaders { get; }

    public IReadOnlyList<VirtualMemoryRegion> MappedRegions { get; }

    public IReadOnlyDictionary<ulong, string> ImportStubs { get; }

    public IReadOnlyDictionary<string, ulong> RuntimeSymbols { get; }

    /// <summary>Defined object symbols, kept separate from callable resolution.</summary>
    public IReadOnlyDictionary<string, ulong> RuntimeDataSymbols { get; }

    public IReadOnlyList<ImportedSymbolRelocation> ImportedRelocations { get; }

    public IReadOnlyList<ulong> PreInitializerFunctions { get; }

    public IReadOnlyList<ulong> InitializerFunctions { get; }

    public ulong InitFunctionEntryPoint { get; }

    public ulong EntryPoint => ElfHeader.EntryPoint + _imageBase;

    public ulong ImageBase => _imageBase;

    public ulong ProcParamAddress { get; }

    public string? Title { get; }

    public string? TitleId { get; }

    public string? Version { get; }

    public string? ContentId { get; }

    public ulong? FlexibleMemorySize { get; }

    public ulong EhFrameHeaderAddress { get; }

    public ulong EhFrameAddress { get; }

    public ulong EhFrameSize { get; }

    public IReadOnlyList<uint> UnsupportedRelocationTypes { get; }
}
