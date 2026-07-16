// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Globalization;

namespace SharpEmu.Libs.Agc;

internal readonly record struct GuestDrawTraceRequest(
    ulong ShaderAddress,
    int DrawLimit)
{
    public bool IsEnabled => ShaderAddress != 0 && DrawLimit > 0;

    public bool Matches(
        ulong exportShaderAddress,
        ulong pixelShaderAddress) =>
        IsEnabled &&
        (ShaderAddress == exportShaderAddress ||
         ShaderAddress == pixelShaderAddress);

    public bool ShouldTrace(
        ulong exportShaderAddress,
        ulong pixelShaderAddress,
        int matchingDraw) =>
        Matches(exportShaderAddress, pixelShaderAddress) &&
        matchingDraw <= DrawLimit;

    public override string ToString() =>
        FormattableString.Invariant($"0x{ShaderAddress:X}@{DrawLimit}");

    public static bool TryParse(
        string? value,
        out GuestDrawTraceRequest request)
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

        var shaderSpan = span[..separator].Trim();
        if (shaderSpan.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            shaderSpan = shaderSpan[2..];
        }

        if (!ulong.TryParse(
                shaderSpan,
                NumberStyles.HexNumber,
                CultureInfo.InvariantCulture,
                out var shaderAddress) ||
            shaderAddress == 0 ||
            !int.TryParse(
                span[(separator + 1)..].Trim(),
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out var drawLimit) ||
            drawLimit is <= 0 or > 64)
        {
            request = default;
            return false;
        }

        request = new GuestDrawTraceRequest(shaderAddress, drawLimit);
        return true;
    }
}
