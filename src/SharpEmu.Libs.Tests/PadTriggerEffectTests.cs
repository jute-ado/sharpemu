// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using System.Reflection;
using SharpEmu.HLE;
using SharpEmu.Libs.Pad;
using Xunit;

namespace SharpEmu.Libs.Tests;

public sealed class PadTriggerEffectTests
{
    private const ulong ParameterAddress = 0x2000;
    private const int PrimaryPadHandle = 1;
    private const int OrbisPadErrorInvalidHandle = unchecked((int)0x80920003);

    [Theory]
    [InlineData(0, 0)]
    [InlineData(1, 31)]
    [InlineData(4, 127)]
    [InlineData(8, 255)]
    [InlineData(9, 255)]
    [InlineData(255, 255)]
    public void ModeThreeScalesAndClampsActiveAmplitude(byte amplitude, byte expected)
    {
        var command = CreateCommand(3);
        command[9] = amplitude;
        command[10] = 1;

        Assert.Equal(expected, PadExports.DecodeTriggerVibration(command));
    }

    [Fact]
    public void ModeThreeIgnoresInactiveAmplitude()
    {
        var command = CreateCommand(3);
        command[9] = 8;

        Assert.Equal(0, PadExports.DecodeTriggerVibration(command));
    }

    [Theory]
    [InlineData(1, 31)]
    [InlineData(4, 127)]
    [InlineData(8, 255)]
    [InlineData(200, 255)]
    public void ModeSixUsesStrongestActiveZone(byte strongestZone, byte expected)
    {
        var command = CreateCommand(6);
        command[8] = 1;
        command[9] = 1;
        command[14] = strongestZone;

        Assert.Equal(expected, PadExports.DecodeTriggerVibration(command));
    }

    [Fact]
    public void ModeSixIgnoresZonesWhenInactive()
    {
        var command = CreateCommand(6);
        command[9] = 8;

        Assert.Equal(0, PadExports.DecodeTriggerVibration(command));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(3)]
    [InlineData(18)]
    public void TruncatedCommandsDecodeAsNoVibration(int length)
    {
        Assert.Equal(0, PadExports.DecodeTriggerVibration(new byte[length]));
    }

    [Fact]
    public void UnsupportedModeDecodesAsNoVibration()
    {
        Assert.Equal(0, PadExports.DecodeTriggerVibration(CreateCommand(99)));
    }

    [Fact]
    public void ExportRejectsInvalidHandleBeforeReadingMemory()
    {
        var context = CreateContext(new FakeGuestMemory(), handle: 2, ParameterAddress);

        var result = PadExports.PadSetTriggerEffect(context);

        AssertResult(context, OrbisPadErrorInvalidHandle, result);
    }

    [Fact]
    public void ExportRejectsNullParameter()
    {
        var context = CreateContext(new FakeGuestMemory(), PrimaryPadHandle, 0);

        var result = PadExports.PadSetTriggerEffect(context);

        AssertResult(
            context,
            (int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT,
            result);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(119)]
    public void ExportRejectsUnavailableCompleteParameter(int mappedLength)
    {
        var memory = new FakeGuestMemory();
        if (mappedLength != 0)
        {
            memory.AddRegion(ParameterAddress, new byte[mappedLength]);
        }

        var context = CreateContext(memory, PrimaryPadHandle, ParameterAddress);

        var result = PadExports.PadSetTriggerEffect(context);

        AssertResult(
            context,
            (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT,
            result);
    }

    [Fact]
    public void ExportAcceptsCompleteParameterWithoutSelectedTriggers()
    {
        var memory = new FakeGuestMemory();
        memory.AddRegion(ParameterAddress, new byte[120]);
        var context = CreateContext(memory, PrimaryPadHandle, ParameterAddress);

        var result = PadExports.PadSetTriggerEffect(context);

        AssertResult(context, (int)OrbisGen2Result.ORBIS_GEN2_OK, result);
    }

    [Fact]
    public void ExportMetadataIsExact()
    {
        var method = typeof(PadExports).GetMethod(
            nameof(PadExports.PadSetTriggerEffect),
            BindingFlags.Public | BindingFlags.Static);
        var attribute = method?.GetCustomAttribute<SysAbiExportAttribute>();

        Assert.NotNull(attribute);
        Assert.Equal("2JgFB2n9oUM", attribute.Nid);
        Assert.Equal("scePadSetTriggerEffect", attribute.ExportName);
        Assert.Equal("libScePad", attribute.LibraryName);
        Assert.Equal(Generation.Gen4 | Generation.Gen5, attribute.Target);
    }

    private static byte[] CreateCommand(uint mode)
    {
        var command = new byte[56];
        BinaryPrimitives.WriteUInt32LittleEndian(command, mode);
        return command;
    }

    private static CpuContext CreateContext(
        FakeGuestMemory memory,
        int handle,
        ulong parameterAddress)
    {
        var context = new CpuContext(memory, Generation.Gen5);
        context[CpuRegister.Rdi] = unchecked((ulong)handle);
        context[CpuRegister.Rsi] = parameterAddress;
        return context;
    }

    private static void AssertResult(CpuContext context, int expected, int actual)
    {
        Assert.Equal(expected, actual);
        Assert.Equal(unchecked((ulong)expected), context[CpuRegister.Rax]);
    }
}
