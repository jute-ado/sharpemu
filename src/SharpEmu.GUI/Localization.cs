// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Text.Json;

namespace SharpEmu.GUI;

/// <summary>
/// Loads UI strings for the launcher from Languages/&lt;code&gt;.json next to the
/// executable. The files are plain, pretty-printed JSON (a flat key/value
/// map) so they stay easy to open and translate by hand; a missing or
/// unreadable key falls back to the key name itself.
/// </summary>
public sealed class Localization
{
    public static Localization Instance { get; } = new();

    public sealed record LanguageInfo(string Code, string NativeName);

    private Dictionary<string, string> _strings = new();

    private Localization()
    {
    }

    /// <summary>Directory holding the *.json language files; user-editable.</summary>
    public static string LanguagesDirectory => Path.Combine(AppContext.BaseDirectory, "Languages");

    public string CurrentCode { get; private set; } = "en";

    public string Get(string key) => _strings.TryGetValue(key, out var value) ? value : key;

    public string Format(string key, params object?[] args) => string.Format(Get(key), args);

    /// <summary>Languages discovered under <see cref="LanguagesDirectory"/>, sorted by code.</summary>
    public List<LanguageInfo> DiscoverLanguages()
    {
        var languages = new List<LanguageInfo>();
        try
        {
            foreach (var file in Directory.EnumerateFiles(LanguagesDirectory, "*.json"))
            {
                var code = Path.GetFileNameWithoutExtension(file);
                languages.Add(new LanguageInfo(code, ReadLanguageName(file) ?? code));
            }
        }
        catch (Exception)
        {
            // Missing Languages directory: no languages to offer.
        }

        languages.Sort((a, b) => string.CompareOrdinal(a.Code, b.Code));
        return languages;
    }

    private static string? ReadLanguageName(string path)
    {
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            if (document.RootElement.TryGetProperty("_languageName", out var name) &&
                name.ValueKind == JsonValueKind.String)
            {
                return name.GetString();
            }
        }
        catch (Exception)
        {
            // Malformed file: fall back to the file's code as its display name.
        }

        return null;
    }

    /// <summary>Loads a language by file code (e.g. "en"), falling back to English.</summary>
    public void Load(string code)
    {
        if (!TryLoadFile(code) && !string.Equals(code, "en", StringComparison.OrdinalIgnoreCase))
        {
            TryLoadFile("en");
        }
    }

    private bool TryLoadFile(string code)
    {
        try
        {
            var path = Path.Combine(LanguagesDirectory, $"{code}.json");
            if (!File.Exists(path))
            {
                return false;
            }

            var loaded = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(path));
            if (loaded is null)
            {
                return false;
            }

            _strings = loaded;
            CurrentCode = code;
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }
}
