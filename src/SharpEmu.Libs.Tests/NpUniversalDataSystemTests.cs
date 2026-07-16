// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Reflection;
using System.Text;
using SharpEmu.HLE;
using SharpEmu.Libs.Np;
using Xunit;

namespace SharpEmu.Libs.Tests;

public sealed class NpUniversalDataSystemTests
{
    private const int InvalidArgument = unchecked((int)0x80553102);
    private const ulong ArrayAddress = 0x1000;
    private const ulong ValueAddress = 0x2000;

    [Fact]
    public void ArraySetStringValidatesDestinationAndBoundedString()
    {
        var memory = new FakeGuestMemory();
        memory.AddRegion(ArrayAddress, new byte[1]);
        memory.AddRegion(ValueAddress, Encoding.UTF8.GetBytes("telemetry\0"));
        var context = new CpuContext(memory, Generation.Gen5);
        context[CpuRegister.Rdi] = ArrayAddress;
        context[CpuRegister.Rsi] = ValueAddress;

        Assert.Equal(
            0,
            NpUniversalDataSystemExports
                .NpUniversalDataSystemEventPropertyArraySetString(context));
        Assert.Equal(0UL, context[CpuRegister.Rax]);

        context[CpuRegister.Rdi] = 0;
        Assert.Equal(
            InvalidArgument,
            NpUniversalDataSystemExports
                .NpUniversalDataSystemEventPropertyArraySetString(context));
        Assert.Equal(unchecked((ulong)InvalidArgument), context[CpuRegister.Rax]);

        context[CpuRegister.Rdi] = ArrayAddress;
        context[CpuRegister.Rsi] = 0x3000;
        Assert.Equal(
            (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT,
            NpUniversalDataSystemExports
                .NpUniversalDataSystemEventPropertyArraySetString(context));
    }

    [Fact]
    public void ArraySetStringExportMetadataIsExact()
    {
        var method = typeof(NpUniversalDataSystemExports).GetMethod(
            nameof(NpUniversalDataSystemExports
                .NpUniversalDataSystemEventPropertyArraySetString),
            BindingFlags.Public | BindingFlags.Static);
        var attribute = method?.GetCustomAttribute<SysAbiExportAttribute>();

        Assert.NotNull(attribute);
        Assert.Equal("4llLk7YJRTE", attribute.Nid);
        Assert.Equal(
            "sceNpUniversalDataSystemEventPropertyArraySetString",
            attribute.ExportName);
        Assert.Equal("libSceNpUniversalDataSystem", attribute.LibraryName);
        Assert.Equal(Generation.Gen4 | Generation.Gen5, attribute.Target);
    }
}
