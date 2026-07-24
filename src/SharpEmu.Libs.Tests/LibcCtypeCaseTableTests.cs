// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Runtime.InteropServices;
using SharpEmu.HLE;
using SharpEmu.Libs.LibcStdio;
using Xunit;

namespace SharpEmu.Libs.Tests;

public sealed class LibcCtypeCaseTableTests
{
    [Fact]
    public void LowerTable_CoversSignedCharEofAsciiAndExtendedByteIndices()
    {
        var pointer = GetTable(LibcStdioExports.GetPtolower);

        Assert.Equal((short)128, ReadEntry(pointer, -128));
        Assert.Equal((short)254, ReadEntry(pointer, -2));
        Assert.Equal((short)-1, ReadEntry(pointer, -1));
        Assert.Equal((short)0, ReadEntry(pointer, 0));
        Assert.Equal((short)'a', ReadEntry(pointer, 'A'));
        Assert.Equal((short)'a', ReadEntry(pointer, 'a'));
        Assert.Equal((short)0x80, ReadEntry(pointer, 0x80));
        Assert.Equal((short)0xFF, ReadEntry(pointer, 0xFF));
    }

    [Fact]
    public void UpperTable_CoversSignedCharEofAsciiAndExtendedByteIndices()
    {
        var pointer = GetTable(LibcStdioExports.GetPtoupper);

        Assert.Equal((short)128, ReadEntry(pointer, -128));
        Assert.Equal((short)254, ReadEntry(pointer, -2));
        Assert.Equal((short)-1, ReadEntry(pointer, -1));
        Assert.Equal((short)0, ReadEntry(pointer, 0));
        Assert.Equal((short)'A', ReadEntry(pointer, 'a'));
        Assert.Equal((short)'A', ReadEntry(pointer, 'A'));
        Assert.Equal((short)0x80, ReadEntry(pointer, 0x80));
        Assert.Equal((short)0xFF, ReadEntry(pointer, 0xFF));
    }

    [Fact]
    public void CaseTableAccessors_PublishStableDistinctPointers()
    {
        var lower = GetTable(LibcStdioExports.GetPtolower);
        var upper = GetTable(LibcStdioExports.GetPtoupper);

        Assert.NotEqual(lower, upper);
        Assert.Equal(lower, GetTable(LibcStdioExports.GetPtolower));
        Assert.Equal(upper, GetTable(LibcStdioExports.GetPtoupper));
    }

    private static nint GetTable(Func<CpuContext, int> accessor)
    {
        var context = new CpuContext(
            new FakeGuestMemory(),
            Generation.Gen5);

        Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_OK, accessor(context));
        Assert.NotEqual(0UL, context[CpuRegister.Rax]);
        return unchecked((nint)(long)context[CpuRegister.Rax]);
    }

    private static short ReadEntry(nint table, int index) =>
        Marshal.ReadInt16(table + (index * sizeof(short)));
}
