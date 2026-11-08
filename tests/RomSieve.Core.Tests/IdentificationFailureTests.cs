using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.IO.Hashing;
using System.Text;
using RomSieve.Core.Models;
using RomSieve.Core.Services;
using RomSieve.Core.Tests.Fixtures;

namespace RomSieve.Core.Tests;

/// <summary>
/// Files the identification sets aside whatever their name (<see cref="ProcessItem.IdentificationFailure"/>): CRC32
/// unknown to the DAT, SHA-1 different from the DAT, file damaged in its archive, invalid CHD. Their name may follow the
/// convention and give the key of an identified game: the recalculation of a game (a 1G1R winner forced in the table)
/// must not turn them into releases of that game, as the simulation never does.
/// </summary>
public sealed class IdentificationFailureTests : IDisposable
{
    private const string AsteroidsUsa = "Asteroids (USA)";
    private const string AsteroidsAftermarket = "Asteroids (World) (Aftermarket) (Unl)";
    private const string Alpha = "Alpha (1990)(Ocean)";

    private static readonly byte[] AsteroidsData = Encoding.ASCII.GetBytes("ASTEROIDS 7800 USA");
    private static readonly byte[] AftermarketData = Encoding.ASCII.GetBytes("ASTEROIDS 7800 AFTERMARKET");
    private static readonly byte[] AlphaData = Encoding.ASCII.GetBytes("ALPHA DISK, STORED WITHOUT COMPRESSION");

    private readonly TempDirectoryFixture _temp = new();
    private readonly CoreTestServices _services = new("en");

    public void Dispose() => _temp.Dispose();

    private static string Crc(byte[] data) => Crc32.HashToUInt32(data).ToString("x8", CultureInfo.InvariantCulture);

    private string WriteDat(string name, string homepage, params (string Game, string Rom, byte[] Data, string? Sha1)[] roms) =>
        _temp.WriteText("test.dat", $"""
            <?xml version="1.0"?>
            <datafile>
              <header><name>{name}</name><description>{name}</description><homepage>{homepage}</homepage></header>
            {string.Concat(roms.Select(r => $"""
              <game name="{r.Game}"><description>{r.Game}</description>
                <rom name="{r.Rom}" size="{r.Data.Length}" crc="{Crc(r.Data)}"{(r.Sha1 is null ? string.Empty : $" sha1=\"{r.Sha1}\"")}/>
              </game>

            """))}
            </datafile>
            """);

    private SortConfig Config(string? dat = null, Action<SortConfig>? configure = null)
    {
        var config = new SortConfig
        {
            SourceDir = _temp.GetFilePath("src"),
            DestinationDir = _temp.GetFilePath("dst"),
            DatFile = dat,
            Pattern = string.Empty
        };
        configure?.Invoke(config);
        return config;
    }

    /// <summary>
    /// Recalculates the game of <paramref name="item"/> as the table does when a 1G1R winner is forced: every conforming
    /// file of its game key, with all the items of the run.
    /// </summary>
    private List<ProcessItem> RecalculateGameOf(ProcessItem item, SortConfig config, SortExecutionResult result)
    {
        var key = _services.FilterService.SerializeCanonicalGameKey(item.Parsed);
        var gameItems = result.Items
            .Where(it => it.Parsed.IsConform && _services.FilterService.SerializeCanonicalGameKey(it.Parsed) == key)
            .ToList();
        _services.Processor.RecalculateGameItems(gameItems, config, result.DatCatalog, result.Items);
        return gameItems;
    }

    private static void ShouldStaySetAside(ProcessItem item, string reasonKey)
    {
        item.Status.Should().Be(FileStatus.Unmatched, "no filter makes a file the identification set aside a release");
        item.StatusReason!.Key.Should().Be(reasonKey);
        item.Is1G1RWinner.Should().BeFalse();
        SelectionFilter.BelongsToGameUnit(item).Should().BeFalse("a partial sort chooses it on its own, apart from the game");
    }

    [Fact]
    public async Task FileNotInDat_WithTheKeyOfAnIdentifiedGame_ShouldStayNonConformingWhenTheGameIsRecalculated()
    {
        // Issue #2: a No-Intro name, same title as a game of the DAT, but a CRC32 the DAT does not know
        _temp.WriteBytes(Path.Combine("src", AsteroidsUsa + ".a78"), AsteroidsData);
        _temp.WriteBytes(Path.Combine("src", AsteroidsAftermarket + ".a78"), AftermarketData);
        var config = Config(
            WriteDat("Atari - 7800", "No-Intro", (AsteroidsUsa, AsteroidsUsa + ".a78", AsteroidsData, null)),
            c =>
            {
                c.NamingConvention = NamingConvention.NoIntro;
                c.Filters.OneGameOneRom = true;
                c.Filters.PriorityCountries = ["WO", "US"];
            });

        var result = await _services.Processor.ExecuteAsync(config, isDryRun: true);

        var identified = result.Items.Single(it => it.DisplayName == AsteroidsUsa + ".a78");
        var notInDat = result.Items.Single(it => it.DisplayName == AsteroidsAftermarket + ".a78");
        notInDat.Parsed.IsConform.Should().BeTrue();
        notInDat.IdentificationFailure.Should().Be(IdentificationFailure.NotInDat);
        identified.IdentificationFailure.Should().Be(IdentificationFailure.None);
        _services.FilterService.SerializeCanonicalGameKey(notInDat.Parsed)
            .Should().Be(_services.FilterService.SerializeCanonicalGameKey(identified.Parsed), "both names give the same game");
        ShouldStaySetAside(notInDat, "reason.unmatched_not_in_dat");
        identified.Is1G1RWinner.Should().BeTrue();

        var gameItems = RecalculateGameOf(identified, config, result);

        gameItems.Should().HaveCount(2);
        ShouldStaySetAside(notInDat, "reason.unmatched_not_in_dat");
        identified.Status.Should().Be(FileStatus.Valid);
        identified.Is1G1RWinner.Should().BeTrue("the World file the DAT does not know cannot win the game");
    }

    [Fact]
    public async Task Sha1Mismatch_ShouldStayNonConformingWhenTheGameIsRecalculated()
    {
        _temp.WriteBytes(Path.Combine("src", Alpha + ".adf"), AlphaData);
        var config = Config(
            WriteDat("Commodore Amiga - Games - [ADF]", "TOSEC", (Alpha, Alpha + ".adf", AlphaData, new string('a', 40))),
            c => c.VerifySha1 = true);

        var result = await _services.Processor.ExecuteAsync(config, isDryRun: true);

        var item = result.Items.Single();
        item.Parsed.IsConform.Should().BeTrue();
        item.IdentificationFailure.Should().Be(IdentificationFailure.Sha1Mismatch);

        RecalculateGameOf(item, config, result);

        ShouldStaySetAside(item, "reason.sha1_mismatch");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FileDamagedInItsArchive_ShouldStayNonConformingWhenTheGameIsRecalculated(bool withDat)
    {
        var zip = _temp.GetFilePath(Path.Combine("src", "pack.zip"));
        Directory.CreateDirectory(Path.GetDirectoryName(zip)!);
        using (var archive = ZipFile.Open(zip, ZipArchiveMode.Create))
        {
            using var stream = archive.CreateEntry(Alpha + ".adf", CompressionLevel.NoCompression).Open();
            stream.Write(AlphaData);
        }

        // Altered data under the CRC32 of the right data: the member is damaged
        var bytes = File.ReadAllBytes(zip);
        bytes[bytes.AsSpan().IndexOf(AlphaData)] ^= 0xFF;
        File.WriteAllBytes(zip, bytes);
        var config = Config(
            withDat ? WriteDat("Commodore Amiga - Games - [ADF]", "TOSEC", (Alpha, Alpha + ".adf", AlphaData, null)) : null,
            c => c.VerifyArchiveContents = true);

        var result = await _services.Processor.ExecuteAsync(config, isDryRun: true);

        var item = result.Items.Single();
        item.Parsed.IsConform.Should().BeTrue();
        item.IdentificationFailure.Should().Be(IdentificationFailure.DamagedArchive);

        RecalculateGameOf(item, config, result);

        ShouldStaySetAside(item, "reason.archive_member_damaged");
    }

    [Fact]
    public async Task InvalidChd_ShouldStayNonConformingWhenTheGameIsRecalculated()
    {
        _temp.WriteBytes(Path.Combine("src", Alpha + ".chd"), ChdTestFile.Create("alpha")[..50]);
        var config = Config();

        var result = await _services.Processor.ExecuteAsync(config, isDryRun: true);

        var item = result.Items.Single();
        item.Parsed.IsConform.Should().BeTrue();
        item.IdentificationFailure.Should().Be(IdentificationFailure.InvalidChd);

        RecalculateGameOf(item, config, result);

        ShouldStaySetAside(item, "reason.chd_invalid");
    }

    [Fact]
    public async Task FileOfTheDatOrWithoutDat_ShouldHaveNoIdentificationFailure()
    {
        _temp.WriteBytes(Path.Combine("src", Alpha + ".adf"), AlphaData);

        var withDat = await _services.Processor.ExecuteAsync(
            Config(WriteDat("Commodore Amiga - Games - [ADF]", "TOSEC", (Alpha, Alpha + ".adf", AlphaData, null))), isDryRun: true);
        var withoutDat = await _services.Processor.ExecuteAsync(Config(), isDryRun: true);

        withDat.Items.Single().IdentificationFailure.Should().Be(IdentificationFailure.None);
        withoutDat.Items.Single().IdentificationFailure.Should().Be(IdentificationFailure.None, "without a DAT, the name decides");
    }
}
