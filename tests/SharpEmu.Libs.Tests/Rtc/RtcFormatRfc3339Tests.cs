// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Globalization;
using System.Text;
using SharpEmu.HLE;
using Xunit;

namespace SharpEmu.Libs.Tests.Rtc;

public sealed class RtcFormatRfc3339Tests
{
    private const string FormatRfc3339Nid = "WJ3rqFwymew";
    private const int RtcErrorInvalidPointer = unchecked((int)0x80B50002);
    private const int RtcErrorInvalidValue = unchecked((int)0x80B50003);
    private const ulong Base = 0x1_0000_0000;
    private const ulong OutputAddress = Base + 0x100;
    private const ulong TickAddress = Base + 0x200;

    [Theory]
    [InlineData(Generation.Gen4)]
    [InlineData(Generation.Gen5)]
    public void FormatRfc3339HasExactCrossGenerationMetadata(Generation generation)
    {
        var manager = CreateManager(generation);

        Assert.True(manager.TryGetExport(FormatRfc3339Nid, out var export));
        Assert.Equal("sceRtcFormatRFC3339", export.Name);
        Assert.Equal("libSceRtc", export.LibraryName);
        Assert.Equal(Generation.Gen4 | Generation.Gen5, export.Target);
    }

    [Theory]
    [InlineData(0, "2024-02-29T23:59:58.12Z")]
    [InlineData(330, "2024-03-01T05:29:58.12+05:30")]
    [InlineData(-90, "2024-02-29T22:29:58.12-01:30")]
    public void FormatRfc3339FormatsExplicitTicksAndTimezoneOffsets(int timezoneMinutes, string expected)
    {
        var memory = new FakeCpuMemory(Base, 0x10000);
        var context = new CpuContext(memory, Generation.Gen5);
        var instant = new DateTime(2024, 2, 29, 23, 59, 58, 120, DateTimeKind.Utc)
            .AddTicks(3_456);
        Assert.True(context.TryWriteUInt64(TickAddress, unchecked((ulong)(instant.Ticks / 10))));
        context[CpuRegister.Rdi] = OutputAddress;
        context[CpuRegister.Rsi] = TickAddress;
        context[CpuRegister.Rdx] = unchecked((ulong)timezoneMinutes);

        var manager = CreateManager(Generation.Gen5);
        Assert.True(manager.TryDispatch(FormatRfc3339Nid, context, out var result));
        Assert.Equal(0, (int)result);
        Assert.Equal(expected, ReadNullTerminatedUtf8(memory, OutputAddress, 32));
    }

    [Fact]
    public void FormatRfc3339UsesCurrentUtcTickWhenTickPointerIsNull()
    {
        var memory = new FakeCpuMemory(Base, 0x10000);
        var context = new CpuContext(memory, Generation.Gen5);
        context[CpuRegister.Rdi] = OutputAddress;
        context[CpuRegister.Rsi] = 0;
        context[CpuRegister.Rdx] = 0;
        var before = DateTime.UtcNow.AddSeconds(-1);

        var manager = CreateManager(Generation.Gen5);
        Assert.True(manager.TryDispatch(FormatRfc3339Nid, context, out var result));
        var after = DateTime.UtcNow.AddSeconds(1);

        Assert.Equal(0, (int)result);
        var formatted = ReadNullTerminatedUtf8(memory, OutputAddress, 32);
        Assert.True(DateTime.TryParseExact(
            formatted,
            "yyyy-MM-dd'T'HH:mm:ss.ff'Z'",
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
            out var parsed));
        Assert.InRange(parsed, before, after);
    }

    [Fact]
    public void FormatRfc3339RejectsNullOutputAndOffsetUnderflowWithoutMutatingOutput()
    {
        var memory = new FakeCpuMemory(Base, 0x10000);
        var context = new CpuContext(memory, Generation.Gen5);
        var sentinel = Enumerable.Repeat((byte)0xA5, 32).ToArray();
        Assert.True(memory.TryWrite(OutputAddress, sentinel));
        Assert.True(context.TryWriteUInt64(TickAddress, 0));
        var manager = CreateManager(Generation.Gen5);

        context[CpuRegister.Rdi] = 0;
        context[CpuRegister.Rsi] = TickAddress;
        context[CpuRegister.Rdx] = 0;
        Assert.True(manager.TryDispatch(FormatRfc3339Nid, context, out var result));
        Assert.Equal(RtcErrorInvalidPointer, (int)result);

        context[CpuRegister.Rdi] = OutputAddress;
        context[CpuRegister.Rdx] = unchecked((ulong)-1);
        Assert.True(manager.TryDispatch(FormatRfc3339Nid, context, out result));
        Assert.Equal(RtcErrorInvalidValue, (int)result);
        Assert.Equal(sentinel, ReadBytes(memory, OutputAddress, sentinel.Length));
    }

    [Fact]
    public void FormatRfc3339RejectsShortOutputWithoutPartialWrites()
    {
        const int outputSize = 28;
        var outputAddress = Base + 0x10000 - outputSize;
        var memory = new FakeCpuMemory(Base, 0x10000);
        var context = new CpuContext(memory, Generation.Gen5);
        var sentinel = Enumerable.Repeat((byte)0xA5, outputSize).ToArray();
        Assert.True(memory.TryWrite(outputAddress, sentinel));
        var instant = new DateTime(2024, 2, 29, 23, 59, 58, DateTimeKind.Utc);
        Assert.True(context.TryWriteUInt64(TickAddress, unchecked((ulong)(instant.Ticks / 10))));
        context[CpuRegister.Rdi] = outputAddress;
        context[CpuRegister.Rsi] = TickAddress;
        context[CpuRegister.Rdx] = 330;

        var manager = CreateManager(Generation.Gen5);
        Assert.True(manager.TryDispatch(FormatRfc3339Nid, context, out var result));
        Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT, (int)result);
        Assert.Equal(sentinel, ReadBytes(memory, outputAddress, outputSize));
    }

    private static ModuleManager CreateManager(Generation generation)
    {
        var manager = new ModuleManager();
        manager.RegisterExports(SharpEmu.Generated.SysAbiExportRegistry.CreateExports(generation));
        return manager;
    }

    private static string ReadNullTerminatedUtf8(FakeCpuMemory memory, ulong address, int maximumLength)
    {
        var bytes = ReadBytes(memory, address, maximumLength);
        var terminator = Array.IndexOf(bytes, (byte)0);
        Assert.True(terminator >= 0);
        return Encoding.UTF8.GetString(bytes, 0, terminator);
    }

    private static byte[] ReadBytes(FakeCpuMemory memory, ulong address, int length)
    {
        var bytes = new byte[length];
        Assert.True(memory.TryRead(address, bytes));
        return bytes;
    }
}
