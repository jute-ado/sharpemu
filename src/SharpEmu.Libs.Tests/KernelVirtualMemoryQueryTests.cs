// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using SharpEmu.HLE;
using SharpEmu.Libs.Kernel;
using Xunit;

namespace SharpEmu.Libs.Tests;

public sealed class KernelVirtualMemoryQueryTests
{
    private const ulong OutputAddress = 0x1000;
    private const ulong QueryAddress = 0x0000_0006_0010_0000;
    private const ulong RegionAddress = 0x0000_0006_0020_0000;
    private const ulong RegionLength = 0x4000;

    [Fact]
    public void FindNextUsesTheBackingGuestMemoryMap()
    {
        var output = Enumerable.Repeat((byte)0xCC, 72).ToArray();
        var memory = new QueryGuestMemory(
            new GuestVirtualMemoryRegion(RegionAddress, RegionLength, 0x03));
        memory.AddRegion(OutputAddress, output);
        var context = new CpuContext(memory, Generation.Gen5);
        context[CpuRegister.Rdi] = QueryAddress;
        context[CpuRegister.Rsi] = 1;
        context[CpuRegister.Rdx] = OutputAddress;
        context[CpuRegister.Rcx] = 72;

        var result = KernelMemoryCompatExports.KernelVirtualQuery(context);

        Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_OK, result);
        Assert.Equal(RegionAddress, BinaryPrimitives.ReadUInt64LittleEndian(output.AsSpan(0, 8)));
        Assert.Equal(RegionAddress + RegionLength, BinaryPrimitives.ReadUInt64LittleEndian(output.AsSpan(8, 8)));
        Assert.Equal(3, BinaryPrimitives.ReadInt32LittleEndian(output.AsSpan(24, 4)));
        Assert.Equal(0x10, output[32]);
    }

    [Fact]
    public void UnmappedQueryReturnsKernelAccessError()
    {
        var output = Enumerable.Repeat((byte)0xCC, 72).ToArray();
        var memory = new QueryGuestMemory(
            new GuestVirtualMemoryRegion(RegionAddress, RegionLength, 0x03));
        memory.AddRegion(OutputAddress, output);
        var context = new CpuContext(memory, Generation.Gen5);
        context[CpuRegister.Rdi] = QueryAddress;
        context[CpuRegister.Rsi] = 0;
        context[CpuRegister.Rdx] = OutputAddress;
        context[CpuRegister.Rcx] = 72;

        var result = KernelMemoryCompatExports.KernelVirtualQuery(context);

        Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_ERROR_DELETED, result);
        Assert.All(output, value => Assert.Equal(0xCC, value));
    }

    [Fact]
    public void QueryReportsRegisteredGuestStackMetadata()
    {
        var output = new byte[72];
        var memory = new QueryGuestMemory(
            new GuestVirtualMemoryRegion(RegionAddress, RegionLength, 0x03));
        memory.AddRegion(OutputAddress, output);
        memory.RegisterStackRange(RegionAddress, RegionLength);
        var context = new CpuContext(memory, Generation.Gen5);
        context[CpuRegister.Rdi] = RegionAddress + 0x100;
        context[CpuRegister.Rdx] = OutputAddress;
        context[CpuRegister.Rcx] = 72;

        var result = KernelMemoryCompatExports.KernelVirtualQuery(context);

        Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_OK, result);
        Assert.Equal(0x14, output[32]);
    }

    [Theory]
    [InlineData(2, 72)]
    [InlineData(0, 71)]
    [InlineData(0, 73)]
    public void QueryRejectsUnknownFlagsAndNonExactInfoSizes(int flags, ulong infoSize)
    {
        var output = Enumerable.Repeat((byte)0xCC, 72).ToArray();
        var memory = new QueryGuestMemory(
            new GuestVirtualMemoryRegion(RegionAddress, RegionLength, 0x03));
        memory.AddRegion(OutputAddress, output);
        var context = new CpuContext(memory, Generation.Gen5);
        context[CpuRegister.Rdi] = RegionAddress + 0x100;
        context[CpuRegister.Rsi] = unchecked((ulong)flags);
        context[CpuRegister.Rdx] = OutputAddress;
        context[CpuRegister.Rcx] = infoSize;

        var result = KernelMemoryCompatExports.KernelVirtualQuery(context);

        Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT, result);
        Assert.All(output, value => Assert.Equal(0xCC, value));
    }

    private sealed class QueryGuestMemory :
        ICpuMemory,
        IGuestVirtualMemoryQuery,
        IGuestStackMemory
    {
        private readonly GuestVirtualMemoryRegion _queryRegion;
        private readonly List<(ulong Address, byte[] Data)> _regions = [];
        private readonly List<(ulong Start, ulong End)> _stackRanges = [];

        public QueryGuestMemory(GuestVirtualMemoryRegion queryRegion)
        {
            _queryRegion = queryRegion;
        }

        public void AddRegion(ulong address, byte[] data) => _regions.Add((address, data));

        public void RegisterStackRange(ulong start, ulong size) =>
            _stackRanges.Add((start, checked(start + size)));

        public bool TryGetStackRange(ulong address, out ulong start, out ulong end)
        {
            foreach (var range in _stackRanges)
            {
                if (address >= range.Start && address < range.End)
                {
                    start = range.Start;
                    end = range.End;
                    return true;
                }
            }

            start = 0;
            end = 0;
            return false;
        }

        public bool TryQueryMemoryRegion(
            ulong address,
            bool findNext,
            out GuestVirtualMemoryRegion region)
        {
            var end = _queryRegion.Address + _queryRegion.Length;
            if ((address >= _queryRegion.Address && address < end) ||
                (findNext && address <= _queryRegion.Address))
            {
                region = _queryRegion;
                return true;
            }

            region = default;
            return false;
        }

        public bool TryRead(ulong virtualAddress, Span<byte> destination)
        {
            foreach (var (address, data) in _regions)
            {
                if (virtualAddress < address || virtualAddress - address > (ulong)data.Length)
                {
                    continue;
                }

                var offset = virtualAddress - address;
                if ((ulong)destination.Length > (ulong)data.Length - offset)
                {
                    continue;
                }

                data.AsSpan(checked((int)offset), destination.Length).CopyTo(destination);
                return true;
            }

            return false;
        }

        public bool TryWrite(ulong virtualAddress, ReadOnlySpan<byte> source)
        {
            foreach (var (address, data) in _regions)
            {
                if (virtualAddress < address || virtualAddress - address > (ulong)data.Length)
                {
                    continue;
                }

                var offset = virtualAddress - address;
                if ((ulong)source.Length > (ulong)data.Length - offset)
                {
                    continue;
                }

                source.CopyTo(data.AsSpan(checked((int)offset), source.Length));
                return true;
            }

            return false;
        }
    }
}
