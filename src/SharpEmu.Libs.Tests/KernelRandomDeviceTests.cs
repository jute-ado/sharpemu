// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using System.Text;
using SharpEmu.HLE;
using SharpEmu.Libs.Kernel;
using Xunit;

namespace SharpEmu.Libs.Tests;

public sealed class KernelRandomDeviceTests
{
    private const ulong MemoryBase = 0x1_0000_0000;
    private const ulong PathAddress = MemoryBase + 0x100;
    private const ulong BufferAddress = MemoryBase + 0x400;
    private const ulong StatAddress = MemoryBase + 0x800;
    private const int KernelStatSize = 120;
    private const ushort KernelStatModeRegular = 0x81FF;

    [Theory]
    [InlineData("/dev/random")]
    [InlineData("/dev/urandom")]
    public void RandomDeviceSupportsOpenReadFstatCloseLifecycle(string path)
    {
        var memory = new FakeGuestMemory();
        memory.AddRegion(MemoryBase, new byte[0x2000]);
        var context = new CpuContext(memory, Generation.Gen5);
        Assert.True(memory.TryWrite(PathAddress, Encoding.UTF8.GetBytes(path + '\0')));
        context[CpuRegister.Rdi] = PathAddress;
        context[CpuRegister.Rsi] = StatAddress;

        Assert.Equal(0, KernelMemoryCompatExports.KernelStat(context));
        var pathStat = new byte[KernelStatSize];
        Assert.True(memory.TryRead(StatAddress, pathStat));
        Assert.Equal(
            KernelStatModeRegular,
            BinaryPrimitives.ReadUInt16LittleEndian(pathStat.AsSpan(8)));
        Assert.Equal(0, BinaryPrimitives.ReadInt64LittleEndian(pathStat.AsSpan(72)));

        context[CpuRegister.Rdi] = PathAddress;
        context[CpuRegister.Rsi] = 0;
        Assert.Equal(0, KernelMemoryCompatExports.KernelOpenUnderscore(context));
        var fd = unchecked((int)context[CpuRegister.Rax]);
        Assert.True(fd >= 3);

        context[CpuRegister.Rdi] = unchecked((ulong)fd);
        context[CpuRegister.Rsi] = StatAddress;
        Assert.Equal(0, KernelMemoryCompatExports.KernelFstat(context));
        var stat = new byte[KernelStatSize];
        Assert.True(memory.TryRead(StatAddress, stat));
        Assert.Equal(
            KernelStatModeRegular,
            BinaryPrimitives.ReadUInt16LittleEndian(stat.AsSpan(8)));
        Assert.Equal(0, BinaryPrimitives.ReadInt64LittleEndian(stat.AsSpan(72)));

        var baseline = Enumerable.Repeat((byte)0xCC, 64).ToArray();
        Assert.True(memory.TryWrite(BufferAddress, baseline));
        context[CpuRegister.Rdi] = unchecked((ulong)fd);
        context[CpuRegister.Rsi] = BufferAddress;
        context[CpuRegister.Rdx] = unchecked((ulong)baseline.Length);
        Assert.Equal(0, KernelMemoryCompatExports.KernelReadUnderscore(context));
        Assert.Equal((ulong)baseline.Length, context[CpuRegister.Rax]);
        var entropy = new byte[baseline.Length];
        Assert.True(memory.TryRead(BufferAddress, entropy));
        Assert.NotEqual(baseline, entropy);

        context[CpuRegister.Rdi] = unchecked((ulong)fd);
        Assert.Equal(0, KernelMemoryCompatExports.KernelClose(context));

        context[CpuRegister.Rdi] = unchecked((ulong)fd);
        context[CpuRegister.Rsi] = BufferAddress;
        context[CpuRegister.Rdx] = 1;
        Assert.Equal(
            (int)OrbisGen2Result.ORBIS_GEN2_ERROR_NOT_FOUND,
            KernelMemoryCompatExports.KernelReadUnderscore(context));
    }
}
