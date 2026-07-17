// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;

namespace SharpEmu.Libs.Json;

// sce::Json::Value and sce::Json::String constructors, setters and destructors. Prospero titles
// (Quake among them) build a Value tree and populate it through these before serializing it for a
// web request; without them the imports resolve to nothing and the guest faults on the call. The
// payload and its bounded guest mirror are modelled in JsonExports; this file only translates
// the observed complete-object C++ ABI variants (registers in, `this` back out). Base-object
// variants use the same canonical state through JsonExports.
public static class JsonValueExports
{
    private const int MaxStringLength = 0x10000;

    private static double ReadDoubleArg(CpuContext ctx)
    {
        ctx.GetXmmRegister(0, out var low, out _);
        return BitConverter.Int64BitsToDouble(unchecked((long)low));
    }

    private static string ReadCString(CpuContext ctx, ulong address) =>
        ctx.TryReadNullTerminatedUtf8(address, MaxStringLength, out var text) ? text : string.Empty;

    // ---- sce::Json::Value constructors ----

    [SysAbiExport(Nid = "qBMjqyBn3OM", ExportName = "_ZN3sce4Json5ValueC1Ev",
        Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceJson")]
    public static int ValueDefaultConstructor(CpuContext ctx)
        => JsonExports.SetNullValue(ctx);

    [SysAbiExport(Nid = "UeuWT+yNdCQ", ExportName = "_ZN3sce4Json5ValueC1Eb",
        Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceJson")]
    public static int ValueBooleanConstructor(CpuContext ctx)
        => JsonExports.SetBooleanValue(
            ctx,
            (ctx[CpuRegister.Rsi] & 0xFF) != 0);

    [SysAbiExport(Nid = "0lLK8+kDqmE", ExportName = "_ZN3sce4Json5ValueC1El",
        Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceJson")]
    public static int ValueIntegerConstructor(CpuContext ctx)
        => JsonExports.SetIntegerValue(
            ctx,
            unchecked((long)ctx[CpuRegister.Rsi]));

    [SysAbiExport(Nid = "x4AUdbhpRB0", ExportName = "_ZN3sce4Json5ValueC1Em",
        Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceJson")]
    public static int ValueUnsignedConstructor(CpuContext ctx)
        => JsonExports.SetUnsignedIntegerValue(
            ctx,
            ctx[CpuRegister.Rsi]);

    [SysAbiExport(Nid = "sOmU4vnx3s0", ExportName = "_ZN3sce4Json5ValueC1Ed",
        Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceJson")]
    public static int ValueRealConstructor(CpuContext ctx)
        => JsonExports.SetRealValue(ctx, ReadDoubleArg(ctx));

    [SysAbiExport(Nid = "b9V6fmppLXY", ExportName = "_ZN3sce4Json5ValueC1EPKc",
        Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceJson")]
    public static int ValueCStringConstructor(CpuContext ctx)
        => JsonExports.SetStringValue(
            ctx,
            ReadCString(ctx, ctx[CpuRegister.Rsi]));

    [SysAbiExport(Nid = "CbrT3dwDILo", ExportName = "_ZN3sce4Json5ValueC1ENS0_9ValueTypeE",
        Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceJson")]
    public static int ValueTypeConstructor(CpuContext ctx)
        => JsonExports.SetExplicitType(
            ctx,
            unchecked((uint)ctx[CpuRegister.Rsi]));

    [SysAbiExport(Nid = "sZIoMRGO+jk", ExportName = "_ZN3sce4Json5ValueC1ERKNS0_6StringE",
        Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceJson")]
    public static int ValueStringConstructor(CpuContext ctx)
        => JsonExports.SetStringValue(
            ctx,
            JsonExports.GetStringObject(ctx[CpuRegister.Rsi]));

    [SysAbiExport(Nid = "WTtYf+cNnXI", ExportName = "_ZN3sce4Json5ValueD1Ev",
        Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceJson")]
    public static int ValueDestructor(CpuContext ctx)
        => JsonExports.DestroyCanonicalValue(ctx);

    // ---- sce::Json::Value setters (return Value&, i.e. `this`) ----

    [SysAbiExport(Nid = "5yHuiWXo2gg", ExportName = "_ZN3sce4Json5Value3setEb",
        Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceJson")]
    public static int ValueSetBoolean(CpuContext ctx)
        => JsonExports.SetBooleanValue(
            ctx,
            (ctx[CpuRegister.Rsi] & 0xFF) != 0);

    [SysAbiExport(Nid = "QxVVYhP-mvg", ExportName = "_ZN3sce4Json5Value3setEl",
        Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceJson")]
    public static int ValueSetInteger(CpuContext ctx)
        => JsonExports.SetIntegerValue(
            ctx,
            unchecked((long)ctx[CpuRegister.Rsi]));

    [SysAbiExport(Nid = "SIe1ZmW7e7s", ExportName = "_ZN3sce4Json5Value3setEm",
        Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceJson")]
    public static int ValueSetUnsigned(CpuContext ctx)
        => JsonExports.SetUnsignedIntegerValue(
            ctx,
            ctx[CpuRegister.Rsi]);

    [SysAbiExport(Nid = "BSmWDIkV4w4", ExportName = "_ZN3sce4Json5Value3setEd",
        Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceJson")]
    public static int ValueSetReal(CpuContext ctx)
        => JsonExports.SetRealValue(ctx, ReadDoubleArg(ctx));

    [SysAbiExport(Nid = "IKQimvG9Wqs", ExportName = "_ZN3sce4Json5Value3setENS0_9ValueTypeE",
        Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceJson")]
    public static int ValueSetType(CpuContext ctx)
        => JsonExports.SetExplicitType(
            ctx,
            unchecked((uint)ctx[CpuRegister.Rsi]));

    [SysAbiExport(Nid = "n6FC+l9DU70", ExportName = "_ZN3sce4Json5Value3setEPKc",
        Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceJson")]
    public static int ValueSetCString(CpuContext ctx)
        => JsonExports.SetStringValue(
            ctx,
            ReadCString(ctx, ctx[CpuRegister.Rsi]));

    [SysAbiExport(Nid = "6l3Bv2gysNc", ExportName = "_ZN3sce4Json5Value3setERKNS0_6StringE",
        Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceJson")]
    public static int ValueSetString(CpuContext ctx)
        => JsonExports.SetStringValue(
            ctx,
            JsonExports.GetStringObject(ctx[CpuRegister.Rsi]));

    [SysAbiExport(Nid = "FIjXN2TkuTs", ExportName = "_ZN3sce4Json5Value5clearEv",
        Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceJson")]
    public static int ValueClear(CpuContext ctx)
        => JsonExports.SetNullValue(ctx);

    // ---- sce::Json::String ----

    [SysAbiExport(Nid = "9KUZFjI1IxA", ExportName = "_ZN3sce4Json6StringC1EPKc",
        Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceJson")]
    public static int StringCStringConstructor(CpuContext ctx)
        => JsonExports.SetStringObject(
            ctx,
            ReadCString(ctx, ctx[CpuRegister.Rsi]));

    [SysAbiExport(Nid = "qSmqLXXCPas", ExportName = "_ZN3sce4Json6StringC1Ev",
        Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceJson")]
    public static int StringDefaultConstructor(CpuContext ctx)
        => JsonExports.SetStringObject(ctx, string.Empty);

    [SysAbiExport(Nid = "0CAesfH963Q", ExportName = "_ZN3sce4Json6StringC1ERKS1_",
        Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceJson")]
    public static int StringCopyConstructor(CpuContext ctx)
        => JsonExports.SetStringObject(
            ctx,
            JsonExports.GetStringObject(ctx[CpuRegister.Rsi]));

    [SysAbiExport(Nid = "cG1VE2HMl6c", ExportName = "_ZN3sce4Json6StringD1Ev",
        Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceJson")]
    public static int StringDestructor(CpuContext ctx)
        => JsonExports.DestroyCanonicalString(ctx);
}
