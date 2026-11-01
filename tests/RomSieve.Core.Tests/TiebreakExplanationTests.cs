using System.IO;
using FluentAssertions;
using RomSieve.Core.Models;
using RomSieve.Core.Services;
using RomSieve.Core.Tests.Fixtures;
using Xunit;

namespace RomSieve.Core.Tests;

/// <summary>
/// The 1G1R elimination text names the criterion of the cascade that decided between the winner and each loser
/// (<see cref="OneGameOneRomResolver.Explain1G1RTiebreak"/>).
/// </summary>
public sealed class TiebreakExplanationTests : IDisposable
{
    private readonly TempDirectoryFixture _temp = new();

    public void Dispose() => _temp.Dispose();

    private static readonly Dictionary<string, (string[] Files, Action<FilterConfig> Configure)> Cases = new()
    {
        ["lang"] = (["Game (1990)(Pub)(FR).adf", "Game (1990)(Pub)(DE).adf"], f => f.PriorityLanguages = ["fr", "de"]),
        ["country_first"] = (["Game (1990)(Pub)(FR).adf", "Game (1990)(Pub)(US).adf"], f =>
        {
            f.RegionPriorityMode = RegionPriorityMode.CountryFirst;
            f.PriorityLanguages = ["en", "fr"];
            f.PriorityCountries = ["FR", "US"];
        }
        ),
        ["country"] = (["Game (1990)(Pub)(GB).adf", "Game (1990)(Pub)(US).adf"], f =>
        {
            f.PriorityLanguages = ["en"];
            f.PriorityCountries = ["GB", "US"];
        }
        ),
        ["verified"] = (["Game (1990)(Pub)(FR)[!].adf", "Game (1990)(Pub)(FR)[cr XYZ].adf"], f =>
        {
            f.ExcludeModifications = false;
            f.PriorityLanguages = ["fr"];
        }
        ),
        ["good_dump"] = (["Game (1990)(Pub)(FR).adf", "Game (1990)(Pub)(FR)[b].adf"], f =>
        {
            f.ExcludeBadDumps = false;
            f.PriorityLanguages = ["fr"];
        }
        ),
        ["original"] = (["Game (1990)(Pub)(FR).adf", "Game (1990)(Pub)(FR)[cr XYZ].adf"], f =>
        {
            f.ExcludeModifications = false;
            f.PriorityLanguages = ["fr"];
        }
        ),
        ["less_modified"] = (["Game (1990)(Pub)(FR)[t +2].adf", "Game (1990)(Pub)(FR)[cr XYZ][t +2][h ABC].adf"], f =>
        {
            f.ExcludeModifications = false;
            f.PriorityLanguages = ["fr"];
        }
        ),
        ["video"] = (["Game (1990)(Pub)(PAL)(FR).adf", "Game (1990)(Pub)(NTSC)(FR).adf"], f =>
        {
            f.PreferredVideo = VideoPreference.Pal;
            f.PriorityLanguages = ["fr"];
        }
        ),
        ["media"] = (["Game (1990)(Pub)(FR).tzx", "Game (1990)(Pub)(FR).adf"], f =>
        {
            f.PreferredMediaType = MediaPreference.Tape;
            f.PriorityLanguages = ["fr"];
        }
        ),
        ["fidelity"] = (["Game (1990)(Pub)(FR).ipf", "Game (1990)(Pub)(FR).adf"], f => f.PriorityLanguages = ["fr"]),
        ["mono"] = (["Game (1990)(Pub)(FR).adf", "Game (1990)(Pub)(FR)(fr-de).adf"], f => f.PriorityLanguages = ["fr"]),
        ["multi"] = (["Game (1990)(Pub)(FR)(fr-de).adf", "Game (1990)(Pub)(FR).adf"], f =>
        {
            f.PreferMonolingual = false;
            f.PriorityLanguages = ["fr"];
        }
        ),
        ["region"] = (["Game (1990)(Pub)(US)(en).adf", "Game (1990)(Pub)(FR)(en).adf"], f => f.PriorityLanguages = ["en"]),
        ["version"] = (["Game v1.1 (1990)(Pub)(FR).adf", "Game v1.0 (1990)(Pub)(FR).adf"], f => f.PriorityLanguages = ["fr"]),
        ["version_missing"] = (["Game v1.0 (1990)(Pub)(FR).adf", "Game (1990)(Pub)(FR).adf"], f => f.PriorityLanguages = ["fr"]),
        ["date"] = (["Game (1991)(Pub)(FR).adf", "Game (1990)(Pub)(FR).adf"], f => f.PriorityLanguages = ["fr"]),
        ["complete_set"] = (
            ["Zak v1.0 (1988)(Lucas)(FR)(Disk 1 of 2).adf", "Zak v1.0 (1988)(Lucas)(FR)(Disk 2 of 2).adf", "Zak v1.1 (1988)(Lucas)(FR)(Disk 1 of 2).adf"],
            f =>
            {
                f.SmartMultidiskGrouping = false;
                f.PriorityLanguages = ["fr"];
            }
        ),
        ["complete_and_cleaner_set"] = (
            ["Zak (1988)(Lucas)(FR)(Disk 1 of 2).adf", "Zak (1988)(Lucas)(FR)(Disk 2 of 2).adf", "Zak (1988)(Lucas)(FR)(Disk 1 of 2)[cr X].adf"],
            f =>
            {
                f.ExcludeModifications = false;
                f.SmartMultidiskGrouping = false;
                f.PriorityLanguages = ["fr"];
            }
        ),
        ["name"] = (["Game (1990)(Pub)(FR).adf", "Game (1990)(Publisher)(FR).adf"], f => f.PriorityLanguages = ["fr"]),
        // For an AGA Amiga, the AGA and OCS versions of a game are one game, and the AGA one wins
        ["amiga_chipset"] = (["Game (1990)(Pub)(AGA)(FR).adf", "Game (1990)(Pub)(FR)[!].adf"], f =>
        {
            f.AmigaTarget = AmigaTarget.Aga;
            f.PriorityLanguages = ["fr"];
        }
        ),
        ["loser_with_better_language_outside_pool"] = (["Game (1990)(Pub)(US)(de).adf", "Game (1990)(Pub)(DE)(en).adf"], f =>
        {
            f.FilterCountries = true;
            f.FallbackUnmatchedCountries = false;
            f.PriorityLanguages = ["en", "de"];
            f.PriorityCountries = ["US"];
        }
        ),
        // Customized orders of the criteria (FilterConfig.TosecTiebreakOrder)
        ["custom_date_first"] = (["Game (1991)(Pub)(DE).adf", "Game (1990)(Pub)(FR).adf"], f =>
        {
            f.PriorityLanguages = ["fr", "de"];
            f.TosecTiebreakOrder = [TiebreakCriterion.Date, .. TiebreakOrder.Default(NamingConvention.Tosec).Where(c => c != TiebreakCriterion.Date)];
        }
        ),
        ["custom_country_above_language"] = (["Game (1990)(Pub)(US).adf", "Game (1990)(Pub)(FR).adf"], f =>
        {
            f.PriorityLanguages = ["fr", "en"];
            f.PriorityCountries = ["US", "FR"];
            f.TosecTiebreakOrder = [TiebreakCriterion.CountryRank, .. TiebreakOrder.Default(NamingConvention.Tosec).Where(c => c != TiebreakCriterion.CountryRank)];
        }
        ),
        ["loser_cleaner_outside_pool"] = (["Game (1990)(Pub)(US)[cr X].adf", "Game (1990)(Pub)(DE)(en)[!].adf"], f =>
        {
            f.ExcludeModifications = false;
            f.FilterCountries = true;
            f.FallbackUnmatchedCountries = false;
            f.PriorityLanguages = ["en", "de"];
            f.PriorityCountries = ["US"];
        }
        ),
    };

    public static TheoryData<string, string, string, string> Explanations() => new()
    {
        { "lang", "fr", "Game (1990)(Pub)(DE).adf", "langue FR préférée à DE" },
        { "lang", "en", "Game (1990)(Pub)(DE).adf", "FR preferred over DE" },
        { "country_first", "fr", "Game (1990)(Pub)(US).adf", "pays/région FR préféré(e) à US" },
        { "country", "fr", "Game (1990)(Pub)(US).adf", "pays/région GB préféré(e) à US" },
        { "country", "en", "Game (1990)(Pub)(US).adf", "country/region GB preferred over US" },
        // A verified dump is a better dump: the good dump decides before the original release
        { "verified", "fr", "Game (1990)(Pub)(FR)[cr XYZ].adf", "dump vérifié [!]" },
        { "verified", "en", "Game (1990)(Pub)(FR)[cr XYZ].adf", "verified dump [!]" },
        { "good_dump", "fr", "Game (1990)(Pub)(FR)[b].adf", "bon dump vs mauvais dump" },
        { "original", "fr", "Game (1990)(Pub)(FR)[cr XYZ].adf", "version originale vs modifiée" },
        { "original", "en", "Game (1990)(Pub)(FR)[cr XYZ].adf", "original release vs modified" },
        { "less_modified", "fr", "Game (1990)(Pub)(FR)[cr XYZ][t +2][h ABC].adf", "version moins altérée / plus fidèle à l'original" },
        { "video", "fr", "Game (1990)(Pub)(NTSC)(FR).adf", "standard PAL préféré" },
        { "video", "en", "Game (1990)(Pub)(NTSC)(FR).adf", "preferred video PAL" },
        { "media", "fr", "Game (1990)(Pub)(FR).adf", "format Cassettes préféré" },
        { "fidelity", "fr", "Game (1990)(Pub)(FR).adf", "format .ipf préféré à .adf" },
        { "mono", "fr", "Game (1990)(Pub)(FR)(fr-de).adf", "version unilingue dédiée" },
        { "multi", "fr", "Game (1990)(Pub)(FR).adf", "version multilingue préférée" },
        { "region", "fr", "Game (1990)(Pub)(FR)(en).adf", "région US préférée" },
        { "version", "fr", "Game v1.0 (1990)(Pub)(FR).adf", "version v1.1 > v1.0" },
        // A release without a version ranks below any number: it was said to be "v1.0", hence "v1.0 > v1.0"
        { "version_missing", "fr", "Game (1990)(Pub)(FR).adf", "version v1.0 > sans version" },
        { "version_missing", "en", "Game (1990)(Pub)(FR).adf", "version v1.0 > no version" },
        { "date", "fr", "Game (1990)(Pub)(FR).adf", "date 1991 plus récente" },
        { "date", "en", "Game (1990)(Pub)(FR).adf", "newer date 1991" },
        { "complete_set", "fr", "Zak v1.1 (1988)(Lucas)(FR)(Disk 1 of 2).adf", "Set multi-disquettes incomplet (1/2 disques trouvés)" },
        { "complete_and_cleaner_set", "fr", "Zak (1988)(Lucas)(FR)(Disk 1 of 2)[cr X].adf", "version originale vs modifiée" },
        { "name", "fr", "Game (1990)(Publisher)(FR).adf", "officielle/propre" },
        { "name", "en", "Game (1990)(Publisher)(FR).adf", "clean/official" },
        { "amiga_chipset", "fr", "Game (1990)(Pub)(FR)[!].adf", "version AGA préférée à la version OCS / ECS (Amiga AGA)" },
        { "amiga_chipset", "en", "Game (1990)(Pub)(FR)[!].adf", "AGA version preferred to the OCS / ECS version (AGA Amiga)" },
        { "loser_with_better_language_outside_pool", "fr", "Game (1990)(Pub)(DE)(en).adf", "pays/région US préféré(e) à DE" },
        // The cleaner loser was left out by the country filter: the winner is explained by its country, not by a cleanliness it lacks
        { "loser_cleaner_outside_pool", "fr", "Game (1990)(Pub)(DE)(en)[!].adf", "pays/région US préféré(e) à DE" },
        // With the date first, a newer German version beats the preferred French language
        { "custom_date_first", "fr", "Game (1990)(Pub)(FR).adf", "date 1991 plus récente" },
        { "custom_date_first", "en", "Game (1990)(Pub)(FR).adf", "newer date 1991" },
        // The order of the criteria decides even when the region mode still says "language first"
        { "custom_country_above_language", "fr", "Game (1990)(Pub)(FR).adf", "pays/région US préféré(e) à FR" },
    };

    [Theory]
    [MemberData(nameof(Explanations))]
    public async Task EliminationDetail_ShouldNameTheDecisiveCriterion(string scenario, string locale, string loser, string expected)
    {
        var (files, configure) = Cases[scenario];
        var src = _temp.CreateSubdirectory($"src_{scenario}_{locale}");
        var dst = _temp.CreateSubdirectory($"dst_{scenario}_{locale}");
        foreach (var name in files)
        {
            File.WriteAllText(Path.Combine(src, name), name);
        }

        var filters = new FilterConfig { OneGameOneRom = true };
        configure(filters);
        var services = new CoreTestServices(locale);

        var result = await services.Processor.ExecuteAsync(
            new SortConfig { SourceDir = src, DestinationDir = dst, Filters = filters },
            isDryRun: true);

        var winner = result.Items.Single(it => it.Is1G1RWinner && it.DisplayName == files[0]);
        winner.Status.Should().Be(FileStatus.Valid);
        var eliminated = result.Items.Single(it => it.DisplayName == loser);
        eliminated.Status.Should().Be(FileStatus.Excluded);
        eliminated.WinnerReference.Should().Be(files[0]);
        services.I18n.Format(eliminated.EliminationDetail).Should().Be(expected);
    }

    [Fact]
    public async Task EachConvention_ShouldFollowItsOwnOrder()
    {
        // The date first in the order of TOSEC: a newer German version beats the French one; the order of No-Intro
        // keeps the language first, which keeps the French version of the cartridge
        string[] files = ["Game (1991)(Pub)(DE).adf", "Game (1990)(Pub)(FR).adf", "Cart (Germany) (Rev 1).sfc", "Cart (France).sfc"];
        var src = _temp.CreateSubdirectory("src_per_convention");
        foreach (var name in files)
        {
            File.WriteAllText(Path.Combine(src, name), name);
        }

        var filters = new FilterConfig
        {
            OneGameOneRom = true,
            PriorityLanguages = ["fr", "de"],
            TosecTiebreakOrder = [TiebreakCriterion.Date, .. TiebreakOrder.Default(NamingConvention.Tosec).Where(c => c != TiebreakCriterion.Date)]
        };
        var services = new CoreTestServices("en");

        var result = await services.Processor.ExecuteAsync(
            new SortConfig { SourceDir = src, DestinationDir = _temp.GetFilePath("dst_per_convention"), Filters = filters },
            isDryRun: true);

        result.Items.Where(it => it.Is1G1RWinner).Select(it => it.DisplayName).Should().BeEquivalentTo("Game (1991)(Pub)(DE).adf", "Cart (France).sfc");
        result.Items.Single(it => it.DisplayName == "Cart (Germany) (Rev 1).sfc").EliminatedBy.Should().Be(TiebreakCriterion.LanguageRank);
    }

    [Fact]
    public async Task ReleasesOfDifferentConventions_ShouldBeComparedAlikeWhateverTheirOrder()
    {
        // The same game named after TOSEC and after No-Intro: the two releases are compared by one cascade, so that the
        // winner does not depend on which one is compared first
        string[] files = ["Game (1990)(Pub)(US).sfc", "Game (USA).sfc"];
        List<string>? previousWinners = null;
        foreach (var ordered in new[] { files, files.Reverse().ToArray() })
        {
            var src = _temp.CreateSubdirectory($"src_mixed_{previousWinners is null}");
            foreach (var name in ordered)
            {
                File.WriteAllText(Path.Combine(src, name), name);
            }

            var services = new CoreTestServices("en");
            var result = await services.Processor.ExecuteAsync(
                new SortConfig { SourceDir = src, DestinationDir = _temp.GetFilePath($"dst_mixed_{previousWinners is null}"), Filters = new FilterConfig { OneGameOneRom = true, PriorityLanguages = ["en"] } },
                isDryRun: true);

            result.Items.Select(it => it.Parsed.Convention).Should().BeEquivalentTo([NamingConvention.Tosec, NamingConvention.NoIntro]);
            var winners = result.Items.Where(it => it.Is1G1RWinner).Select(it => it.DisplayName).Order(StringComparer.Ordinal).ToList();
            winners.Should().ContainSingle("the two names are one game");
            if (previousWinners is not null)
            {
                winners.Should().Equal(previousWinners);
            }

            previousWinners = winners;
        }
    }

    [Theory]
    [InlineData("fr", "émulation bonne préférée à imparfaite")]
    [InlineData("en", "good emulation preferred to imperfect")]
    public async Task EliminationDetail_OfMameSets_ShouldNameTheEmulationStatus(string locale, string expected)
    {
        // Japan is preferred, but the emulation status comes before the country: the well emulated world set wins
        var src = _temp.CreateSubdirectory($"src_mame_{locale}");
        var dst = _temp.GetFilePath($"dst_mame_{locale}");
        var world = System.Text.Encoding.ASCII.GetBytes("WORLD PROGRAM");
        var japan = System.Text.Encoding.ASCII.GetBytes("JAPAN PROGRAM");
        string Rom(string name, byte[] data) =>
            $"""<rom name="{name}" size="{data.Length}" crc="{System.IO.Hashing.Crc32.HashToUInt32(data):x8}"/>""";
        var dat = _temp.WriteText($"mame_{locale}.xml", $"""
            <?xml version="1.0"?>
            <mame build="test">
            	<machine name="game"><description>Game (World 900101)</description>{Rom("w.bin", world)}<driver status="good"/></machine>
            	<machine name="gamej" cloneof="game" romof="game"><description>Game (Japan 900101)</description>{Rom("j.bin", japan)}<driver status="imperfect"/></machine>
            </mame>
            """);
        foreach (var (name, member, data) in new[] { ("game.zip", "w.bin", world), ("gamej.zip", "j.bin", japan) })
        {
            using var zip = System.IO.Compression.ZipFile.Open(Path.Combine(src, name), System.IO.Compression.ZipArchiveMode.Create);
            using var stream = zip.CreateEntry(member).Open();
            stream.Write(data);
        }

        var services = new CoreTestServices(locale);
        var result = await services.Processor.ExecuteAsync(
            new SortConfig
            {
                SourceDir = src,
                DestinationDir = dst,
                DatFile = dat,
                Filters = new FilterConfig { OneGameOneRom = true, PriorityCountries = ["JP", "WO"] }
            },
            isDryRun: true);

        result.Items.Single(it => it.DisplayName == "game.zip").Is1G1RWinner.Should().BeTrue();
        var eliminated = result.Items.Single(it => it.DisplayName == "gamej.zip");
        eliminated.Status.Should().Be(FileStatus.Excluded);
        services.I18n.Format(eliminated.EliminationDetail).Should().Be(expected);
    }
}
