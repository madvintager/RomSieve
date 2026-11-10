using System.Text;
using FluentAssertions;
using RomSieve.Core.Models;
using RomSieve.Core.Services;
using RomSieve.Core.Tests.Fixtures;
using Xunit;

namespace RomSieve.Core.Tests;

/// <summary>
/// Labels of the disks of a Redump set, written in a parenthesis of their own after the disc number:
/// <c>Command &amp; Conquer (USA) (Disc 1) (GDI Disc)</c> / <c>(Disc 2) (NOD Disc)</c> (Sega - Saturn). The label split the
/// set into two releases of one disc: disc 1 was kept alone, disc 2 excluded as an incomplete set (1/2).
/// <see cref="DiscLabelResolver"/> leaves it out of the release key, but keeps apart the editions whose mark takes the
/// same place (<c>(Disc 1) (Premium Box)</c>).
/// </summary>
public sealed class DiscLabelTests : IDisposable
{
    private readonly TempDirectoryFixture _temp = new();
    private readonly CoreTestServices _services = new();

    public void Dispose() => _temp.Dispose();

    private static ProcessItem Item(string fileName) => new()
    {
        SourcePath = fileName,
        FileSize = 1,
        Parsed = NoIntroRedumpParser.ParseNoIntroFilenameStatic(fileName, isRedump: true)
    };

    private static DatCatalog Catalog(params string[] gameNames)
    {
        var catalog = new DatCatalog();
        foreach (var name in gameNames)
        {
            catalog.AddGame(new DatGame { Name = name });
        }
        return catalog;
    }

    private string ReleaseKeyOf(ProcessItem item) => _services.FilterService.GetReleaseKey(item.Parsed);

    [Theory]
    [InlineData("Command & Conquer (USA) (Disc 1) (GDI Disc).cue", "(GDI Disc)")]
    [InlineData("Kowloon's Gate - Kowloon Fuusuiden (Japan, Asia) (Disc 2) (Genbu) (Shokai Genteiban).cue", "(Genbu)")]
    [InlineData("Baten Kaitos II (Japan) (Disc 1) (Rev 1).iso", null)]
    [InlineData("Tales of Symphonia (USA) (Disc 1) (Beta) (2004-03-30).iso", null)]
    [InlineData("Final Fantasy VII (USA) (Disc 1).cue", null)]
    [InlineData("Command & Conquer (USA) (GDI Disc).cue", null)]
    public void Parser_ShouldTakeTheUnrecognizedParenthesisRightAfterTheDiscNumberAsCandidate(string fileName, string? expected)
    {
        var parsed = NoIntroRedumpParser.ParseNoIntroFilenameStatic(fileName, isRedump: true);

        parsed.DiscLabelCandidate.Should().Be(expected);
        parsed.DiscLabel.Should().BeNull("only the comparison with the other disks decides");
    }

    [Fact]
    public void DisksLabelledOneByOne_ShouldFormASingleRelease()
    {
        var disc1 = Item("Command & Conquer (USA) (Disc 1) (GDI Disc).cue");
        var disc2 = Item("Command & Conquer (USA) (Disc 2) (NOD Disc).cue");
        ReleaseKeyOf(disc1).Should().NotBe(ReleaseKeyOf(disc2), "the labels split the set before");

        DiscLabelResolver.Apply([disc1, disc2], null);

        disc1.Parsed.DiscLabel.Should().Be("(GDI Disc)");
        disc2.Parsed.DiscLabel.Should().Be("(NOD Disc)");
        ReleaseKeyOf(disc1).Should().Be(ReleaseKeyOf(disc2));
    }

    [Fact]
    public void FreeLabels_FollowedByAnEditionMark_ShouldKeepTheMark()
    {
        var allies = Item("Command & Conquer - Red Alert - Retaliation (USA) (Disc 1) (Allies).cue");
        var soviet = Item("Command & Conquer - Red Alert - Retaliation (USA) (Disc 2) (Soviet).cue");
        var byakko = Item("Kowloon's Gate (Japan, Asia) (Disc 1) (Byakko) (Shokai Genteiban).cue");
        var genbu = Item("Kowloon's Gate (Japan, Asia) (Disc 2) (Genbu) (Shokai Genteiban).cue");
        var plainGenbu = Item("Kowloon's Gate (Japan, Asia) (Disc 2) (Genbu).cue");

        DiscLabelResolver.Apply([allies, soviet, byakko, genbu, plainGenbu], null);

        ReleaseKeyOf(allies).Should().Be(ReleaseKeyOf(soviet));
        ReleaseKeyOf(byakko).Should().Be(ReleaseKeyOf(genbu));
        ReleaseKeyOf(genbu).Should().NotBe(ReleaseKeyOf(plainGenbu), "the first print edition stays apart");
    }

    [Fact]
    public void EditionMarkOnEveryDisc_ShouldKeepTheEditionsApart()
    {
        var disc1 = Item("Tales of Eternia (Japan) (Disc 1).cue");
        var disc2 = Item("Tales of Eternia (Japan) (Disc 2).cue");
        var box1 = Item("Tales of Eternia (Japan) (Disc 1) (Premium Box).cue");
        var box2 = Item("Tales of Eternia (Japan) (Disc 2) (Premium Box).cue");

        DiscLabelResolver.Apply([disc1, disc2, box1, box2], null);

        box1.Parsed.DiscLabel.Should().BeNull();
        box2.Parsed.DiscLabel.Should().BeNull();
        ReleaseKeyOf(box1).Should().Be(ReleaseKeyOf(box2));
        ReleaseKeyOf(box1).Should().NotBe(ReleaseKeyOf(disc1));
    }

    [Fact]
    public void EditionMarkOfTheDat_ShouldKeepTheEditionsApart_WhenTheSourceLacksADisc()
    {
        // The source holds only the first disc of the box, but the DAT knows the second one
        var disc1 = Item("Tales of Eternia (Japan) (Disc 1).cue");
        var disc2 = Item("Tales of Eternia (Japan) (Disc 2).cue");
        var box1 = Item("Tales of Eternia (Japan) (Disc 1) (Premium Box).cue");
        var catalog = Catalog(
            "Tales of Eternia (Japan) (Disc 1)",
            "Tales of Eternia (Japan) (Disc 2)",
            "Tales of Eternia (Japan) (Disc 1) (Premium Box)",
            "Tales of Eternia (Japan) (Disc 2) (Premium Box)");

        DiscLabelResolver.Apply([disc1, disc2, box1], catalog);

        box1.Parsed.DiscLabel.Should().BeNull();
        ReleaseKeyOf(box1).Should().NotBe(ReleaseKeyOf(disc1));
    }

    [Fact]
    public void Candidate_ThatWouldMakeTheDiscLookLikeAnotherDumpOfTheSameNumber_ShouldNotBeALabel()
    {
        var plain = Item("7th Guest, The (Europe) (Disc 1).cue");
        var edc = Item("7th Guest, The (Europe) (Disc 1) (EDC).cue");
        var disc2 = Item("7th Guest, The (Europe) (Disc 2).cue");

        DiscLabelResolver.Apply([plain, edc, disc2], null);

        edc.Parsed.DiscLabel.Should().BeNull();
        ReleaseKeyOf(edc).Should().NotBe(ReleaseKeyOf(plain));
    }

    [Fact]
    public void FirstDiscWithoutLabel_ShouldKeepItsEditionMark()
    {
        // Disc 1 has no label: the mark of the edition follows its number
        var disc1 = Item("Mahjong Doukyuusei Special (Japan) (Disc 1) (Shokai Genteiban).cue");
        var disc2 = Item("Mahjong Doukyuusei Special (Japan) (Disc 2) (Portrait CD) (Shokai Genteiban).cue");

        DiscLabelResolver.Apply([disc1, disc2], null);

        disc1.Parsed.DiscLabel.Should().BeNull();
        disc2.Parsed.DiscLabel.Should().Be("(Portrait CD)");
        ReleaseKeyOf(disc1).Should().Be(ReleaseKeyOf(disc2));
    }

    [Fact]
    public void Labels_ShouldBeClearedWhenNoLongerFound()
    {
        var disc1 = Item("Command & Conquer (USA) (Disc 1) (GDI Disc).cue");
        var disc2 = Item("Command & Conquer (USA) (Disc 2) (NOD Disc).cue");
        DiscLabelResolver.Apply([disc1, disc2], null);
        var other1 = Item("Command & Conquer (USA) (Disc 1).cue");

        DiscLabelResolver.Apply([disc1, disc2, other1], null);

        disc1.Parsed.DiscLabel.Should().BeNull("the disc now looks like another dump of disc 1");
        disc2.Parsed.DiscLabel.Should().Be("(NOD Disc)");
    }

    [Fact]
    public async Task RedumpSetWithLabelledDisks_ShouldKeepBothDisksWithTheirPlaylist()
    {
        var src = _temp.CreateSubdirectory("src");
        var dst = _temp.CreateSubdirectory("dst");
        var games = new StringBuilder();
        string[] discs = ["Command & Conquer (USA) (Disc 1) (GDI Disc)", "Command & Conquer (USA) (Disc 2) (NOD Disc)"];
        foreach (var disc in discs)
        {
            var bin = _temp.WriteBytes(Path.Combine("src", $"{disc}.bin"), Encoding.UTF8.GetBytes($"DATA {disc}"));
            var cue = _temp.WriteText(Path.Combine("src", $"{disc}.cue"), $"FILE \"{disc}.bin\" BINARY\n  TRACK 01 MODE1/2352\n    INDEX 01 00:00:00\n");
            var xmlName = disc.Replace("&", "&amp;", StringComparison.Ordinal);
            games.AppendLine($"""
                	<game name="{xmlName}">
                		<category>Games</category>
                		<description>{xmlName}</description>
                		<rom name="{xmlName}.cue" size="{new FileInfo(cue).Length}" crc="{await _services.ChecksumService.ComputeFileCrc32Async(cue)}"/>
                		<rom name="{xmlName}.bin" size="{new FileInfo(bin).Length}" crc="{await _services.ChecksumService.ComputeFileCrc32Async(bin)}"/>
                	</game>
                """);
        }
        var dat = _temp.WriteText("saturn.dat", $"""
            <?xml version="1.0"?>
            <datafile>
            	<header>
            		<name>Sega - Saturn</name>
            		<url>http://redump.org/</url>
            	</header>
            {games}</datafile>
            """);

        var config = new SortConfig
        {
            SourceDir = src,
            DestinationDir = dst,
            DatFile = dat,
            NamingConvention = NamingConvention.Auto,
            Pattern = string.Empty,
            SubfolderMultidisk = true,
            GenerateM3u = true,
            M3uPlacement = M3uPlacementMode.Subfolder,
            TransferMode = TransferMode.Copy,
            Filters = new FilterConfig
            {
                OneGameOneRom = true,
                PriorityLanguages = ["fr", "m", "en"],
                PriorityCountries = ["FR", "EU", "US"],
                CompleteMultidiskSetsOnly = true,
                NoIntroCompleteMultimediaSetsOnly = true
            }
        };

        var result = await _services.Processor.ExecuteAsync(config, isDryRun: false);

        foreach (var disc in discs)
        {
            result.Items.Single(it => it.DisplayName == $"{disc}.cue").Status.Should().Be(FileStatus.Valid);
        }
        var m3u = Path.Combine(dst, "Command & Conquer", "Command & Conquer (USA).m3u");
        File.Exists(m3u).Should().BeTrue("the label of the first disc does not name the playlist of the set");
        (await File.ReadAllLinesAsync(m3u)).Where(l => !string.IsNullOrWhiteSpace(l))
            .Should().Equal(discs.Select(d => $"{d}.cue"));
    }
}
