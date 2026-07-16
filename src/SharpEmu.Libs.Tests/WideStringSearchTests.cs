// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Runtime.InteropServices;
using System.Text;
using SharpEmu.HLE;
using SharpEmu.Libs.Kernel;
using Xunit;

namespace SharpEmu.Libs.Tests;

public sealed class WideStringSearchTests
{
    private const ulong HaystackAddress = 0x1000;
    private const ulong NeedleAddress = 0x2000;

    [Fact]
    public void WcsstrReturnsAddressOfFirstMatch()
    {
        var context = CreateContext("one two two", "two");

        var result = KernelMemoryCompatExports.Wcsstr(context);

        Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_OK, result);
        Assert.Equal(HaystackAddress + (4 * sizeof(ushort)), context[CpuRegister.Rax]);
    }

    [Fact]
    public void WcsstrReturnsNullWhenNeedleIsMissing()
    {
        var context = CreateContext("sharp emulator", "kernel");

        var result = KernelMemoryCompatExports.Wcsstr(context);

        Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_OK, result);
        Assert.Equal(0UL, context[CpuRegister.Rax]);
    }

    [Fact]
    public void WcsstrReturnsHaystackForEmptyNeedle()
    {
        var context = CreateContext("sharp emulator", string.Empty);

        var result = KernelMemoryCompatExports.Wcsstr(context);

        Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_OK, result);
        Assert.Equal(HaystackAddress, context[CpuRegister.Rax]);
    }

    [Fact]
    public void WcsstrMatchesUtf16CodeUnitSequences()
    {
        const string prefix = "ready ";
        var context = CreateContext(prefix + "🎮 now", "🎮");

        var result = KernelMemoryCompatExports.Wcsstr(context);

        Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_OK, result);
        Assert.Equal(
            HaystackAddress + ((ulong)prefix.Length * sizeof(ushort)),
            context[CpuRegister.Rax]);
    }

    [Fact]
    public void WcsstrReadsTrackedLibcAllocations()
    {
        var context = new CpuContext(new FakeGuestMemory(), Generation.Gen5);
        var haystack = AllocateWideString(context, "runtime wide search");
        var needle = AllocateWideString(context, "wide");

        try
        {
            context[CpuRegister.Rdi] = haystack;
            context[CpuRegister.Rsi] = needle;

            Assert.Equal(
                (int)OrbisGen2Result.ORBIS_GEN2_OK,
                KernelMemoryCompatExports.Wcsstr(context));
            Assert.Equal(haystack + (8 * sizeof(ushort)), context[CpuRegister.Rax]);
        }
        finally
        {
            Free(context, needle);
            Free(context, haystack);
        }
    }

    [Theory]
    [InlineData(0, NeedleAddress)]
    [InlineData(HaystackAddress, 0)]
    [InlineData(0xDEAD_0000, NeedleAddress)]
    [InlineData(HaystackAddress, 0xDEAD_0000)]
    public void WcsstrRejectsUnreadablePointers(ulong haystack, ulong needle)
    {
        var context = CreateContext("haystack", "needle");
        context[CpuRegister.Rdi] = haystack;
        context[CpuRegister.Rsi] = needle;

        var result = KernelMemoryCompatExports.Wcsstr(context);

        Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT, result);
        Assert.Equal(0UL, context[CpuRegister.Rax]);
    }

    [Fact]
    public void WcsstrExportMetadataIsExact()
    {
        ExportMetadataAssert.Exact(
            "WDpobjImAb4",
            "wcsstr",
            "libc",
            Generation.Gen4 | Generation.Gen5);
    }

    private static CpuContext CreateContext(string haystack, string needle)
    {
        var memory = new FakeGuestMemory();
        memory.AddRegion(HaystackAddress, Encoding.Unicode.GetBytes(haystack + '\0'));
        memory.AddRegion(NeedleAddress, Encoding.Unicode.GetBytes(needle + '\0'));
        var context = new CpuContext(memory, Generation.Gen5);
        context[CpuRegister.Rdi] = HaystackAddress;
        context[CpuRegister.Rsi] = NeedleAddress;
        return context;
    }

    private static ulong AllocateWideString(CpuContext context, string value)
    {
        var bytes = Encoding.Unicode.GetBytes(value + '\0');
        context[CpuRegister.Rdi] = unchecked((ulong)bytes.Length);
        Assert.Equal(
            (int)OrbisGen2Result.ORBIS_GEN2_OK,
            KernelMemoryCompatExports.Malloc(context));
        var address = context[CpuRegister.Rax];
        Assert.NotEqual(0UL, address);
        Marshal.Copy(bytes, 0, (nint)address, bytes.Length);
        return address;
    }

    private static void Free(CpuContext context, ulong address)
    {
        context[CpuRegister.Rdi] = address;
        _ = KernelMemoryCompatExports.Free(context);
    }
}
