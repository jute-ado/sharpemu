// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using Xunit;

namespace SharpEmu.GUI.Tests;

public sealed class GuiSettingsTests
{
    [Fact]
    public void NullPropertiesRestoreDefaults()
    {
        const string json = """
            {
              "LogLevel": null,
              "GameFolders": null,
              "ExcludedGames": null,
              "EnvironmentToggles": null,
              "Language": null,
              "DiscordClientId": null
            }
            """;

        var settings = GuiSettings.NormalizeFromJson(json);

        Assert.Equal("Info", settings.LogLevel);
        Assert.Equal("en", settings.Language);
        Assert.Equal("1525606762248540221", settings.DiscordClientId);
        Assert.Empty(settings.GameFolders);
        Assert.Empty(settings.ExcludedGames);
        Assert.Empty(settings.EnvironmentToggles);
    }

    [Fact]
    public void ValidPropertiesArePreserved()
    {
        const string json = """
            {
              "LogLevel": "Debug",
              "GameFolders": ["F:\\Games"],
              "ExcludedGames": ["F:\\Games\\skip.bin"],
              "EnvironmentToggles": ["SHARPEMU_TRACE"],
              "Language": "pt-BR",
              "DiscordClientId": "999"
            }
            """;

        var settings = GuiSettings.NormalizeFromJson(json);

        Assert.Equal("Debug", settings.LogLevel);
        Assert.Equal("pt-BR", settings.Language);
        Assert.Equal("999", settings.DiscordClientId);
        Assert.Equal(["F:\\Games"], settings.GameFolders);
        Assert.Equal(["F:\\Games\\skip.bin"], settings.ExcludedGames);
        Assert.Equal(["SHARPEMU_TRACE"], settings.EnvironmentToggles);
    }

    [Fact]
    public void EmptyDiscordClientIdRemainsAnExplicitOptOut()
    {
        var settings =
            GuiSettings.NormalizeFromJson("""{ "DiscordClientId": "" }""");

        Assert.Equal(string.Empty, settings.DiscordClientId);
    }

    [Fact]
    public void NullAndEmptyListEntriesAreRemovedInPlace()
    {
        const string json = """
            {
              "GameFolders": ["F:\\Games", null, "", "G:\\Games"],
              "ExcludedGames": [null],
              "EnvironmentToggles": [null, "SHARPEMU_TRACE", ""]
            }
            """;

        var settings = GuiSettings.NormalizeFromJson(json);

        Assert.Equal(["F:\\Games", "G:\\Games"], settings.GameFolders);
        Assert.Empty(settings.ExcludedGames);
        Assert.Equal(["SHARPEMU_TRACE"], settings.EnvironmentToggles);
    }

    [Fact]
    public void EmptyObjectRetainsConstructorDefaults()
    {
        var settings = GuiSettings.NormalizeFromJson("{}");

        Assert.Equal("Info", settings.LogLevel);
        Assert.Equal("en", settings.Language);
        Assert.Equal("1525606762248540221", settings.DiscordClientId);
        Assert.Empty(settings.GameFolders);
        Assert.Empty(settings.ExcludedGames);
        Assert.Empty(settings.EnvironmentToggles);
    }
}
