// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Collections.Concurrent;
using System.Text;
using SharpEmu.SourceGenerators;
using Xunit;

namespace SharpEmu.SourceGenerators.Tests;

public sealed class GenerateAerolibBinaryTaskTests
{
    [Fact]
    public async Task ConcurrentGeneratorsPublishOneCompleteCatalog()
    {
        var temporaryDirectory = Path.Combine(
            Path.GetTempPath(),
            "sharpemu-aerolib-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temporaryDirectory);

        try
        {
            var namesFile = Path.Combine(temporaryDirectory, "names.txt");
            var outputFile = Path.Combine(temporaryDirectory, "aerolib.bin");
            var names = Enumerable.Range(0, 4096)
                .Select(index => $"sceSyntheticExport{index:D4}")
                .ToArray();
            File.WriteAllLines(namesFile, names);

            const int generatorCount = 12;
            using var start = new ManualResetEventSlim(initialState: false);
            var results = new ConcurrentBag<bool>();
            var workers = Enumerable.Range(0, generatorCount)
                .Select(_ => Task.Run(() =>
                {
                    start.Wait();
                    results.Add(new GenerateAerolibBinaryTask
                    {
                        NamesFile = namesFile,
                        OutputFile = outputFile,
                    }.Execute());
                }))
                .ToArray();

            start.Set();
            await Task.WhenAll(workers);

            Assert.Equal(generatorCount, results.Count);
            Assert.All(results, Assert.True);
            Assert.Empty(Directory.EnumerateFiles(
                temporaryDirectory,
                "aerolib.bin.*.tmp"));

            using var stream = File.OpenRead(outputFile);
            using var reader = new BinaryReader(stream, Encoding.UTF8);
            Assert.Equal((uint)names.Length, reader.ReadUInt32());
            foreach (var expectedName in names)
            {
                AssertCatalogEntry(reader, expectedName);
            }

            Assert.Equal(stream.Length, stream.Position);
        }
        finally
        {
            Directory.Delete(temporaryDirectory, recursive: true);
        }
    }

    [Fact]
    public void RegenerationAtomicallyReplacesExistingCatalog()
    {
        var temporaryDirectory = Path.Combine(
            Path.GetTempPath(),
            "sharpemu-aerolib-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temporaryDirectory);

        try
        {
            var namesFile = Path.Combine(temporaryDirectory, "names.txt");
            var outputFile = Path.Combine(temporaryDirectory, "aerolib.bin");
            File.WriteAllLines(namesFile, ["sceFirstExport"]);
            var generator = new GenerateAerolibBinaryTask
            {
                NamesFile = namesFile,
                OutputFile = outputFile,
            };

            Assert.True(generator.Execute());
            File.WriteAllLines(namesFile, ["sceSecondExport", "sceThirdExport"]);
            Assert.True(generator.Execute());

            using var stream = File.OpenRead(outputFile);
            using var reader = new BinaryReader(stream, Encoding.UTF8);
            Assert.Equal(2u, reader.ReadUInt32());
            AssertCatalogEntry(reader, "sceSecondExport");
            AssertCatalogEntry(reader, "sceThirdExport");
            Assert.Equal(stream.Length, stream.Position);
        }
        finally
        {
            Directory.Delete(temporaryDirectory, recursive: true);
        }
    }

    private static void AssertCatalogEntry(BinaryReader reader, string expectedName)
    {
        var nidLength = reader.ReadByte();
        var nid = Encoding.UTF8.GetString(reader.ReadBytes(nidLength));
        var nameLength = reader.ReadUInt16();
        var name = Encoding.UTF8.GetString(reader.ReadBytes(nameLength));

        Assert.Equal(Ps5Nid.Compute(expectedName), nid);
        Assert.Equal(expectedName, name);
    }
}
