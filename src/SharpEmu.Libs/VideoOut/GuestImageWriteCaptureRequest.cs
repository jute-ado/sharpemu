// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Globalization;

namespace SharpEmu.Libs.VideoOut;

internal readonly record struct GuestImageWriteCaptureRequest(
    ulong Address,
    uint Width,
    uint Height,
    ulong PixelShaderAddress,
    string PixelShaderSignature,
    int Write)
{
    public bool IsEnabled =>
        Write > 0 &&
        (Address != 0 || Width != 0 && Height != 0);

    public bool Matches(
        ulong address,
        uint width,
        uint height,
        ulong pixelShaderAddress) =>
        Matches(address, width, height, pixelShaderAddress, null);

    public bool Matches(
        ulong address,
        uint width,
        uint height,
        ulong pixelShaderAddress,
        string? pixelShaderSignature) =>
        IsEnabled &&
        (Address != 0
            ? address == Address
            : width == Width && height == Height) &&
        (PixelShaderAddress == 0 ||
         pixelShaderAddress == PixelShaderAddress) &&
        (string.IsNullOrEmpty(PixelShaderSignature) ||
         PixelShaderSignaturesMatch(
             PixelShaderSignature,
             pixelShaderSignature));

    public bool ShouldCapture(
        ulong address,
        uint width,
        uint height,
        ulong pixelShaderAddress,
        int matchingWrite) =>
        ShouldCapture(
            address,
            width,
            height,
            pixelShaderAddress,
            pixelShaderSignature: null,
            matchingWrite);

    public bool ShouldCapture(
        ulong address,
        uint width,
        uint height,
        ulong pixelShaderAddress,
        string? pixelShaderSignature,
        int matchingWrite) =>
        Matches(address, width, height, pixelShaderAddress, pixelShaderSignature) &&
        matchingWrite == Write;

    public override string ToString()
    {
        var target = Address != 0
            ? FormattableString.Invariant($"0x{Address:X}@{Write}")
            : FormattableString.Invariant($"{Width}x{Height}@{Write}");
        if (PixelShaderAddress == 0 && string.IsNullOrEmpty(PixelShaderSignature))
        {
            return target;
        }

        var occurrenceSeparator = target.LastIndexOf('@');
        var qualifier = PixelShaderAddress != 0
            ? $"ps=0x{PixelShaderAddress:X}"
            : $"sig={PixelShaderSignature}";
        return FormattableString.Invariant(
            $"{target[..occurrenceSeparator]},{qualifier}{target[occurrenceSeparator..]}");
    }

    public static bool TryParse(
        string? value,
        out GuestImageWriteCaptureRequest request)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            request = default;
            return true;
        }

        var span = value.AsSpan().Trim();
        var separator = span.IndexOf('@');
        if (separator <= 0 || separator == span.Length - 1)
        {
            request = default;
            return false;
        }

        var selectorSpan = span[..separator].Trim();
        ulong pixelShaderAddress = 0;
        var pixelShaderSignature = string.Empty;
        var qualifierSeparator = selectorSpan.IndexOf(',');
        if (qualifierSeparator >= 0)
        {
            var qualifierSpan =
                selectorSpan[(qualifierSeparator + 1)..].Trim();
            selectorSpan = selectorSpan[..qualifierSeparator].Trim();
            var assignmentSeparator = qualifierSpan.IndexOf('=');
            if (assignmentSeparator <= 0)
            {
                request = default;
                return false;
            }

            var qualifierName = qualifierSpan[..assignmentSeparator].Trim();
            var shaderSpan =
                qualifierSpan[(assignmentSeparator + 1)..].Trim();
            if (qualifierName.Equals("ps", StringComparison.OrdinalIgnoreCase))
            {
                if (shaderSpan.StartsWith(
                        "0x",
                        StringComparison.OrdinalIgnoreCase))
                {
                    shaderSpan = shaderSpan[2..];
                }

                if (!ulong.TryParse(
                        shaderSpan,
                        NumberStyles.HexNumber,
                        CultureInfo.InvariantCulture,
                        out pixelShaderAddress) ||
                    pixelShaderAddress == 0)
                {
                    request = default;
                    return false;
                }
            }
            else if (qualifierName.Equals("sig", StringComparison.OrdinalIgnoreCase) &&
                     IsHexSignature(shaderSpan))
            {
                pixelShaderSignature = shaderSpan.ToString().ToUpperInvariant();
            }
            else
            {
                request = default;
                return false;
            }
        }

        ulong address = 0;
        uint width = 0;
        uint height = 0;
        if (!selectorSpan.StartsWith("0x", StringComparison.OrdinalIgnoreCase) &&
            TryParseDimensions(selectorSpan, out width, out height))
        {
        }
        else
        {
            if (selectorSpan.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            {
                selectorSpan = selectorSpan[2..];
            }

            if (!ulong.TryParse(
                    selectorSpan,
                    NumberStyles.HexNumber,
                    CultureInfo.InvariantCulture,
                    out address) ||
                address == 0)
            {
                request = default;
                return false;
            }
        }

        if (
            !int.TryParse(
                span[(separator + 1)..].Trim(),
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out var write) ||
            write <= 0)
        {
            request = default;
            return false;
        }

        request = new GuestImageWriteCaptureRequest(
            address,
            width,
            height,
            pixelShaderAddress,
            pixelShaderSignature,
            write);
        return true;
    }

    private static bool IsHexSignature(ReadOnlySpan<char> value)
    {
        if (value.Length < 8 || value.Length > 64)
        {
            return false;
        }

        foreach (var character in value)
        {
            if (!Uri.IsHexDigit(character))
            {
                return false;
            }
        }

        return true;
    }

    private static bool PixelShaderSignaturesMatch(
        string requested,
        string? actual)
    {
        if (string.IsNullOrEmpty(actual))
        {
            return false;
        }

        return actual.StartsWith(requested, StringComparison.OrdinalIgnoreCase) ||
               requested.StartsWith(actual, StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryParseDimensions(
        ReadOnlySpan<char> value,
        out uint width,
        out uint height)
    {
        width = 0;
        height = 0;
        var separator = value.IndexOfAny('x', 'X');
        if (separator <= 0 || separator == value.Length - 1)
        {
            return false;
        }

        return uint.TryParse(
                   value[..separator].Trim(),
                   NumberStyles.None,
                   CultureInfo.InvariantCulture,
                   out width) &&
               width > 0 &&
               uint.TryParse(
                   value[(separator + 1)..].Trim(),
                   NumberStyles.None,
                   CultureInfo.InvariantCulture,
                   out height) &&
               height > 0;
    }
}
