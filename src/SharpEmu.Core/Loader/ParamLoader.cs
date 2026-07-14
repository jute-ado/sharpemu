// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Text.Json;
using SharpEmu.Core;

namespace SharpEmu.Core.Loader;

public sealed record ParamLoader(
    string? TitleId,
    string? ContentId,
    string? ContentVersion,
    string? MasterVersion,
    string? TargetContentVersion,
    LocalizedParameters? LocalizedParameters,
    Disc? Disc
);

public sealed record LocalizedParameters(
    string? DefaultLanguage,
    Dictionary<string, LocalizedLanguage>? Languages
);

public sealed record LocalizedLanguage(string? TitleName);

public sealed record Disc(LocalizedParameters? LocalizedParameters);

public static class Ps5ParamJsonReader
{
    public static (string? Title, string? TitleId, string? Version) TryReadPs5Param(IFileSystem fs, string paramJsonPath)
    {
        if (!fs.Exists(paramJsonPath))
            return (null, null, null);

        if (!fs.TryReadAllBytes(paramJsonPath, out var data))
            return (null, null, null);

        return TryReadPs5Param(data);
    }

    public static (string? Title, string? TitleId, string? Version) TryReadPs5Param(byte[] data)
    {
        if (data == null || data.Length == 0)
            return (null, null, null);

        try
        {
            using var doc = JsonDocument.Parse(data);
            return TryReadPs5Param(doc.RootElement);
        }
        catch (JsonException)
        {
            return (null, null, null);
        }
    }

    private static (string? Title, string? TitleId, string? Version) TryReadPs5Param(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
        {
            return default;
        }

        var titleId = TryGetString(root, "titleId");

        string? ver =
            TryGetString(root, "contentVersion")
            ?? TryGetString(root, "masterVersion")
            ?? TryGetString(root, "targetContentVersion");

        string? title = ExtractTitleName(root);

        return (title, titleId, ver);
    }

    private static string? ExtractTitleName(JsonElement root)
    {
        if (!TryGetObject(root, "localizedParameters", out var lp))
        {
            if (!TryGetObject(root, "disc", out var disc) ||
                !TryGetObject(disc, "localizedParameters", out lp))
            {
                return null;
            }
        }

        var defLang = TryGetString(lp, "defaultLanguage");

        if (!string.IsNullOrEmpty(defLang))
        {
            if (TryGetObject(lp, defLang, out var langObj))
            {
                var title = TryGetString(langObj, "titleName");
                if (title is not null)
                {
                    return title;
                }
            }
        }

        return TryGetObject(lp, "en-US", out var english)
            ? TryGetString(english, "titleName")
            : null;
    }

    private static bool TryGetObject(JsonElement parent, string propertyName, out JsonElement value)
    {
        if (parent.ValueKind == JsonValueKind.Object &&
            parent.TryGetProperty(propertyName, out value) &&
            value.ValueKind == JsonValueKind.Object)
        {
            return true;
        }

        value = default;
        return false;
    }

    private static string? TryGetString(JsonElement parent, string propertyName) =>
        parent.ValueKind == JsonValueKind.Object &&
        parent.TryGetProperty(propertyName, out var value) &&
        value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}
