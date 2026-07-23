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

public sealed record Ps5ApplicationMetadata(
    string? Title,
    string? TitleId,
    string? ContentId,
    string? Version,
    ulong? FlexibleMemorySize = null);

public static class Ps5ParamJsonReader
{
    public static (string? Title, string? TitleId, string? Version) TryReadPs5Param(IFileSystem fs, string paramJsonPath)
    {
        var metadata = TryReadApplicationMetadata(fs, paramJsonPath);
        return (metadata.Title, metadata.TitleId, metadata.Version);
    }

    public static Ps5ApplicationMetadata TryReadApplicationMetadata(IFileSystem fs, string paramJsonPath)
    {
        if (!fs.Exists(paramJsonPath))
            return EmptyMetadata();

        if (!fs.TryReadAllBytes(paramJsonPath, out var data))
            return EmptyMetadata();

        return TryReadApplicationMetadata(data);
    }

    public static (string? Title, string? TitleId, string? Version) TryReadPs5Param(byte[] data)
    {
        var metadata = TryReadApplicationMetadata(data);
        return (metadata.Title, metadata.TitleId, metadata.Version);
    }

    public static Ps5ApplicationMetadata TryReadApplicationMetadata(byte[] data)
    {
        if (data == null || data.Length == 0)
            return EmptyMetadata();

        try
        {
            ReadOnlyMemory<byte> json = data;
            if (json.Span.StartsWith("\uFEFF"u8))
            {
                json = json[3..];
            }

            using var doc = JsonDocument.Parse(json);
            return TryReadApplicationMetadata(doc.RootElement);
        }
        catch (JsonException)
        {
            return EmptyMetadata();
        }
    }

    private static Ps5ApplicationMetadata TryReadApplicationMetadata(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
        {
            return EmptyMetadata();
        }

        var titleId = TryGetString(root, "titleId");
        var contentId = TryGetString(root, "contentId");
        var version =
            TryGetString(root, "contentVersion")
            ?? TryGetString(root, "masterVersion")
            ?? TryGetString(root, "targetContentVersion");
        ulong? flexibleMemorySize = null;
        if (TryGetObject(root, "kernel", out var kernel) &&
            kernel.TryGetProperty("flexibleMemorySize", out var flexibleMemoryValue) &&
            flexibleMemoryValue.ValueKind == JsonValueKind.Number &&
            flexibleMemoryValue.TryGetUInt64(out var parsedFlexibleMemorySize) &&
            parsedFlexibleMemorySize != 0)
        {
            flexibleMemorySize = parsedFlexibleMemorySize;
        }

        return new Ps5ApplicationMetadata(
            ExtractTitleName(root),
            titleId,
            contentId,
            version,
            flexibleMemorySize);
    }

    private static Ps5ApplicationMetadata EmptyMetadata() => new(null, null, null, null);

    private static string? ExtractTitleName(JsonElement root)
    {
        if (!TryGetObject(root, "localizedParameters", out var localizedParameters))
        {
            if (!TryGetObject(root, "disc", out var disc) ||
                !TryGetObject(disc, "localizedParameters", out localizedParameters))
            {
                return null;
            }
        }

        var defaultLanguage = TryGetString(localizedParameters, "defaultLanguage");
        if (!string.IsNullOrEmpty(defaultLanguage) &&
            TryGetObject(localizedParameters, defaultLanguage, out var defaultLanguageObject))
        {
            var title = TryGetString(defaultLanguageObject, "titleName");
            if (!string.IsNullOrWhiteSpace(title))
            {
                return title;
            }
        }

        if (TryGetObject(localizedParameters, "en-US", out var english))
        {
            var title = TryGetString(english, "titleName");
            if (!string.IsNullOrWhiteSpace(title))
            {
                return title;
            }
        }

        foreach (var property in localizedParameters.EnumerateObject())
        {
            if (property.Value.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var title = TryGetString(property.Value, "titleName");
            if (!string.IsNullOrWhiteSpace(title))
            {
                return title;
            }
        }

        return null;
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
