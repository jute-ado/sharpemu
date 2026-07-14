// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Text;
using SharpEmu.Core.Loader;
using Xunit;

namespace SharpEmu.Core.Tests;

public sealed class ParamLoaderTests
{
    [Theory]
    [InlineData("")]
    [InlineData("{")]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("42")]
    [InlineData("\"metadata\"")]
    public void InvalidOrUnsupportedJsonShapesReturnEmptyMetadata(string json)
    {
        var metadata = Read(json);

        Assert.Equal((null, null, null), metadata);
    }

    [Fact]
    public void WrongPropertyTypesAreIgnoredWithoutDiscardingValidFallbacks()
    {
        const string json = """
            {
              "titleId": 12345,
              "contentVersion": { "value": "ignored" },
              "masterVersion": "02.50",
              "localizedParameters": {
                "defaultLanguage": 7,
                "en-US": { "titleName": false }
              }
            }
            """;

        var metadata = Read(json);

        Assert.Equal((null, null, "02.50"), metadata);
    }

    [Fact]
    public void DefaultLanguageTitleTakesPriorityOverEnglishFallback()
    {
        const string json = """
            {
              "titleId": "PPSA00001",
              "contentVersion": "01.20",
              "localizedParameters": {
                "defaultLanguage": "nl-NL",
                "nl-NL": { "titleName": "Nederlandse titel" },
                "en-US": { "titleName": "English title" }
              }
            }
            """;

        var metadata = Read(json);

        Assert.Equal(("Nederlandse titel", "PPSA00001", "01.20"), metadata);
    }

    [Fact]
    public void TypedMetadataIncludesContentIdentity()
    {
        const string json = """
            {
              "titleId": "PPSA00001",
              "contentId": "EP0001-PPSA00001_00-SHARPEMUTEST0001",
              "contentVersion": "01.20",
              "localizedParameters": {
                "defaultLanguage": "en-US",
                "en-US": { "titleName": "Synthetic title" }
              }
            }
            """;

        var metadata = Ps5ParamJsonReader.TryReadApplicationMetadata(Encoding.UTF8.GetBytes(json));

        Assert.Equal("Synthetic title", metadata.Title);
        Assert.Equal("PPSA00001", metadata.TitleId);
        Assert.Equal("EP0001-PPSA00001_00-SHARPEMUTEST0001", metadata.ContentId);
        Assert.Equal("01.20", metadata.Version);
    }

    [Fact]
    public void MissingDefaultLanguageTitleFallsBackToEnglish()
    {
        const string json = """
            {
              "localizedParameters": {
                "defaultLanguage": "fr-FR",
                "fr-FR": { "titleName": 5 },
                "en-US": { "titleName": "English fallback" }
              }
            }
            """;

        var metadata = Read(json);

        Assert.Equal(("English fallback", null, null), metadata);
    }

    [Fact]
    public void MalformedRootLocalizationFallsBackToDiscMetadata()
    {
        const string json = """
            {
              "localizedParameters": [],
              "disc": {
                "localizedParameters": {
                  "defaultLanguage": "en-US",
                  "en-US": { "titleName": "Disc title" }
                }
              }
            }
            """;

        var metadata = Read(json);

        Assert.Equal(("Disc title", null, null), metadata);
    }

    [Fact]
    public void FileSystemReadFailureReturnsEmptyMetadata()
    {
        var fileSystem = new StubFileSystem(exists: true, readSucceeds: false, []);

        var metadata = Ps5ParamJsonReader.TryReadPs5Param(fileSystem, "sce_sys/param.json");

        Assert.Equal((null, null, null), metadata);
    }

    [Fact]
    public void FileSystemBytesUseTheSameValidatedParser()
    {
        var data = Encoding.UTF8.GetBytes("""
            { "titleId": "PPSA00002", "targetContentVersion": "03.00" }
            """);
        var fileSystem = new StubFileSystem(exists: true, readSucceeds: true, data);

        var metadata = Ps5ParamJsonReader.TryReadPs5Param(fileSystem, "sce_sys/param.json");

        Assert.Equal((null, "PPSA00002", "03.00"), metadata);
    }

    [Fact]
    public void Utf8BomIsAccepted()
    {
        var json = Encoding.UTF8.GetBytes("""
            { "titleId": "PPSA00003", "contentVersion": "04.00" }
            """);
        var data = new byte[3 + json.Length];
        Encoding.UTF8.Preamble.CopyTo(data);
        json.CopyTo(data, 3);

        var metadata = Ps5ParamJsonReader.TryReadPs5Param(data);

        Assert.Equal((null, "PPSA00003", "04.00"), metadata);
    }

    [Fact]
    public void FirstAvailableLocalizationIsUsedWhenPreferredLanguagesAreMissing()
    {
        const string json = """
            {
              "localizedParameters": {
                "defaultLanguage": "fr-FR",
                "ja-JP": { "titleName": "Japanese fallback" }
              }
            }
            """;

        var metadata = Read(json);

        Assert.Equal(("Japanese fallback", null, null), metadata);
    }

    private static (string? Title, string? TitleId, string? Version) Read(string json) =>
        Ps5ParamJsonReader.TryReadPs5Param(Encoding.UTF8.GetBytes(json));

    private sealed class StubFileSystem(bool exists, bool readSucceeds, byte[] contents) : IFileSystem
    {
        public bool Exists(string path) => exists;

        public bool TryReadAllBytes(string path, out byte[] data)
        {
            data = contents;
            return readSucceeds;
        }
    }
}
