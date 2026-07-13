// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using System.Reflection;
using System.Text;
using SharpEmu.HLE;
using SharpEmu.Libs.Kernel;
using Xunit;

namespace SharpEmu.Libs.Tests;

public sealed class FormattedInputTests
{
    private const ulong InputAddress = 0x1000;
    private const ulong FormatAddress = 0x2000;
    private const ulong FirstOutputAddress = 0x3000;
    private const ulong SecondOutputAddress = 0x4000;
    private const ulong ThirdOutputAddress = 0x5000;
    private const ulong FourthOutputAddress = 0x6000;
    private const ulong FifthOutputAddress = 0x7000;
    private const ulong StackAddress = 0x8000;

    [Fact]
    public void SscanfParsesIntegersAndWidthLimitedString()
    {
        var signedOutput = new byte[sizeof(int)];
        var autoBaseOutput = new byte[sizeof(int)];
        var stringOutput = new byte[6];
        var context = CreateContext("  -42 0x2a hello", "%d %i %5s");
        AddOutput(context, FirstOutputAddress, signedOutput);
        AddOutput(context, SecondOutputAddress, autoBaseOutput);
        AddOutput(context, ThirdOutputAddress, stringOutput);
        SetOutputRegisters(context, FirstOutputAddress, SecondOutputAddress, ThirdOutputAddress);

        var result = KernelMemoryCompatExports.Sscanf(context);

        Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_OK, result);
        Assert.Equal(3UL, context[CpuRegister.Rax]);
        Assert.Equal(-42, BinaryPrimitives.ReadInt32LittleEndian(signedOutput));
        Assert.Equal(42, BinaryPrimitives.ReadInt32LittleEndian(autoBaseOutput));
        Assert.Equal("hello\0", Encoding.ASCII.GetString(stringOutput));
    }

    [Fact]
    public void SscanfSupportsScanSetsAndAssignmentSuppression()
    {
        var prefixOutput = new byte[4];
        var suffixOutput = new byte[5];
        var context = CreateContext("abc123 rest", "%3[a-z]%*3d %s");
        AddOutput(context, FirstOutputAddress, prefixOutput);
        AddOutput(context, SecondOutputAddress, suffixOutput);
        SetOutputRegisters(context, FirstOutputAddress, SecondOutputAddress);

        var result = KernelMemoryCompatExports.Sscanf(context);

        Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_OK, result);
        Assert.Equal(2UL, context[CpuRegister.Rax]);
        Assert.Equal("abc\0", Encoding.ASCII.GetString(prefixOutput));
        Assert.Equal("rest\0", Encoding.ASCII.GetString(suffixOutput));
    }

    [Fact]
    public void SscanfWritesLengthModifiedValuesAndReadsSpilledPointer()
    {
        var byteOutput = new byte[sizeof(byte)];
        var ushortOutput = new byte[sizeof(ushort)];
        var uintOutput = new byte[sizeof(uint)];
        var ulongOutput = new byte[sizeof(ulong)];
        var intOutput = new byte[sizeof(int)];
        var stack = new byte[16];
        BinaryPrimitives.WriteUInt64LittleEndian(stack.AsSpan(8), FifthOutputAddress);

        var context = CreateContext(
            "255 65535 4294967295 18446744073709551615 7",
            "%hhu %hu %u %llu %d");
        AddOutput(context, FirstOutputAddress, byteOutput);
        AddOutput(context, SecondOutputAddress, ushortOutput);
        AddOutput(context, ThirdOutputAddress, uintOutput);
        AddOutput(context, FourthOutputAddress, ulongOutput);
        AddOutput(context, FifthOutputAddress, intOutput);
        AddOutput(context, StackAddress, stack);
        SetOutputRegisters(
            context,
            FirstOutputAddress,
            SecondOutputAddress,
            ThirdOutputAddress,
            FourthOutputAddress);
        context[CpuRegister.Rsp] = StackAddress;

        var result = KernelMemoryCompatExports.Sscanf(context);

        Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_OK, result);
        Assert.Equal(5UL, context[CpuRegister.Rax]);
        Assert.Equal(byte.MaxValue, byteOutput[0]);
        Assert.Equal(ushort.MaxValue, BinaryPrimitives.ReadUInt16LittleEndian(ushortOutput));
        Assert.Equal(uint.MaxValue, BinaryPrimitives.ReadUInt32LittleEndian(uintOutput));
        Assert.Equal(ulong.MaxValue, BinaryPrimitives.ReadUInt64LittleEndian(ulongOutput));
        Assert.Equal(7, BinaryPrimitives.ReadInt32LittleEndian(intOutput));
    }

    [Fact]
    public void SscanfCharacterPreservesWhitespaceAndCountDoesNotAddAssignment()
    {
        var characterOutput = new byte[] { 0xCC };
        var countOutput = new byte[sizeof(int)];
        var context = CreateContext(" A", "%c%n");
        AddOutput(context, FirstOutputAddress, characterOutput);
        AddOutput(context, SecondOutputAddress, countOutput);
        SetOutputRegisters(context, FirstOutputAddress, SecondOutputAddress);

        var result = KernelMemoryCompatExports.Sscanf(context);

        Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_OK, result);
        Assert.Equal(1UL, context[CpuRegister.Rax]);
        Assert.Equal((byte)' ', characterOutput[0]);
        Assert.Equal(1, BinaryPrimitives.ReadInt32LittleEndian(countOutput));
    }

    [Fact]
    public void SscanfReturnsCompletedAssignmentsAfterMatchingFailure()
    {
        var firstOutput = Enumerable.Repeat((byte)0xCC, sizeof(int)).ToArray();
        var secondOutput = Enumerable.Repeat((byte)0xCC, sizeof(int)).ToArray();
        var context = CreateContext("12 nope", "%d %d");
        AddOutput(context, FirstOutputAddress, firstOutput);
        AddOutput(context, SecondOutputAddress, secondOutput);
        SetOutputRegisters(context, FirstOutputAddress, SecondOutputAddress);

        var result = KernelMemoryCompatExports.Sscanf(context);

        Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_OK, result);
        Assert.Equal(1UL, context[CpuRegister.Rax]);
        Assert.Equal(12, BinaryPrimitives.ReadInt32LittleEndian(firstOutput));
        Assert.All(secondOutput, value => Assert.Equal(0xCC, value));
    }

    [Fact]
    public void SscanfReturnsEofBeforeFirstConversion()
    {
        var output = Enumerable.Repeat((byte)0xCC, sizeof(int)).ToArray();
        var context = CreateContext(string.Empty, "%d");
        AddOutput(context, FirstOutputAddress, output);
        SetOutputRegisters(context, FirstOutputAddress);

        var result = KernelMemoryCompatExports.Sscanf(context);

        Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_OK, result);
        Assert.Equal(ulong.MaxValue, context[CpuRegister.Rax]);
        Assert.All(output, value => Assert.Equal(0xCC, value));
    }

    [Fact]
    public void SscanfWritesFloatAndDoubleDestinations()
    {
        var floatOutput = new byte[sizeof(float)];
        var doubleOutput = new byte[sizeof(double)];
        var context = CreateContext("1.25 -2.5", "%f %lf");
        AddOutput(context, FirstOutputAddress, floatOutput);
        AddOutput(context, SecondOutputAddress, doubleOutput);
        SetOutputRegisters(context, FirstOutputAddress, SecondOutputAddress);

        var result = KernelMemoryCompatExports.Sscanf(context);

        Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_OK, result);
        Assert.Equal(2UL, context[CpuRegister.Rax]);
        Assert.Equal(1.25f, BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(floatOutput)));
        Assert.Equal(-2.5, BitConverter.Int64BitsToDouble(BinaryPrimitives.ReadInt64LittleEndian(doubleOutput)));
    }

    [Fact]
    public void SscanfReturnsMemoryFaultForUnreadableInputOrFormat()
    {
        var memory = new FakeGuestMemory();
        memory.AddRegion(FormatAddress, Terminated("%d"));
        var context = new CpuContext(memory, Generation.Gen5);
        context[CpuRegister.Rdi] = 0xDEAD_0000;
        context[CpuRegister.Rsi] = FormatAddress;

        Assert.Equal(
            (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT,
            KernelMemoryCompatExports.Sscanf(context));

        memory.AddRegion(InputAddress, Terminated("7"));
        context[CpuRegister.Rdi] = InputAddress;
        context[CpuRegister.Rsi] = 0xDEAD_0000;

        Assert.Equal(
            (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT,
            KernelMemoryCompatExports.Sscanf(context));
    }

    [Fact]
    public void SscanfReturnsMemoryFaultForUnmappedOutput()
    {
        var context = CreateContext("7", "%d");
        SetOutputRegisters(context, 0xDEAD_0000);

        Assert.Equal(
            (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT,
            KernelMemoryCompatExports.Sscanf(context));
    }

    [Fact]
    public void SscanfExportMetadataIsExact()
    {
        var method = typeof(KernelMemoryCompatExports).GetMethod(
            nameof(KernelMemoryCompatExports.Sscanf),
            BindingFlags.Public | BindingFlags.Static);
        var attribute = method?.GetCustomAttribute<SysAbiExportAttribute>();

        Assert.NotNull(attribute);
        Assert.Equal("1Pk0qZQGeWo", attribute.Nid);
        Assert.Equal("sscanf", attribute.ExportName);
        Assert.Equal("libc", attribute.LibraryName);
        Assert.Equal(Generation.Gen4 | Generation.Gen5, attribute.Target);
    }

    private static CpuContext CreateContext(string input, string format)
    {
        var memory = new FakeGuestMemory();
        memory.AddRegion(InputAddress, Terminated(input));
        memory.AddRegion(FormatAddress, Terminated(format));
        var context = new CpuContext(memory, Generation.Gen5);
        context[CpuRegister.Rdi] = InputAddress;
        context[CpuRegister.Rsi] = FormatAddress;
        return context;
    }

    private static void AddOutput(CpuContext context, ulong address, byte[] output)
        => ((FakeGuestMemory)context.Memory).AddRegion(address, output);

    private static void SetOutputRegisters(CpuContext context, params ulong[] outputs)
    {
        var registers = new[]
        {
            CpuRegister.Rdx,
            CpuRegister.Rcx,
            CpuRegister.R8,
            CpuRegister.R9,
        };
        Assert.True(outputs.Length <= registers.Length);
        for (var i = 0; i < outputs.Length; i++)
        {
            context[registers[i]] = outputs[i];
        }
    }

    private static byte[] Terminated(string value)
        => Encoding.ASCII.GetBytes(value + "\0");
}
