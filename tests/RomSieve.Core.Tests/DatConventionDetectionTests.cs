using System.Globalization;
using System.IO;
using System.IO.Hashing;
using System.Text;
using RomSieve.Core.Models;
using RomSieve.Core.Tests.Fixtures;

namespace RomSieve.Core.Tests;

/// <summary>
/// Convention of a DAT that names none (no keyword in its header, file names of no known grammar): it is analysed once
/// per run, and not again for every file classified, since the analysis goes through every game of the DAT.
/// </summary>
public sealed class DatConventionDetectionTests : IDisposable
{
    private const int FileCount = 30;

    private readonly TempDirectoryFixture _temp = new();
    private readonly CoreTestServices _services = new("en");

    public void Dispose() => _temp.Dispose();

    private static string Crc(string text) => Crc32.HashToUInt32(Encoding.UTF8.GetBytes(text)).ToString("x8");

    [Fact]
    public async Task UnknownConvention_ShouldBeAnalysedOncePerRun()
    {
        var games = new StringBuilder();
        for (var i = 0; i < FileCount; i++)
        {
            var name = $"file{i:00}.bin";
            var content = $"content {i}";
            _temp.WriteText(Path.Combine("src", name), content);
            games.Append(CultureInfo.InvariantCulture, $"""<game name="file{i:00}"><description>file {i}</description><rom name="{name}" size="{content.Length}" crc="{Crc(content)}"/></game>""");
        }

        var dat = _temp.WriteText("collection.dat", $"""
            <?xml version="1.0"?>
            <datafile>
              <header><name>My collection</name><description>My collection</description><version>1</version></header>
              {games}
            </datafile>
            """);

        var result = await _services.Processor.ExecuteAsync(
            new SortConfig { SourceDir = _temp.GetFilePath("src"), DestinationDir = _temp.GetFilePath("dst"), DatFile = dat },
            isDryRun: true);

        result.DatCatalog!.DetectConvention().Should().Be(NamingConvention.Auto, "the DAT names no convention");
        result.Items.Should().HaveCount(FileCount);
        result.Items.Should().OnlyContain(i => i.DatGameName != null, "every file is identified by the DAT");

        // A few times per run (reading the DAT, the steps that need it, the assertion above), never once per file
        result.DatCatalog.ConventionAnalysisCount.Should().BeLessThan(FileCount / 3);
    }
}
