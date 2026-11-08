using System.Globalization;
using System.IO;
using System.IO.Hashing;
using System.Text;
using RomSieve.Core.Models;
using RomSieve.Core.Services;
using RomSieve.Core.Tests.Fixtures;

namespace RomSieve.Core.Tests;

/// <summary>
/// The content filters applied to the files missing from the DAT (<see cref="FilterConfig.FilterNotInDat"/>, issue #2):
/// a file whose CRC32 the DAT does not know and whose name the filters exclude is excluded, left in the source, instead
/// of being isolated as non-conforming; any other file the DAT does not know stays non-conforming. Such a file is never a
/// release of the game its name gives.
/// </summary>
public sealed class NotInDatFilterTests : IDisposable
{
    private const string AsteroidsUsa = "Asteroids (USA).a78";
    private const string AsteroidsDemo = "Asteroids (World) (Demo) (Aftermarket) (Unl).a78";
    private const string Centipede = "Centipede (World) (Aftermarket) (Unl).a78";
    private const string Readme = "readme.txt";

    private static readonly byte[] AsteroidsData = Encoding.ASCII.GetBytes("ASTEROIDS 7800 USA");

    private readonly TempDirectoryFixture _temp = new();
    private readonly CoreTestServices _services = new("en");

    public NotInDatFilterTests()
    {
        _temp.WriteBytes(Path.Combine("src", AsteroidsUsa), AsteroidsData);
        _temp.WriteText(Path.Combine("src", AsteroidsDemo), "ASTEROIDS DEMO");
        _temp.WriteText(Path.Combine("src", Centipede), "CENTIPEDE");
        _temp.WriteText(Path.Combine("src", Readme), "not a ROM");
    }

    public void Dispose() => _temp.Dispose();

    private static string Crc(byte[] data) => Crc32.HashToUInt32(data).ToString("x8", CultureInfo.InvariantCulture);

    private string WriteDat(string? sha1 = null) => _temp.WriteText("atari7800.dat", $"""
        <?xml version="1.0"?>
        <datafile>
          <header><name>Atari - 7800</name><description>Atari - 7800</description><homepage>No-Intro</homepage></header>
          <game name="Asteroids (USA)"><description>Asteroids (USA)</description>
            <rom name="{AsteroidsUsa}" size="{AsteroidsData.Length}" crc="{Crc(AsteroidsData)}"{(sha1 is null ? string.Empty : $" sha1=\"{sha1}\"")}/>
          </game>
        </datafile>
        """);

    private SortConfig Config(bool filterNotInDat = true, Action<SortConfig>? configure = null)
    {
        var config = new SortConfig
        {
            SourceDir = _temp.GetFilePath("src"),
            DestinationDir = _temp.GetFilePath("dst"),
            DatFile = WriteDat(),
            NamingConvention = NamingConvention.NoIntro,
            Pattern = string.Empty
        };
        config.Filters.NoIntroExcludeDemos = true;
        config.Filters.FilterNotInDat = filterNotInDat;
        configure?.Invoke(config);
        return config;
    }

    private static ProcessItem Item(SortExecutionResult result, string name) => result.Items.Single(it => it.DisplayName == name);

    private IEnumerable<string> DestinationFiles() =>
        Directory.Exists(_temp.GetFilePath("dst"))
            ? Directory.EnumerateFiles(_temp.GetFilePath("dst"), "*", SearchOption.AllDirectories)
                .Where(p => !DestinationGuard.IsGuardPath(p))
                .Select(p => Path.GetRelativePath(_temp.GetFilePath("dst"), p).Replace('\\', '/'))
                .Order(StringComparer.Ordinal)
            : [];

    [Fact]
    public async Task FileMissingFromTheDat_ThatTheFiltersExclude_ShouldBeExcludedWithBothReasons()
    {
        var result = await _services.Processor.ExecuteAsync(Config(), isDryRun: true);

        var demo = Item(result, AsteroidsDemo);
        demo.Status.Should().Be(FileStatus.Excluded);
        demo.IdentificationFailure.Should().Be(IdentificationFailure.NotInDat);
        demo.StatusReason!.Key.Should().Be("reason.not_in_dat_excluded");
        _services.I18n.Format(demo.StatusReason).Should().Be("Demo version excluded (file missing from the DAT)");
        new LocalizationService("fr").Format(demo.StatusReason).Should().Be("Version Démo exclue (fichier absent du DAT)");

        var centipede = Item(result, Centipede);
        centipede.Status.Should().Be(FileStatus.Unmatched, "the filters keep it: it stays non-conforming");
        centipede.StatusReason!.Key.Should().Be("reason.unmatched_not_in_dat");
        var readme = Item(result, Readme);
        readme.Parsed.IsConform.Should().BeFalse();
        readme.Status.Should().Be(FileStatus.Unmatched, "a non-conforming name is never filtered");
        Item(result, AsteroidsUsa).Status.Should().Be(FileStatus.Valid);
    }

    [Fact]
    public async Task OptionOff_ShouldLeaveEveryFileMissingFromTheDatNonConforming()
    {
        var result = await _services.Processor.ExecuteAsync(Config(filterNotInDat: false), isDryRun: true);

        var demo = Item(result, AsteroidsDemo);
        demo.Status.Should().Be(FileStatus.Unmatched);
        demo.StatusReason!.Key.Should().Be("reason.unmatched_not_in_dat");
        demo.IdentificationFailure.Should().Be(IdentificationFailure.NotInDat);
    }

    [Fact]
    public async Task Counts_ShouldTakeTheExcludedFileFromTheNonConformingOnes_WithoutChangingTheDatCompletion()
    {
        var on = await _services.Processor.ExecuteAsync(Config(), isDryRun: true);
        var off = await _services.Processor.ExecuteAsync(Config(filterNotInDat: false), isDryRun: true);

        on.Stats.ExcludedCount.Should().Be(1);
        on.Stats.UnmatchedCount.Should().Be(2);
        off.Stats.ExcludedCount.Should().Be(0);
        off.Stats.UnmatchedCount.Should().Be(3);
        on.Stats.ValidCount.Should().Be(off.Stats.ValidCount);
        on.Stats.DatMatchedCount.Should().Be(off.Stats.DatMatchedCount).And.Be(1);
        on.Stats.DatCompletionPct.Should().Be(off.Stats.DatCompletionPct);
        on.MissingRoms.Should().BeEquivalentTo(off.MissingRoms);

        _services.Processor.RecomputeStats(on.Items.ToList(), on.Stats).ExcludedCount.Should().Be(1);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Sort_ShouldLeaveTheExcludedFileInTheSource_WhetherTheNonConformingOnesAreIsolatedOrNot(bool isolate)
    {
        var result = await _services.Processor.ExecuteAsync(Config(configure: c => c.IsolateUnmatched = isolate), isDryRun: false);

        var demo = Item(result, AsteroidsDemo);
        demo.Status.Should().Be(FileStatus.Excluded);
        demo.StatusReason!.Key.Should().Be("reason.not_in_dat_excluded", "the isolation of the non-conforming files does not concern it");
        demo.IsNotIsolated.Should().BeFalse();
        File.Exists(_temp.GetFilePath(Path.Combine("src", AsteroidsDemo))).Should().BeTrue();
        DestinationFiles().Should().NotContain(f => f.EndsWith(AsteroidsDemo, StringComparison.Ordinal));
        if (isolate)
        {
            DestinationFiles().Should().Contain($"{PathOrganizerService.UnmatchedFolderName}/{Centipede}");
        }
    }

    [Fact]
    public async Task Sha1Mismatch_ShouldStayNonConformingWhateverItsName()
    {
        // The CRC32 of a game of the DAT, another SHA-1: an anomaly to show, never filtered
        _temp.WriteBytes(Path.Combine("src", "Asteroids (USA) (Demo).a78"), AsteroidsData);
        File.Delete(_temp.GetFilePath(Path.Combine("src", AsteroidsUsa)));
        var config = Config(configure: c =>
        {
            c.DatFile = WriteDat(sha1: new string('a', 40));
            c.VerifySha1 = true;
        });

        var result = await _services.Processor.ExecuteAsync(config, isDryRun: true);

        var item = Item(result, "Asteroids (USA) (Demo).a78");
        item.Status.Should().Be(FileStatus.Unmatched);
        item.StatusReason!.Key.Should().Be("reason.sha1_mismatch");
        item.IdentificationFailure.Should().Be(IdentificationFailure.Sha1Mismatch);
    }

    [Fact]
    public async Task ExcludedFile_ShouldBeNoReleaseOfTheGameItsNameGives_EvenForcedAsTheWinner()
    {
        var first = await _services.Processor.ExecuteAsync(Config(configure: c => c.Filters.OneGameOneRom = true), isDryRun: true);
        var key = _services.FilterService.SerializeCanonicalGameKey(Item(first, AsteroidsDemo).Parsed);
        key.Should().Be(_services.FilterService.SerializeCanonicalGameKey(Item(first, AsteroidsUsa).Parsed), "both names give the game Asteroids");
        Item(first, AsteroidsUsa).Status.Should().Be(FileStatus.Valid);
        Item(first, AsteroidsDemo).EliminationDetail.Should().BeNull("it never entered the 1G1R");

        // The excluded file forced as the winner of its game, as a choice saved in the table would do
        var forced = await _services.Processor.ExecuteAsync(
            Config(configure: c =>
            {
                c.Filters.OneGameOneRom = true;
                c.Filters.Manual1G1RWinners[key] = AsteroidsDemo;
            }),
            isDryRun: true);

        var demo = Item(forced, AsteroidsDemo);
        demo.Status.Should().Be(FileStatus.Excluded);
        demo.StatusReason!.Key.Should().Be("reason.not_in_dat_excluded");
        demo.Is1G1RWinner.Should().BeFalse();
        Item(forced, AsteroidsUsa).Status.Should().Be(FileStatus.Valid);
    }

    [Fact]
    public async Task ExcludedFile_ShouldNotBePromotedWithTheForcedReleaseItsNameShares()
    {
        // The cracked release of the DAT, excluded by the filters, is forced by hand. Another dump under the same name,
        // that the DAT does not know, is the same release by its name: it stays excluded
        const string alpha = "Alpha (1990)(Ocean)";
        const string crack = alpha + "[cr].adf";
        var alphaData = Encoding.ASCII.GetBytes("ALPHA DISK");
        var crackData = Encoding.ASCII.GetBytes("ALPHA CRACK");
        Directory.Delete(_temp.GetFilePath("src"), recursive: true);
        _temp.WriteBytes(Path.Combine("src", alpha + ".adf"), alphaData);
        var identifiedCrack = _temp.WriteBytes(Path.Combine("src", crack), crackData);
        var otherCrack = _temp.WriteText(Path.Combine("src", "other", crack), "ANOTHER ALPHA CRACK");
        var dat = _temp.WriteText("amiga.dat", $"""
            <?xml version="1.0"?>
            <datafile>
              <header><name>Commodore Amiga - Games - [ADF]</name><description>TOSEC</description><homepage>TOSEC</homepage></header>
              <game name="{alpha}"><description>{alpha}</description><rom name="{alpha}.adf" size="{alphaData.Length}" crc="{Crc(alphaData)}"/></game>
              <game name="{alpha}[cr]" cloneof="{alpha}"><description>{alpha}[cr]</description><rom name="{crack}" size="{crackData.Length}" crc="{Crc(crackData)}"/></game>
            </datafile>
            """);
        ProcessItem At(SortExecutionResult result, string path) => result.Items.Single(it => it.SourcePath == path);
        SortConfig AlphaConfig(Dictionary<string, string>? winners = null)
        {
            var config = new SortConfig
            {
                SourceDir = _temp.GetFilePath("src"),
                DestinationDir = _temp.GetFilePath("dst"),
                DatFile = dat,
                Pattern = string.Empty
            };
            config.Filters.OneGameOneRom = true;
            foreach (var (game, winner) in winners ?? [])
            {
                config.Filters.Manual1G1RWinners[game] = winner;
            }

            return config;
        }

        var first = await _services.Processor.ExecuteAsync(AlphaConfig(), isDryRun: true);
        var key = _services.FilterService.SerializeCanonicalGameKey(Item(first, alpha + ".adf").Parsed);
        At(first, identifiedCrack).Status.Should().Be(FileStatus.Excluded);
        At(first, otherCrack).Status.Should().Be(FileStatus.Excluded);
        At(first, otherCrack).IdentificationFailure.Should().Be(IdentificationFailure.NotInDat);

        var forced = await _services.Processor.ExecuteAsync(AlphaConfig(new() { [key] = crack }), isDryRun: true);

        At(forced, identifiedCrack).Status.Should().Be(FileStatus.Valid, "the release forced by hand is kept");
        var other = At(forced, otherCrack);
        other.Status.Should().Be(FileStatus.Excluded, "a file missing from the DAT is never promoted with the forced release");
        other.StatusReason!.Key.Should().Be("reason.not_in_dat_excluded");
        Item(forced, alpha + ".adf").Status.Should().Be(FileStatus.Excluded);
    }

    [Fact]
    public async Task Recalculation_ShouldKeepTheExcludedFileApart_AndFollowTheOption()
    {
        var config = Config(configure: c => c.Filters.OneGameOneRom = true);
        var result = await _services.Processor.ExecuteAsync(config, isDryRun: true);
        var demo = Item(result, AsteroidsDemo);
        var usa = Item(result, AsteroidsUsa);
        var game = new List<ProcessItem> { usa, demo };

        _services.Processor.RecalculateGameItems(game, config, result.DatCatalog, result.Items);

        demo.Status.Should().Be(FileStatus.Excluded);
        demo.StatusReason!.Key.Should().Be("reason.not_in_dat_excluded");
        usa.Status.Should().Be(FileStatus.Valid);

        // The option turned off since the simulation: the file is non-conforming again, as a new simulation would say
        config.Filters.FilterNotInDat = false;
        _services.Processor.RecalculateGameItems(game, config, result.DatCatalog, result.Items);

        demo.Status.Should().Be(FileStatus.Unmatched);
        demo.StatusReason!.Key.Should().Be("reason.unmatched_not_in_dat");
        SelectionFilter.BelongsToGameUnit(demo).Should().BeFalse();
        usa.Status.Should().Be(FileStatus.Valid);

        config.Filters.FilterNotInDat = true;
        _services.Processor.RecalculateGameItems(game, config, result.DatCatalog, result.Items);
        demo.Status.Should().Be(FileStatus.Excluded);
    }

    [Fact]
    public async Task Exports_ShouldGiveTheNewReason_AndTheCollectionDatShouldLeaveTheFileOut()
    {
        var result = await _services.Processor.ExecuteAsync(Config(), isDryRun: true);
        var json = _temp.GetFilePath("report.json");
        var csv = _temp.GetFilePath("report.csv");
        var dat = _temp.GetFilePath("collection.xml");

        await _services.Processor.ExportReportToJsonAsync(result.Items, result.Stats, json);
        await _services.Processor.ExportReportToCsvAsync(result.Items, csv);
        var collection = await _services.Processor.ExportDatAsync(result.Items, dat, datCatalog: result.DatCatalog);

        File.ReadAllText(json).Should().Contain("Demo version excluded (file missing from the DAT)");
        File.ReadAllText(csv).Should().Contain("Demo version excluded (file missing from the DAT)");
        collection.Entries.Select(e => e.Name).Should().Equal(AsteroidsUsa);
        File.ReadAllText(dat).Should().NotContain("Demo");
    }
}
