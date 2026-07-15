// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Reflection;
using System.Text.Json;
using SharpEmu.GUI;
using Xunit;

namespace SharpEmu.GUI.Tests;

public sealed class LocalizationResourceTests
{
    private const string ResourcePrefix = "Languages.";
    private const string ResourceSuffix = ".json";

    [Fact]
    public void EveryEmbeddedLanguageHasValidSchemaAndKnownKeys()
    {
        var assembly = typeof(Localization).Assembly;
        var resources = assembly.GetManifestResourceNames()
            .Where(name => name.StartsWith(ResourcePrefix, StringComparison.Ordinal) &&
                           name.EndsWith(ResourceSuffix, StringComparison.Ordinal))
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

        var englishKeys = ReadAndValidateLanguage(assembly, "Languages.en.json");
        foreach (var resource in resources)
        {
            var actualKeys = ReadAndValidateLanguage(assembly, resource);
            var extra = actualKeys.Except(englishKeys).OrderBy(key => key, StringComparer.Ordinal).ToArray();

            Assert.True(
                extra.Length == 0,
                $"{resource} contains keys absent from English: [{string.Join(", ", extra)}]");
        }
    }

    [Fact]
    public void HungarianLanguageMatchesEnglishKeySet()
    {
        var assembly = typeof(Localization).Assembly;
        var englishKeys = ReadAndValidateLanguage(assembly, "Languages.en.json");
        var hungarianKeys = ReadAndValidateLanguage(assembly, "Languages.hu.json");

        Assert.True(
            englishKeys.SetEquals(hungarianKeys),
            "The Hungarian translation must contain the complete English key set.");
    }

    private static HashSet<string> ReadAndValidateLanguage(Assembly assembly, string resource)
    {
        using var stream = assembly.GetManifestResourceStream(resource);
        Assert.NotNull(stream);

        using var document = JsonDocument.Parse(stream);
        Assert.Equal(JsonValueKind.Object, document.RootElement.ValueKind);
        Assert.True(document.RootElement.TryGetProperty("_languageName", out var languageName));
        Assert.Equal(JsonValueKind.String, languageName.ValueKind);
        Assert.False(string.IsNullOrWhiteSpace(languageName.GetString()));

        var properties = document.RootElement.EnumerateObject().ToArray();
        Assert.All(properties, property => Assert.Equal(JsonValueKind.String, property.Value.ValueKind));

        var keys = properties.Select(property => property.Name).ToHashSet(StringComparer.Ordinal);
        Assert.Equal(properties.Length, keys.Count);
        return keys;
    }
}
