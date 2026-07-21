// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using System.Text;
using SharpEmu.HLE;
using SharpEmu.Libs.Kernel;
using Xunit;

namespace SharpEmu.Libs.Tests;

public sealed class KernelFilesystemContractTests : IDisposable
{
    private const ulong MemoryBase = 0x1_0000_0000;
    private const ulong PathAddress = MemoryBase + 0x100;
    private const ulong StatAddress = MemoryBase + 0x1000;
    private const int KernelStatSize = 120;
    private const int OpenReadWriteCreate = 0x0202;
    private const int OpenDirectory = 0x0002_0000;
    private const int InvalidArgument =
        (int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT;
    private const int PermissionDenied =
        (int)OrbisGen2Result.ORBIS_GEN2_ERROR_PERMISSION_DENIED;

    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "SharpEmu.Tests",
        $"filesystem-{Guid.NewGuid():N}");
    private readonly string _mount = $"/filesystem-{Guid.NewGuid():N}";
    private readonly string _app0Root;
    private readonly string? _originalApp0Root;
    private readonly FakeGuestMemory _memory = new();
    private readonly CpuContext _context;

    public KernelFilesystemContractTests()
    {
        Directory.CreateDirectory(_root);
        _originalApp0Root =
            Environment.GetEnvironmentVariable("SHARPEMU_APP0_DIR");
        _app0Root = Path.Combine(_root, "app0");
        Directory.CreateDirectory(_app0Root);
        Environment.SetEnvironmentVariable("SHARPEMU_APP0_DIR", _app0Root);
        KernelMemoryCompatExports.RegisterGuestPathMount(_mount, _root);
        _memory.AddRegion(MemoryBase, new byte[0x4000]);
        _context = new CpuContext(_memory, Generation.Gen5);
    }

    [Fact]
    public void WritableMountSupportsDirectoryFileAndStatLifecycle()
    {
        var directoryPath = _mount + "/cache";
        WritePath(directoryPath);
        Assert.Equal(0, KernelMemoryCompatExports.KernelMkdir(_context));
        Assert.True(Directory.Exists(Path.Combine(_root, "cache")));

        var filePath = directoryPath + "/data.bin";
        WritePath(filePath);
        _context[CpuRegister.Rsi] = OpenReadWriteCreate;
        Assert.Equal(0, KernelExports.KernelOpen(_context));
        var fd = unchecked((int)_context[CpuRegister.Rax]);
        Assert.True(fd >= 3);
        Assert.True(File.Exists(Path.Combine(_root, "cache", "data.bin")));

        _context[CpuRegister.Rdi] = unchecked((ulong)fd);
        Assert.Equal(0, KernelMemoryCompatExports.KernelClose(_context));

        WritePath(filePath);
        _context[CpuRegister.Rsi] = StatAddress;
        Assert.Equal(0, KernelMemoryCompatExports.KernelStat(_context));

        WritePath(filePath);
        Assert.Equal(0, KernelMemoryCompatExports.KernelUnlink(_context));
        Assert.False(File.Exists(Path.Combine(_root, "cache", "data.bin")));

        WritePath(directoryPath);
        Assert.Equal(0, KernelMemoryCompatExports.KernelRmdir(_context));
        Assert.False(Directory.Exists(Path.Combine(_root, "cache")));
    }

    [Fact]
    public void App0MutationsRemainDenied()
    {
        File.WriteAllText(Path.Combine(_app0Root, "existing.bin"), "fixture");
        WritePath("/app0/existing.bin");
        Assert.Equal(0, KernelExports.KernelOpen(_context));
        var fd = unchecked((int)_context[CpuRegister.Rax]);
        Assert.True(fd >= 3);
        _context[CpuRegister.Rdi] = unchecked((ulong)fd);
        Assert.Equal(0, KernelMemoryCompatExports.KernelClose(_context));

        WritePath("/app0/generated");
        Assert.Equal(
            PermissionDenied,
            KernelMemoryCompatExports.KernelMkdir(_context));

        WritePath("/app0/generated.bin");
        _context[CpuRegister.Rsi] = OpenReadWriteCreate;
        Assert.Equal(PermissionDenied, KernelExports.KernelOpen(_context));
    }

    [Fact]
    public void TraversalIsClampedBeforeMutatingRegisteredMount()
    {
        WritePath(_mount + "/../escape");
        Assert.Equal(0, KernelMemoryCompatExports.KernelMkdir(_context));
        Assert.True(Directory.Exists(Path.Combine(_root, "escape")));
        Assert.False(Directory.Exists(Path.Combine(_root, "..", "escape")));
    }

    [Fact]
    public void App0ParentEnumeratesGuestMountRootWithoutHostTraversal()
    {
        Directory.CreateDirectory(Path.Combine(_root, "host-only-secret"));
        WritePath("/app0/..");
        _context[CpuRegister.Rsi] = OpenDirectory;
        Assert.Equal(0, KernelExports.KernelOpen(_context));
        var fd = unchecked((int)_context[CpuRegister.Rax]);
        Assert.True(fd >= 3);

        var entries = new List<string>();
        while (true)
        {
            _context[CpuRegister.Rdi] = unchecked((ulong)fd);
            _context[CpuRegister.Rsi] = StatAddress;
            _context[CpuRegister.Rdx] = 512;
            Assert.Equal(0, KernelMemoryCompatExports.KernelGetdents(_context));
            var byteCount = unchecked((int)_context[CpuRegister.Rax]);
            Assert.True(byteCount is 0 or 512);
            if (byteCount == 0)
            {
                break;
            }

            Assert.True(_context.TryReadByte(StatAddress + 6, out var entryType));
            Assert.Equal(4, entryType);
            Assert.True(_context.TryReadByte(StatAddress + 7, out var nameLength));
            var nameBytes = new byte[nameLength];
            Assert.True(_context.Memory.TryRead(StatAddress + 8, nameBytes));
            entries.Add(Encoding.UTF8.GetString(nameBytes));
        }

        Assert.Contains("app0", entries);
        Assert.Contains(_mount.TrimStart('/'), entries);
        Assert.DoesNotContain("host-only-secret", entries);

        _context[CpuRegister.Rdi] = unchecked((ulong)fd);
        Assert.Equal(0, KernelMemoryCompatExports.KernelClose(_context));

        WritePath("/app0/../../escape");
        _context[CpuRegister.Rsi] = OpenDirectory;
        Assert.Equal(
            (int)OrbisGen2Result.ORBIS_GEN2_ERROR_NOT_FOUND,
            KernelExports.KernelOpen(_context));
    }

    [Fact]
    public void GuestMountRootSupportsConsistentStatAndFstat()
    {
        WritePath("/app0/..");
        _context[CpuRegister.Rsi] = StatAddress;
        Assert.Equal(0, KernelMemoryCompatExports.KernelStat(_context));

        var pathStat = new byte[KernelStatSize];
        Assert.True(_context.Memory.TryRead(StatAddress, pathStat));
        Assert.Equal(
            0x41FF,
            BinaryPrimitives.ReadUInt16LittleEndian(pathStat.AsSpan(8)));

        WritePath("/app0/..");
        _context[CpuRegister.Rsi] = OpenDirectory;
        Assert.Equal(0, KernelExports.KernelOpen(_context));
        var fd = unchecked((int)_context[CpuRegister.Rax]);

        _context[CpuRegister.Rdi] = unchecked((ulong)fd);
        _context[CpuRegister.Rsi] = StatAddress + 0x100;
        Assert.Equal(0, KernelMemoryCompatExports.KernelFstat(_context));

        var descriptorStat = new byte[KernelStatSize];
        Assert.True(
            _context.Memory.TryRead(StatAddress + 0x100, descriptorStat));
        Assert.Equal(pathStat, descriptorStat);

        _context[CpuRegister.Rdi] = unchecked((ulong)fd);
        Assert.Equal(0, KernelMemoryCompatExports.KernelClose(_context));
    }

    [Fact]
    public void DevlogContainerSupportsStatDirectoryEnumerationAndFstat()
    {
        WritePath("/devlog");
        _context[CpuRegister.Rsi] = StatAddress;
        Assert.Equal(0, KernelMemoryCompatExports.KernelStat(_context));

        var pathStat = new byte[KernelStatSize];
        Assert.True(_context.Memory.TryRead(StatAddress, pathStat));
        Assert.Equal(0x41FF, BinaryPrimitives.ReadUInt16LittleEndian(pathStat.AsSpan(8)));

        WritePath("/devlog");
        _context[CpuRegister.Rsi] = OpenDirectory;
        Assert.Equal(0, KernelExports.KernelOpen(_context));
        var fd = unchecked((int)_context[CpuRegister.Rax]);

        _context[CpuRegister.Rdi] = unchecked((ulong)fd);
        _context[CpuRegister.Rsi] = StatAddress + 0x100;
        Assert.Equal(0, KernelMemoryCompatExports.KernelFstat(_context));
        var descriptorStat = new byte[KernelStatSize];
        Assert.True(_context.Memory.TryRead(StatAddress + 0x100, descriptorStat));
        Assert.Equal(pathStat, descriptorStat);

        _context[CpuRegister.Rdi] = unchecked((ulong)fd);
        _context[CpuRegister.Rsi] = StatAddress;
        _context[CpuRegister.Rdx] = 512;
        Assert.Equal(0, KernelMemoryCompatExports.KernelGetdents(_context));
        Assert.Equal(512, unchecked((int)_context[CpuRegister.Rax]));
        Assert.True(_context.TryReadByte(StatAddress + 7, out var nameLength));
        var nameBytes = new byte[nameLength];
        Assert.True(_context.Memory.TryRead(StatAddress + 8, nameBytes));
        Assert.Equal("app", Encoding.UTF8.GetString(nameBytes));

        _context[CpuRegister.Rdi] = unchecked((ulong)fd);
        Assert.Equal(0, KernelMemoryCompatExports.KernelClose(_context));
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(
            "SHARPEMU_APP0_DIR",
            _originalApp0Root);
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private void WritePath(string path)
    {
        var bytes = Encoding.UTF8.GetBytes(path + '\0');
        Assert.True(_memory.TryWrite(PathAddress, bytes));
        _context[CpuRegister.Rdi] = PathAddress;
        _context[CpuRegister.Rsi] = 0;
        _context[CpuRegister.Rdx] = 0;
    }
}
