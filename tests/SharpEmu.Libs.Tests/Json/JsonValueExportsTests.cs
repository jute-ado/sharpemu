// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using SharpEmu.Libs.Json;
using System.Buffers.Binary;
using System.Text;
using Xunit;

namespace SharpEmu.Libs.Tests.Json;

// Canonical JSON state is shared; both JSON test classes join one collection so xUnit
// does not run them in parallel against it.
[Collection("JsonState")]
public sealed class JsonValueExportsTests
{
    private const ulong ThisAddress = 0x1_0000_0000;
    private const ulong StringAddress = 0x1_0000_1000;
    private const ulong TextAddress = 0x1_0000_2000;

    private readonly FakeCpuMemory _memory = new(0x1_0000_0000, 0x10000);
    private readonly CpuContext _ctx;

    public JsonValueExportsTests()
    {
        JsonExports.ResetForTests();
        _ctx = new CpuContext(_memory, Generation.Gen5);
    }

    [Fact]
    public void ValueDefaultConstructor_RegistersNullAndReturnsThis()
    {
        _ctx[CpuRegister.Rdi] = ThisAddress;

        JsonValueExports.ValueDefaultConstructor(_ctx);

        Assert.Equal(ThisAddress, _ctx[CpuRegister.Rax]);
        Assert.Equal(
            System.Text.Json.JsonValueKind.Null,
            JsonExports.GetValueForTests(ThisAddress).ValueKind);
    }

    [Theory]
    [InlineData(0UL, false)]
    [InlineData(1UL, true)]
    [InlineData(0xFFFF_FF00UL, false)] // only the low byte is the bool; 0x00 low byte => false
    public void ValueSetBoolean_StoresLowByte(ulong raw, bool expected)
    {
        _ctx[CpuRegister.Rdi] = ThisAddress;
        _ctx[CpuRegister.Rsi] = raw;

        JsonValueExports.ValueSetBoolean(_ctx);

        var state = JsonExports.GetValueForTests(ThisAddress);
        Assert.Equal(
            expected
                ? System.Text.Json.JsonValueKind.True
                : System.Text.Json.JsonValueKind.False,
            state.ValueKind);
        Assert.Equal(expected, state.GetBoolean());
        Assert.Equal(ThisAddress, _ctx[CpuRegister.Rax]);
    }

    [Fact]
    public void ValueSetInteger_RoundTripsSignedValue()
    {
        _ctx[CpuRegister.Rdi] = ThisAddress;
        _ctx[CpuRegister.Rsi] = unchecked((ulong)-42L);

        JsonValueExports.ValueSetInteger(_ctx);

        var state = JsonExports.GetValueForTests(ThisAddress);
        Assert.Equal(-42L, state.GetInt64());
    }

    [Fact]
    public void ValueSetUnsigned_RoundTripsFullWidth()
    {
        _ctx[CpuRegister.Rdi] = ThisAddress;
        _ctx[CpuRegister.Rsi] = ulong.MaxValue;

        JsonValueExports.ValueSetUnsigned(_ctx);

        var state = JsonExports.GetValueForTests(ThisAddress);
        Assert.Equal(ulong.MaxValue, state.GetUInt64());
    }

    [Fact]
    public void ValueSetReal_ReadsDoubleFromXmm0()
    {
        _ctx[CpuRegister.Rdi] = ThisAddress;
        _ctx.SetXmmRegister(0, unchecked((ulong)BitConverter.DoubleToInt64Bits(3.14159)), 0);

        JsonValueExports.ValueSetReal(_ctx);

        var state = JsonExports.GetValueForTests(ThisAddress);
        Assert.Equal(3.14159, state.GetDouble(), precision: 10);
    }

    [Fact]
    public void ValueSetCString_ReadsGuestString()
    {
        _memory.WriteCString(TextAddress, "hello json");
        _ctx[CpuRegister.Rdi] = ThisAddress;
        _ctx[CpuRegister.Rsi] = TextAddress;

        JsonValueExports.ValueSetCString(_ctx);

        var state = JsonExports.GetValueForTests(ThisAddress);
        Assert.Equal("hello json", state.GetString());
    }

    [Fact]
    public void ValueSetType_KeepsRawGuestEnumValue()
    {
        _ctx[CpuRegister.Rdi] = ThisAddress;
        _ctx[CpuRegister.Rsi] = 7;

        JsonValueExports.ValueSetType(_ctx);

        Assert.Equal(7, JsonExports.GetValueTypeForTests(ThisAddress));
    }

    [Fact]
    public void StringConstructThenValueSetString_CopiesText()
    {
        _memory.WriteCString(TextAddress, "from string object");
        _ctx[CpuRegister.Rdi] = StringAddress;
        _ctx[CpuRegister.Rsi] = TextAddress;
        JsonValueExports.StringCStringConstructor(_ctx);

        Assert.Equal(
            "from string object",
            JsonExports.GetStringForTests(StringAddress));
        Assert.Equal(StringAddress, _ctx[CpuRegister.Rax]);

        _ctx[CpuRegister.Rdi] = ThisAddress;
        _ctx[CpuRegister.Rsi] = StringAddress;
        JsonValueExports.ValueSetString(_ctx);

        var state = JsonExports.GetValueForTests(ThisAddress);
        Assert.Equal("from string object", state.GetString());
    }

    [Fact]
    public void ValueSetString_MissingStringShadow_DegradesToEmpty()
    {
        _ctx[CpuRegister.Rdi] = ThisAddress;
        _ctx[CpuRegister.Rsi] = StringAddress; // never constructed

        JsonValueExports.ValueSetString(_ctx);

        var state = JsonExports.GetValueForTests(ThisAddress);
        Assert.Equal(string.Empty, state.GetString());
    }

    [Fact]
    public void Destructors_RemoveShadowState()
    {
        _ctx[CpuRegister.Rdi] = ThisAddress;
        _ctx[CpuRegister.Rsi] = 42;
        JsonValueExports.ValueIntegerConstructor(_ctx);
        _memory.WriteCString(TextAddress, "temporary");
        _ctx[CpuRegister.Rdi] = StringAddress;
        _ctx[CpuRegister.Rsi] = TextAddress;
        JsonValueExports.StringCStringConstructor(_ctx);

        Assert.Equal(2, JsonExports.GetValueTypeForTests(ThisAddress));
        Assert.Equal(
            "temporary",
            JsonExports.GetStringForTests(StringAddress));

        _ctx[CpuRegister.Rdi] = ThisAddress;
        JsonValueExports.ValueDestructor(_ctx);
        _ctx[CpuRegister.Rdi] = StringAddress;
        JsonValueExports.StringDestructor(_ctx);

        Assert.Equal(0, JsonExports.GetValueTypeForTests(ThisAddress));
        Assert.Equal(string.Empty, JsonExports.GetStringForTests(StringAddress));
        Assert.Equal(0UL, _ctx[CpuRegister.Rax]);
    }

    [Fact]
    public void ValueSetCString_FaultingPointer_DegradesToEmptyString()
    {
        _ctx[CpuRegister.Rdi] = ThisAddress;
        _ctx[CpuRegister.Rsi] = 0xDEAD_0000_0000; // outside the mapped region

        JsonValueExports.ValueSetCString(_ctx);

        var state = JsonExports.GetValueForTests(ThisAddress);
        Assert.Equal(string.Empty, state.GetString());
    }

    [Fact]
    public void PrimitiveSetterIsVisibleToCanonicalGetterAndGuestMirror()
    {
        _ctx[CpuRegister.Rdi] = ThisAddress;
        _ctx[CpuRegister.Rsi] = unchecked((ulong)-42L);
        JsonValueExports.ValueSetInteger(_ctx);

        _ctx[CpuRegister.Rdi] = ThisAddress;
        JsonExports.ValueGetType(_ctx);

        Assert.Equal(2UL, _ctx[CpuRegister.Rax]);
        Span<byte> storage = stackalloc byte[sizeof(long)];
        Assert.True(_memory.TryRead(ThisAddress + 0x10, storage));
        Assert.Equal(-42L, BinaryPrimitives.ReadInt64LittleEndian(storage));
    }

    [Fact]
    public void PrimitiveSetterReplacesValueParsedByCanonicalParser()
    {
        var json = Encoding.UTF8.GetBytes("""{"stale":true}""");
        Assert.True(_memory.TryWrite(TextAddress, json));
        _memory.WriteCString(StringAddress, "replacement");
        _ctx[CpuRegister.Rdi] = ThisAddress;
        _ctx[CpuRegister.Rsi] = TextAddress;
        _ctx[CpuRegister.Rdx] = (ulong)json.Length;
        Assert.Equal(0, JsonExports.ParserParseBuffer(_ctx));

        _ctx[CpuRegister.Rdi] = ThisAddress;
        _ctx[CpuRegister.Rsi] = StringAddress;
        JsonValueExports.ValueSetCString(_ctx);
        _ctx[CpuRegister.Rdi] = ThisAddress;
        JsonExports.ValueGetType(_ctx);

        Assert.Equal(5UL, _ctx[CpuRegister.Rax]);
    }
}
