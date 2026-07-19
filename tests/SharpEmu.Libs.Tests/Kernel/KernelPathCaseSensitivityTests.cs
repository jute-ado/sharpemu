// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using SharpEmu.Libs.Kernel;
using Xunit;

namespace SharpEmu.Libs.Tests.Kernel;

// The negative-stat and apr-file-size caches memoize host filesystem probe
// results, so their key equivalence must match the host filesystem's. These
// tests pin the case-sensitive-host behavior: a cached miss for one casing
// must never shadow a differently-cased path whose probe would succeed, and
// mount containment must not accept a case-sibling escape. On case-insensitive
// hosts (Windows, default macOS volumes) the aliased paths genuinely are the
// same file, so the case-specific sections skip themselves.
[Collection(KernelMemoryCompatStateCollection.Name)]
public sealed class KernelPathCaseSensitivityTests : IDisposable
{
    private const ulong MemoryBase = 0x1_0000_0000;
    private const ulong PathAddress = MemoryBase + 0x100;
    private const ulong StatAddress = MemoryBase + 0x400;
    private const ulong PathListAddress = MemoryBase + 0x900;
    private const ulong PathBytesAddress = MemoryBase + 0xA00;
    private const ulong IdsAddress = MemoryBase + 0xB00;
    private const ulong SizesAddress = MemoryBase + 0xB40;

    private readonly string _tempRoot;
    private readonly string _guestMount;

    public KernelPathCaseSensitivityTests()
    {
        _tempRoot = Path.Combine(
            Path.GetTempPath(),
            $"sharpemu-kernel-case-{Guid.NewGuid():N}");
        _guestMount = $"/sharpemu_kernel_case_{Guid.NewGuid():N}";
        Directory.CreateDirectory(_tempRoot);
    }

    public void Dispose()
    {
        KernelMemoryCompatExports.TryUnregisterGuestPathMount(_guestMount);
        Directory.Delete(_tempRoot, recursive: true);
    }

    [Fact]
    public void Stat_CachedMissForOneCasingDoesNotShadowExistingFile()
    {
        var app0Root = Path.Combine(_tempRoot, "app0");
        var unique = $"case_{Guid.NewGuid():N}";
        Directory.CreateDirectory(Path.Combine(app0Root, unique));
        File.WriteAllBytes(Path.Combine(app0Root, unique, "Data.bin"), [1, 2, 3]);
        KernelMemoryCompatExports.RegisterGuestPathMount(_guestMount, app0Root);

        // Prime the negative-stat cache with the wrongly-cased sibling. On a
        // case-sensitive host the probe fails and the miss is cached; on a
        // case-insensitive host the probe finds Data.bin and nothing is cached.
        var missResult = PosixStat($"{_guestMount}/{unique}/DATA.BIN");
        if (HostFsIsCaseSensitive())
        {
            Assert.Equal(-1, missResult);
        }

        // The correctly-cased path exists; the cached miss above must not be
        // served for it.
        Assert.Equal(0, PosixStat($"{_guestMount}/{unique}/Data.bin"));
    }

    [Fact]
    public void AprResolve_DistinctlyCasedHostFilesReportTheirOwnSizes()
    {
        if (!HostFsIsCaseSensitive())
        {
            return;
        }

        var app0Root = Path.Combine(_tempRoot, "app0");
        var unique = $"apr_{Guid.NewGuid():N}";
        var assetDir = Path.Combine(app0Root, unique);
        Directory.CreateDirectory(assetDir);
        File.WriteAllBytes(Path.Combine(assetDir, "asset.bin"), new byte[3]);
        File.WriteAllBytes(Path.Combine(assetDir, "ASSET.BIN"), new byte[7]);
        KernelMemoryCompatExports.RegisterGuestPathMount(_guestMount, app0Root);

        // Whichever casing resolves first lands in the size cache; the other
        // casing is a different host file and must not inherit its size.
        Assert.Equal(3UL, AprResolveSize($"{_guestMount}/{unique}/asset.bin"));
        Assert.Equal(7UL, AprResolveSize($"{_guestMount}/{unique}/ASSET.BIN"));
    }

    [Fact]
    public void MountResolution_ClampsCaseSiblingTraversalInsideMount()
    {
        if (!HostFsIsCaseSensitive())
        {
            return;
        }

        var mountRoot = Path.Combine(_tempRoot, "Save");
        var sibling = Path.Combine(_tempRoot, "save");
        Directory.CreateDirectory(mountRoot);
        Directory.CreateDirectory(sibling);
        File.WriteAllBytes(Path.Combine(sibling, "secret.bin"), [1]);
        KernelMemoryCompatExports.RegisterGuestPathMount(_guestMount, mountRoot);

        // "../save/..." is clamped before host resolution. It must resolve to
        // a child of the mounted "Save" directory, never the case-different
        // sibling beside it.
        var resolved = KernelMemoryCompatExports.ResolveGuestPath(
            $"{_guestMount}/../save/secret.bin");
        Assert.Equal(
            Path.Combine(mountRoot, "save", "secret.bin"),
            resolved);
        Assert.False(File.Exists(resolved));
        Assert.NotEqual(Path.Combine(sibling, "secret.bin"), resolved);
    }

    private bool HostFsIsCaseSensitive()
    {
        var name = $"probe_{Guid.NewGuid():N}";
        var probe = Path.Combine(_tempRoot, name + ".tmp");
        File.WriteAllText(probe, string.Empty);
        return !File.Exists(Path.Combine(_tempRoot, name.ToUpperInvariant() + ".TMP"));
    }

    private static int PosixStat(string guestPath)
    {
        var memory = new FakeCpuMemory(MemoryBase, 0x1000);
        var context = new CpuContext(memory, Generation.Gen5);
        memory.WriteCString(PathAddress, guestPath);
        context[CpuRegister.Rdi] = PathAddress;
        context[CpuRegister.Rsi] = StatAddress;
        return KernelMemoryCompatExports.PosixStat(context);
    }

    private static ulong AprResolveSize(string guestPath)
    {
        var memory = new FakeCpuMemory(MemoryBase, 0x1000);
        var context = new CpuContext(memory, Generation.Gen5);
        memory.WriteCString(PathBytesAddress, guestPath);
        Span<byte> pointerBytes = stackalloc byte[sizeof(ulong)];
        BitConverter.TryWriteBytes(pointerBytes, PathBytesAddress);
        Assert.True(memory.TryWrite(PathListAddress, pointerBytes));
        context[CpuRegister.Rdi] = PathListAddress;
        context[CpuRegister.Rsi] = 1;
        context[CpuRegister.Rdx] = IdsAddress;
        context[CpuRegister.Rcx] = SizesAddress;

        var result = KernelMemoryCompatExports.KernelAprResolveFilepathsToIdsAndFileSizes(context);
        Assert.Equal(0, result);

        Span<byte> sizeBytes = stackalloc byte[sizeof(ulong)];
        Assert.True(memory.TryRead(SizesAddress, sizeBytes));
        return BitConverter.ToUInt64(sizeBytes);
    }
}
