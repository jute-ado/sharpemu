// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using System.Buffers.Text;
using SharpEmu.HLE;

namespace SharpEmu.Libs.Kernel;

public static partial class KernelMemoryCompatExports
{
    private const int MaximumScanStringLength = 1_048_576;

    private enum ScanConversionResult
    {
        Success,
        MatchingFailure,
        InputFailure,
        MemoryFault,
    }

    [SysAbiExport(
        Nid = "1Pk0qZQGeWo",
        ExportName = "sscanf",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libc")]
    public static int Sscanf(CpuContext ctx)
    {
        if (!TryReadCString(ctx, ctx[CpuRegister.Rdi], MaximumScanStringLength, out var input) ||
            !TryReadCString(ctx, ctx[CpuRegister.Rsi], MaximumScanStringLength, out var format))
        {
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT;
        }

        var arguments = new RegisterPrintfArgumentSource(ctx, gpIndex: 2);
        var scanResult = ScanFormattedInput(ctx, input, format, ref arguments, out var memoryFault);
        if (memoryFault)
        {
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT;
        }

        ctx[CpuRegister.Rax] = unchecked((ulong)(long)scanResult);
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    private static int ScanFormattedInput(
        CpuContext ctx,
        ReadOnlySpan<byte> input,
        ReadOnlySpan<byte> format,
        ref RegisterPrintfArgumentSource arguments,
        out bool memoryFault)
    {
        var inputIndex = 0;
        var formatIndex = 0;
        var assignments = 0;
        memoryFault = false;
        Span<bool> scanSet = stackalloc bool[256];

        while (formatIndex < format.Length)
        {
            if (IsAsciiWhitespace(format[formatIndex]))
            {
                do
                {
                    formatIndex++;
                }
                while (formatIndex < format.Length && IsAsciiWhitespace(format[formatIndex]));

                SkipAsciiWhitespace(input, ref inputIndex);
                continue;
            }

            if (format[formatIndex] != (byte)'%')
            {
                if (inputIndex >= input.Length)
                {
                    return assignments == 0 ? -1 : assignments;
                }

                if (input[inputIndex] != format[formatIndex])
                {
                    return assignments;
                }

                inputIndex++;
                formatIndex++;
                continue;
            }

            formatIndex++;
            if (formatIndex >= format.Length)
            {
                return assignments;
            }

            if (format[formatIndex] == (byte)'%')
            {
                if (inputIndex >= input.Length)
                {
                    return assignments == 0 ? -1 : assignments;
                }

                if (input[inputIndex] != (byte)'%')
                {
                    return assignments;
                }

                inputIndex++;
                formatIndex++;
                continue;
            }

            var suppressAssignment = false;
            if (format[formatIndex] == (byte)'*')
            {
                suppressAssignment = true;
                formatIndex++;
            }

            var width = 0;
            while (formatIndex < format.Length &&
                   format[formatIndex] is >= (byte)'0' and <= (byte)'9')
            {
                width = width > (int.MaxValue - 9) / 10
                    ? int.MaxValue
                    : (width * 10) + format[formatIndex] - (byte)'0';
                formatIndex++;
            }

            var lengthModifier = ParseScanLengthModifier(format, ref formatIndex);
            if (formatIndex >= format.Length)
            {
                return assignments;
            }

            var specifier = format[formatIndex++];
            if (specifier == (byte)'[' &&
                !TryParseScanSet(format, ref formatIndex, scanSet))
            {
                return assignments;
            }

            if (specifier == (byte)'n')
            {
                if (!suppressAssignment)
                {
                    var destination = arguments.NextGpArg();
                    if (!TryWriteScannedInteger(ctx, destination, unchecked((ulong)inputIndex), lengthModifier, isPointer: false))
                    {
                        memoryFault = true;
                        return assignments;
                    }
                }

                continue;
            }

            if (specifier is not ((byte)'c') and not ((byte)'['))
            {
                SkipAsciiWhitespace(input, ref inputIndex);
            }

            if (inputIndex >= input.Length)
            {
                return assignments == 0 ? -1 : assignments;
            }

            var conversion = specifier switch
            {
                (byte)'d' or (byte)'i' or (byte)'u' or (byte)'x' or (byte)'X' or (byte)'o' or (byte)'p' =>
                    ScanInteger(
                        ctx,
                        input,
                        ref inputIndex,
                        width,
                        specifier,
                        lengthModifier,
                        suppressAssignment,
                        ref arguments),
                (byte)'f' or (byte)'F' or (byte)'e' or (byte)'E' or (byte)'g' or (byte)'G' or (byte)'a' or (byte)'A' =>
                    ScanFloat(
                        ctx,
                        input,
                        ref inputIndex,
                        width,
                        lengthModifier,
                        suppressAssignment,
                        ref arguments),
                (byte)'s' => ScanString(
                    ctx,
                    input,
                    ref inputIndex,
                    width,
                    suppressAssignment,
                    ref arguments),
                (byte)'c' => ScanCharacters(
                    ctx,
                    input,
                    ref inputIndex,
                    width,
                    suppressAssignment,
                    ref arguments),
                (byte)'[' => ScanSetSequence(
                    ctx,
                    input,
                    ref inputIndex,
                    width,
                    scanSet,
                    suppressAssignment,
                    ref arguments),
                _ => ScanConversionResult.MatchingFailure,
            };

            switch (conversion)
            {
                case ScanConversionResult.Success:
                    if (!suppressAssignment)
                    {
                        assignments++;
                    }

                    break;
                case ScanConversionResult.InputFailure:
                    return assignments == 0 ? -1 : assignments;
                case ScanConversionResult.MemoryFault:
                    memoryFault = true;
                    return assignments;
                default:
                    return assignments;
            }
        }

        return assignments;
    }

    private static string ParseScanLengthModifier(ReadOnlySpan<byte> format, ref int formatIndex)
    {
        if (formatIndex >= format.Length)
        {
            return string.Empty;
        }

        var value = format[formatIndex];
        if (value is (byte)'h' or (byte)'l')
        {
            formatIndex++;
            if (formatIndex < format.Length && format[formatIndex] == value)
            {
                formatIndex++;
                return value == (byte)'h' ? "hh" : "ll";
            }

            return value == (byte)'h' ? "h" : "l";
        }

        if (value is (byte)'j' or (byte)'z' or (byte)'t' or (byte)'L')
        {
            formatIndex++;
            return ((char)value).ToString();
        }

        return string.Empty;
    }

    private static ScanConversionResult ScanInteger(
        CpuContext ctx,
        ReadOnlySpan<byte> input,
        ref int inputIndex,
        int width,
        byte specifier,
        string lengthModifier,
        bool suppressAssignment,
        ref RegisterPrintfArgumentSource arguments)
    {
        var available = LimitScanWidth(input.Length - inputIndex, width);
        if (!TryParseScannedInteger(input.Slice(inputIndex, available), specifier, out var value, out var consumed))
        {
            return ScanConversionResult.MatchingFailure;
        }

        inputIndex += consumed;
        if (suppressAssignment)
        {
            return ScanConversionResult.Success;
        }

        var destination = arguments.NextGpArg();
        return TryWriteScannedInteger(
            ctx,
            destination,
            value,
            lengthModifier,
            isPointer: specifier == (byte)'p')
            ? ScanConversionResult.Success
            : ScanConversionResult.MemoryFault;
    }

    private static bool TryParseScannedInteger(
        ReadOnlySpan<byte> input,
        byte specifier,
        out ulong value,
        out int consumed)
    {
        value = 0;
        consumed = 0;
        if (input.IsEmpty)
        {
            return false;
        }

        var index = 0;
        var negative = false;
        if (input[index] is (byte)'+' or (byte)'-')
        {
            negative = input[index] == (byte)'-';
            index++;
            if (index >= input.Length)
            {
                return false;
            }
        }

        var numberBase = specifier switch
        {
            (byte)'o' => 8,
            (byte)'x' or (byte)'X' or (byte)'p' => 16,
            _ => 10,
        };

        var prefixLength = 0;
        if (specifier == (byte)'i')
        {
            if (input[index] == (byte)'0')
            {
                if (index + 2 < input.Length &&
                    input[index + 1] is (byte)'x' or (byte)'X' &&
                    TryGetDigitValue(input[index + 2], 16, out _))
                {
                    numberBase = 16;
                    prefixLength = 2;
                }
                else
                {
                    numberBase = 8;
                }
            }
        }
        else if (numberBase == 16 &&
                 index + 2 < input.Length &&
                 input[index] == (byte)'0' &&
                 input[index + 1] is (byte)'x' or (byte)'X' &&
                 TryGetDigitValue(input[index + 2], 16, out _))
        {
            prefixLength = 2;
        }

        index += prefixLength;
        var digitCount = 0;
        ulong magnitude = 0;
        while (index < input.Length && TryGetDigitValue(input[index], numberBase, out var digit))
        {
            magnitude = magnitude > (ulong.MaxValue - (uint)digit) / (uint)numberBase
                ? ulong.MaxValue
                : (magnitude * (uint)numberBase) + (uint)digit;
            digitCount++;
            index++;
        }

        if (digitCount == 0)
        {
            return false;
        }

        value = negative ? unchecked(0UL - magnitude) : magnitude;
        consumed = index;
        return true;
    }

    private static ScanConversionResult ScanFloat(
        CpuContext ctx,
        ReadOnlySpan<byte> input,
        ref int inputIndex,
        int width,
        string lengthModifier,
        bool suppressAssignment,
        ref RegisterPrintfArgumentSource arguments)
    {
        var available = LimitScanWidth(input.Length - inputIndex, width);
        if (!Utf8Parser.TryParse(input.Slice(inputIndex, available), out double value, out var consumed) || consumed == 0)
        {
            return ScanConversionResult.MatchingFailure;
        }

        inputIndex += consumed;
        if (suppressAssignment)
        {
            return ScanConversionResult.Success;
        }

        var destination = arguments.NextGpArg();
        return TryWriteScannedFloat(ctx, destination, value, lengthModifier)
            ? ScanConversionResult.Success
            : ScanConversionResult.MemoryFault;
    }

    private static ScanConversionResult ScanCharacters(
        CpuContext ctx,
        ReadOnlySpan<byte> input,
        ref int inputIndex,
        int width,
        bool suppressAssignment,
        ref RegisterPrintfArgumentSource arguments)
    {
        var count = width == 0 ? 1 : width;
        if (count > input.Length - inputIndex)
        {
            return ScanConversionResult.InputFailure;
        }

        var start = inputIndex;
        inputIndex += count;
        if (suppressAssignment)
        {
            return ScanConversionResult.Success;
        }

        var destination = arguments.NextGpArg();
        return TryWriteCompat(ctx, destination, input.Slice(start, count))
            ? ScanConversionResult.Success
            : ScanConversionResult.MemoryFault;
    }

    private static ScanConversionResult ScanString(
        CpuContext ctx,
        ReadOnlySpan<byte> input,
        ref int inputIndex,
        int width,
        bool suppressAssignment,
        ref RegisterPrintfArgumentSource arguments)
    {
        var limit = LimitScanWidth(input.Length - inputIndex, width);
        var consumed = 0;
        while (consumed < limit && !IsAsciiWhitespace(input[inputIndex + consumed]))
        {
            consumed++;
        }

        if (consumed == 0)
        {
            return ScanConversionResult.MatchingFailure;
        }

        var start = inputIndex;
        inputIndex += consumed;
        if (suppressAssignment)
        {
            return ScanConversionResult.Success;
        }

        var destination = arguments.NextGpArg();
        var payload = new byte[consumed + 1];
        input.Slice(start, consumed).CopyTo(payload);
        return TryWriteCompat(ctx, destination, payload)
            ? ScanConversionResult.Success
            : ScanConversionResult.MemoryFault;
    }

    private static ScanConversionResult ScanSetSequence(
        CpuContext ctx,
        ReadOnlySpan<byte> input,
        ref int inputIndex,
        int width,
        ReadOnlySpan<bool> scanSet,
        bool suppressAssignment,
        ref RegisterPrintfArgumentSource arguments)
    {
        var limit = LimitScanWidth(input.Length - inputIndex, width);
        var consumed = 0;
        while (consumed < limit && scanSet[input[inputIndex + consumed]])
        {
            consumed++;
        }

        if (consumed == 0)
        {
            return ScanConversionResult.MatchingFailure;
        }

        var start = inputIndex;
        inputIndex += consumed;
        if (suppressAssignment)
        {
            return ScanConversionResult.Success;
        }

        var destination = arguments.NextGpArg();
        var payload = new byte[consumed + 1];
        input.Slice(start, consumed).CopyTo(payload);
        return TryWriteCompat(ctx, destination, payload)
            ? ScanConversionResult.Success
            : ScanConversionResult.MemoryFault;
    }

    private static bool TryParseScanSet(
        ReadOnlySpan<byte> format,
        ref int formatIndex,
        Span<bool> scanSet)
    {
        scanSet.Clear();
        if (formatIndex >= format.Length)
        {
            return false;
        }

        var negate = false;
        if (format[formatIndex] == (byte)'^')
        {
            negate = true;
            formatIndex++;
        }

        var first = true;
        int pending = -1;
        while (formatIndex < format.Length)
        {
            var current = format[formatIndex++];
            if (current == (byte)']' && !first)
            {
                if (pending >= 0)
                {
                    scanSet[pending] = true;
                }

                if (negate)
                {
                    for (var i = 0; i < scanSet.Length; i++)
                    {
                        scanSet[i] = !scanSet[i];
                    }
                }

                return true;
            }

            first = false;
            if (current == (byte)'-' &&
                pending >= 0 &&
                formatIndex < format.Length &&
                format[formatIndex] != (byte)']')
            {
                var rangeEnd = format[formatIndex++];
                if (rangeEnd >= pending)
                {
                    for (var value = pending; value <= rangeEnd; value++)
                    {
                        scanSet[value] = true;
                    }

                    pending = -1;
                    continue;
                }
            }

            if (pending >= 0)
            {
                scanSet[pending] = true;
            }

            pending = current;
        }

        return false;
    }

    private static bool TryWriteScannedInteger(
        CpuContext ctx,
        ulong destination,
        ulong value,
        string lengthModifier,
        bool isPointer)
    {
        var size = isPointer
            ? sizeof(ulong)
            : lengthModifier switch
            {
                "hh" => sizeof(byte),
                "h" => sizeof(ushort),
                "l" or "ll" or "j" or "z" or "t" => sizeof(ulong),
                _ => sizeof(uint),
            };

        Span<byte> payload = stackalloc byte[sizeof(ulong)];
        switch (size)
        {
            case sizeof(byte):
                payload[0] = unchecked((byte)value);
                break;
            case sizeof(ushort):
                BinaryPrimitives.WriteUInt16LittleEndian(payload, unchecked((ushort)value));
                break;
            case sizeof(uint):
                BinaryPrimitives.WriteUInt32LittleEndian(payload, unchecked((uint)value));
                break;
            default:
                BinaryPrimitives.WriteUInt64LittleEndian(payload, value);
                break;
        }

        return TryWriteCompat(ctx, destination, payload[..size]);
    }

    private static bool TryWriteScannedFloat(
        CpuContext ctx,
        ulong destination,
        double value,
        string lengthModifier)
    {
        Span<byte> payload = stackalloc byte[sizeof(double)];
        if (lengthModifier.Length == 0)
        {
            BinaryPrimitives.WriteInt32LittleEndian(
                payload,
                BitConverter.SingleToInt32Bits(unchecked((float)value)));
            return TryWriteCompat(ctx, destination, payload[..sizeof(float)]);
        }

        BinaryPrimitives.WriteInt64LittleEndian(payload, BitConverter.DoubleToInt64Bits(value));
        return TryWriteCompat(ctx, destination, payload);
    }

    private static int LimitScanWidth(int available, int width)
        => width == 0 ? available : Math.Min(available, width);

    private static void SkipAsciiWhitespace(ReadOnlySpan<byte> input, ref int inputIndex)
    {
        while (inputIndex < input.Length && IsAsciiWhitespace(input[inputIndex]))
        {
            inputIndex++;
        }
    }

    private static bool IsAsciiWhitespace(byte value)
        => value is (byte)' ' or (byte)'\t' or (byte)'\n' or (byte)'\r' or (byte)'\f' or (byte)'\v';

    private static bool TryGetDigitValue(byte value, int numberBase, out int digit)
    {
        digit = value switch
        {
            >= (byte)'0' and <= (byte)'9' => value - (byte)'0',
            >= (byte)'a' and <= (byte)'f' => value - (byte)'a' + 10,
            >= (byte)'A' and <= (byte)'F' => value - (byte)'A' + 10,
            _ => -1,
        };
        return digit >= 0 && digit < numberBase;
    }
}
