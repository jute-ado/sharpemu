// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using SharpEmu.Core.Cpu.Native;
using SharpEmu.Core.Loader;
using SharpEmu.Core.Memory;
using SharpEmu.Core.Runtime;
using SharpEmu.Nids;
using Xunit;

namespace SharpEmu.Core.Tests;

public sealed class DataSymbolSeparationTests
{
    [Fact]
    public void RuntimeObjectDefinition_IsNotCallableOrInstalledAsDlsymHook()
    {
        var callableSymbols = new Dictionary<string, ulong>(StringComparer.Ordinal);
        var dataSymbols = new Dictionary<string, ulong>(StringComparer.Ordinal);
        var importStubs = new Dictionary<ulong, string>();

        Assert.True(SelfLoader.RegisterRuntimeSymbol(
            callableSymbols,
            dataSymbols,
            importStubs,
            "kernel_dynlib_dlsym",
            0x2000_0000,
            isData: true));
        Assert.True(SelfLoader.RegisterRuntimeSymbol(
            callableSymbols,
            dataSymbols,
            importStubs,
            "function_definition",
            0x3000_0000,
            isData: false));

        Assert.DoesNotContain("kernel_dynlib_dlsym", callableSymbols.Keys);
        Assert.Equal(0x2000_0000UL, dataSymbols["kernel_dynlib_dlsym"]);
        Assert.Equal(0x3000_0000UL, callableSymbols["function_definition"]);
        Assert.DoesNotContain("function_definition", dataSymbols.Keys);
        Assert.Empty(importStubs);
    }

    [Fact]
    public void DynamicLookup_SeesDataWithoutMakingItCallable()
    {
        var callableSymbols = new Dictionary<string, ulong>(StringComparer.Ordinal)
        {
            ["function"] = 0x2000_0000,
        };
        var dataSymbols = new Dictionary<string, ulong>(StringComparer.Ordinal)
        {
            ["object"] = 0x3000_0000,
        };

        Assert.True(DirectExecutionBackend.TryResolveGlobalDlsymSymbolAddress(
            callableSymbols,
            dataSymbols,
            "object",
            out var address));
        Assert.Equal(0x3000_0000UL, address);
        Assert.False(DirectExecutionBackend.TryResolveCallableRuntimeSymbolAddress(
            callableSymbols,
            "object",
            out _));
    }

    [Fact]
    public void DynamicLookup_ResolvesDataByPlayStationNidAlias()
    {
        const string name = "__progname";
        var dataSymbols = new Dictionary<string, ulong>(StringComparer.Ordinal)
        {
            [Ps5Nid.Compute(name)] = 0x3000_0000,
        };

        Assert.True(DirectExecutionBackend.TryResolveGlobalDlsymSymbolAddress(
            new Dictionary<string, ulong>(),
            dataSymbols,
            name,
            out var address));
        Assert.Equal(0x3000_0000UL, address);
    }

    [Fact]
    public void ImportStubPolicy_ExcludesDataAndRejectsMixedSymbolKinds()
    {
        Assert.False(SelfLoader.EvaluateImportStubPolicy(
            "object", hasDataImport: true, hasFunctionImport: false));
        Assert.True(SelfLoader.EvaluateImportStubPolicy(
            "function", hasDataImport: false, hasFunctionImport: true));
        Assert.Throws<InvalidDataException>(() => SelfLoader.EvaluateImportStubPolicy(
            "ambiguous", hasDataImport: true, hasFunctionImport: true));
    }

    [Fact]
    public void ModuleDynamicLookup_UsesCallableAndDataDefinitions()
    {
        var image = new SelfImage(
            isSelf: false,
            elfHeader: default,
            programHeaders: Array.Empty<ProgramHeader>(),
            mappedRegions: Array.Empty<VirtualMemoryRegion>(),
            runtimeSymbols: new Dictionary<string, ulong>(StringComparer.Ordinal)
            {
                ["callable"] = 0x2000_0000,
            },
            runtimeDataSymbols: new Dictionary<string, ulong>(StringComparer.Ordinal)
            {
                ["object"] = 0x3000_0000,
            });

        var symbols = SharpEmuRuntime.CreateModuleDlsymSymbols(image);

        Assert.Equal(0x2000_0000UL, symbols["callable"]);
        Assert.Equal(0x3000_0000UL, symbols["object"]);
        Assert.DoesNotContain("object", image.RuntimeSymbols.Keys);
    }

    [Fact]
    public void WeakUndefinedDataRelocations_WriteZeroSymbolPlusSignedAddend()
    {
        var memory = CreateWritableMemory();
        var image = CreateImage(
            new ImportedSymbolRelocation(0x10020, 0x28, "weak-positive", IsData: true, IsWeak: true),
            new ImportedSymbolRelocation(0x10028, -0x18, "weak-negative", IsData: true, IsWeak: true));

        Assert.Equal(0, ImportedDataRebinder.Rebind(
            memory,
            image,
            "weak.prx",
            new Dictionary<string, ulong>()));
        Assert.Equal(0x28UL, ReadUInt64(memory, 0x10020));
        Assert.Equal(0xFFFF_FFFF_FFFF_FFE8UL, ReadUInt64(memory, 0x10028));
    }

    [Fact]
    public void ResolvedDataRelocations_ApplyPositiveAndNegativeAddends()
    {
        var memory = CreateWritableMemory();
        var image = CreateImage(
            new ImportedSymbolRelocation(0x10010, 0x28, "positive", IsData: true),
            new ImportedSymbolRelocation(0x10018, -0x18, "negative", IsData: true));
        var symbols = new Dictionary<string, ulong>(StringComparer.Ordinal)
        {
            ["positive"] = 0x3000_0000,
            ["negative"] = 0x4000_0000,
        };

        Assert.Equal(2, ImportedDataRebinder.Rebind(memory, image, "module.prx", symbols));
        Assert.Equal(0x3000_0028UL, ReadUInt64(memory, 0x10010));
        Assert.Equal(0x3FFF_FFE8UL, ReadUInt64(memory, 0x10018));
    }

    [Fact]
    public void StrongUndefinedDataRelocation_FailsClosed()
    {
        var memory = CreateWritableMemory();
        var image = CreateImage(
            new ImportedSymbolRelocation(0x10030, 0, "required-object", IsData: true));

        var exception = Assert.Throws<InvalidDataException>(() => ImportedDataRebinder.Rebind(
            memory,
            image,
            "module.prx",
            new Dictionary<string, ulong>()));

        Assert.Contains("required-object", exception.Message, StringComparison.Ordinal);
        Assert.Contains("module.prx", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void DataRelocationWriteFailure_FailsClosed()
    {
        var image = CreateImage(
            new ImportedSymbolRelocation(0x5000_0000, 0, "object", IsData: true));

        var exception = Assert.Throws<InvalidDataException>(() => ImportedDataRebinder.Rebind(
            CreateWritableMemory(),
            image,
            "module.prx",
            new Dictionary<string, ulong> { ["object"] = 0x3000_0000 }));

        Assert.Contains("Failed to write", exception.Message, StringComparison.Ordinal);
    }

    private static VirtualMemory CreateWritableMemory()
    {
        var memory = new VirtualMemory();
        memory.Map(
            0x10000,
            0x100,
            0,
            ReadOnlySpan<byte>.Empty,
            ProgramHeaderFlags.Read | ProgramHeaderFlags.Write);
        return memory;
    }

    private static SelfImage CreateImage(params ImportedSymbolRelocation[] relocations) =>
        new(
            isSelf: false,
            elfHeader: default,
            programHeaders: Array.Empty<ProgramHeader>(),
            mappedRegions: Array.Empty<VirtualMemoryRegion>(),
            importedRelocations: relocations);

    private static ulong ReadUInt64(VirtualMemory memory, ulong address)
    {
        Span<byte> bytes = stackalloc byte[sizeof(ulong)];
        Assert.True(memory.TryRead(address, bytes));
        return BinaryPrimitives.ReadUInt64LittleEndian(bytes);
    }
}
