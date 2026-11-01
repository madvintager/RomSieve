using RomSieve.Core.Models;
using RomSieve.Core.Services;
using RomSieve.Core.Tests.Fixtures;

namespace RomSieve.Core.Tests;

/// <summary>
/// Unit tests for target path building, cross-platform sanitization, and collision resolution.
/// </summary>
public sealed class PathOrganizerServiceTests
{
    private readonly TosecParser _parser;
    private readonly PathOrganizerService _organizer;

    public PathOrganizerServiceTests()
    {
        _parser = new TosecParser();
        _organizer = new PathOrganizerService(_parser);
    }

    [Theory]
    [InlineData("Robo:cop? *Game", "Robo-cop- -Game")]
    [InlineData("Folder.", "Folder")]
    [InlineData("CON", "CON_")]
    [InlineData("NUL", "NUL_")]
    [InlineData("COM1", "COM1_")]
    [InlineData("LPT9", "LPT9_")]
    [InlineData("Game <1> | \"Special\" / \\Test", "Game -1- - -Special- - -Test")]
    [InlineData("   ...", "Unknown")]
    [InlineData("", "Unknown")]
    public void SanitizePathSegment_ShouldReplaceForbiddenCharactersAndReservedWindowsNames(string input, string expected)
    {
        _organizer.SanitizePathSegment(input).Should().Be(expected);
    }

    [Fact]
    public void BuildTargetRelativePath_ShouldSubstitutePublisherYearAndTitle()
    {
        var p = _parser.ParseFilename("Robocop 3 (1991)(Ocean).adf");
        var relPath = _organizer.BuildTargetRelativePath(p, "{publisher}/{year}/{title}/", restoreArticles: true);

        relPath.Should().Be(Path.Combine("Ocean", "1991", "Robocop 3"));
    }

    [Fact]
    public void BuildTargetWithRestoredArticles_ShouldRestoreLeadingArticleAndUpdateFirstLetter()
    {
        var p = _parser.ParseFilename("Legend of TOSEC, The (1995)(Acme).adf");
        var relPath = _organizer.BuildTargetRelativePath(p, "{letter}/{title}/", restoreArticles: true);

        relPath.Should().Be(Path.Combine("T", "The Legend of TOSEC"));
    }

    [Fact]
    public void BuildTargetUnmatched_ShouldReturnUnmatchedFolder()
    {
        var p = _parser.ParseFilename("random_file.zip");
        var relPath = _organizer.BuildTargetRelativePath(p, "{publisher}/{year}/{title}/", restoreArticles: true);

        relPath.Should().Be("_unmatched");
    }

    [Theory]
    [InlineData("")]
    [InlineData("/")]
    [InlineData("   ")]
    public void BuildTargetEmptyPattern_ShouldReturnDirectRootDot(string pattern)
    {
        var p = _parser.ParseFilename("Robocop 3 (1991)(Ocean).adf");
        _organizer.BuildTargetRelativePath(p, pattern, restoreArticles: true).Should().Be(".");
    }

    [Fact]
    public void ResolveCollisionRename_ShouldAppendNumericSuffixesInCaseInsensitiveManner()
    {
        using var tmp = new TempDirectoryFixture();
        var target = tmp.WriteText("game.adf", string.Empty);

        var claimed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var (newPath1, skipped1) = _organizer.ResolveCollision(target, CollisionMode.Rename, claimed);
        skipped1.Should().BeFalse();
        newPath1.Should().Be(tmp.GetFilePath("game_01.adf"));

        var (newPath2, skipped2) = _organizer.ResolveCollision(target, CollisionMode.Rename, claimed);
        skipped2.Should().BeFalse();
        newPath2.Should().Be(tmp.GetFilePath("game_02.adf"));
    }

    [Fact]
    public void ResolveCollisionSkip_ShouldReturnTrueForSkippedWhenFileAlreadyExists()
    {
        using var tmp = new TempDirectoryFixture();
        var target = tmp.WriteText("game.adf", string.Empty);

        var claimed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var (resPath, skipped) = _organizer.ResolveCollision(target, CollisionMode.Skip, claimed);

        skipped.Should().BeTrue();
        resPath.Should().Be(target);
    }

    [Fact]
    public void ResolveSetCollisionRename_CollidingCompanion_ShouldRenameWholeSetWithSameSuffix()
    {
        using var tmp = new TempDirectoryFixture();
        tmp.WriteText("track 01.bin", string.Empty);
        var set = new[] { tmp.GetFilePath("game.cue"), tmp.GetFilePath("track 01.bin") };

        var claimed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var (paths, skipped) = _organizer.ResolveSetCollision(set, CollisionMode.Rename, claimed);

        skipped.Should().BeFalse();
        paths.Should().Equal(tmp.GetFilePath("game_01.cue"), tmp.GetFilePath("track 01_01.bin"));
        claimed.Should().HaveCount(2);
    }

    [Fact]
    public void ResolveSetCollisionSkip_ClaimedCompanion_ShouldSkipWholeSetWithoutClaimingIt()
    {
        using var tmp = new TempDirectoryFixture();
        var set = new[] { tmp.GetFilePath("game.cue"), tmp.GetFilePath("track 01.bin") };
        var claimed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        _organizer.ResolveSetCollision([tmp.GetFilePath("TRACK 01.BIN")], CollisionMode.Rename, claimed);

        var (paths, skipped) = _organizer.ResolveSetCollision(set, CollisionMode.Skip, claimed);

        skipped.Should().BeTrue();
        paths.Should().Equal(set);
        claimed.Should().HaveCount(1);
    }

    [Fact]
    public void BuildTargetMultidiskSubfolder_ShouldGroupMultiDiskGamesWithoutAffectingSingleDiskGames()
    {
        var pMulti = _parser.ParseFilename("Zombi (1990)(Ubisoft)(fr)(Disk 1 of 2).dsk");
        var pSingle = _parser.ParseFilename("Zombi (1990)(Ubisoft)(fr).dsk");
        var pSingleTagged = _parser.ParseFilename("Espana Secreta (19xx)(-)(fr)(Disk 1 of 1)[cr XOR].dsk");
        var pArticle = _parser.ParseFilename("Sandman, The (1995)(TGS-CRT)(Disk 2 of 4).dsk");

        // 1. Flat destination mode (pattern="")
        _organizer.BuildTargetRelativePath(pMulti, "", subfolderMultidisk: true).Should().Be("Zombi");
        _organizer.BuildTargetRelativePath(pSingle, "", subfolderMultidisk: true).Should().Be(".");
        _organizer.BuildTargetRelativePath(pSingleTagged, "", subfolderMultidisk: true).Should().Be(".");
        _organizer.BuildTargetRelativePath(pArticle, "", restoreArticles: true, subfolderMultidisk: true).Should().Be("The Sandman");
        _organizer.BuildTargetRelativePath(pArticle, "", restoreArticles: false, subfolderMultidisk: true).Should().Be("Sandman, The");

        // 2. Tree mode without {title} in pattern: game folder is appended
        _organizer.BuildTargetRelativePath(pMulti, "{publisher}/{year}/", subfolderMultidisk: true)
            .Should().Be(Path.Combine("Ubisoft", "1990", "Zombi"));

        // Tree mode with {title} in pattern: no duplicate Zombi/Zombi
        _organizer.BuildTargetRelativePath(pMulti, "{publisher}/{year}/{title}/", subfolderMultidisk: true)
            .Should().Be(Path.Combine("Ubisoft", "1990", "Zombi"));

        // 3. Disabled subfolderMultidisk
        _organizer.BuildTargetRelativePath(pMulti, "", subfolderMultidisk: false).Should().Be(".");
    }

    [Fact]
    public void BuildTargetMultidiskHybridDatOverride_ShouldRespectDatMultidiskCertification()
    {
        // Case 1: Filename has no disk tag, but DAT certifies it's multi-disk
        var p1 = _parser.ParseFilename("Super Game (1990)(Ocean).adf");
        p1.IsMultiMedia.Should().BeFalse();
        p1 = p1 with { IsMultidiskOverride = true };
        p1.IsMultiMedia.Should().BeTrue();
        _organizer.BuildTargetRelativePath(p1, "", subfolderMultidisk: true).Should().Be("Super Game");

        // Case 2: Filename has ambiguous media label "(Disk 1)", but DAT certifies it's single-disk
        var p2 = _parser.ParseFilename("Solo Game (1990)(Ocean)(Disk 1).adf");
        p2.IsMultiMedia.Should().BeTrue();
        p2 = p2 with { IsMultidiskOverride = false };
        p2.IsMultiMedia.Should().BeFalse();
        _organizer.BuildTargetRelativePath(p2, "", subfolderMultidisk: true).Should().Be(".");

        // Case 3: ProcessItem integration: the item decision takes precedence without modifying the parse result
        var item = new ProcessItem
        {
            SourcePath = "game.adf",
            FileSize = 1000,
            Parsed = _parser.ParseFilename("Game X (1991)(Ocean).adf"),
            IsMultidisk = true
        };
        item.IsMultiMedia.Should().BeTrue();
        item.Parsed.IsMultiMedia.Should().BeFalse();
        item.Parsed.IsMultidiskOverride.Should().BeNull();
    }

    [Fact]
    public void BuildTargetHiddenSubfolder_ShouldPrefixGameSubfolderWithDot()
    {
        var p = _parser.ParseFilename("Zombi (1990)(Ubisoft)(Disk 1 of 2).adf");

        _organizer.BuildTargetRelativePath(p, "", subfolderMultidisk: true, hiddenSubfolder: true)
            .Should().Be(".Zombi");
        _organizer.BuildTargetRelativePath(p, "{publisher}/", subfolderMultidisk: true, hiddenSubfolder: true)
            .Should().Be(Path.Combine("Ubisoft", ".Zombi"));
    }

    [Theory]
    [InlineData("Final Fantasy VII (USA) (Disc 1).cue", "Final Fantasy VII (USA).m3u")]
    [InlineData("Final Fantasy VII (USA) (Disc 2 of 3).cue", "Final Fantasy VII (USA).m3u")]
    [InlineData("Secret of Monkey Island, The (1990)(LucasArts)(Disk 1 of 4)[!].adf", "Secret of Monkey Island, The (1990)(LucasArts)[!].m3u")]
    [InlineData("Shenmue (Europe) (Disc 1)", "Shenmue (Europe).m3u")]
    [InlineData("Game (France) (Disque 2 sur 3).bin", "Game (France).m3u")]
    [InlineData("Amiga Game (Side A).adf", "Amiga Game.m3u")]
    [InlineData("Cassette (Tape 1 of 2).tzx", "Cassette.m3u")]
    [InlineData("Epic (Part 1).dsk", "Epic.m3u")]
    public void GenerateM3uFilename_ShouldStripPaginationAndAppendM3uExtension(string discFilename, string expectedM3u)
    {
        _organizer.GenerateM3uFilename(discFilename).Should().Be(expectedM3u);
    }
    [Theory]
    [InlineData(TransferMode.Copy, false, false, "Psygnosis/1991/Lemmings/Lemmings (1991)(Psygnosis)[!].adf")]
    [InlineData(TransferMode.Zip, false, false, "Psygnosis/1991/Lemmings/Lemmings (1991)(Psygnosis)[!].zip")]
    [InlineData(TransferMode.Copy, true, false, "Lemmings (1991)(Psygnosis)[!].adf")]
    [InlineData(TransferMode.Copy, false, true, "Psygnosis/1991/Lemmings/Lemmings (1991)(Psygnosis)[!].adf")]
    public void BuildTargetPath_ShouldJoinTheDestinationTheFoldersAndTheFileName(TransferMode mode, bool flat, bool hiddenPlaylists, string expected)
    {
        const string name = "Lemmings (1991)(Psygnosis)[!].adf";
        var destination = Path.Combine(Path.GetTempPath(), "dest");
        var config = new SortConfig
        {
            SourceDir = ".",
            DestinationDir = destination,
            TransferMode = mode,
            Pattern = flat ? string.Empty : "{publisher}/{year}/{title}/",
            GenerateM3u = hiddenPlaylists,
            M3uPlacement = hiddenPlaylists ? M3uPlacementMode.ParentHidden : M3uPlacementMode.Parent
        };

        _organizer.BuildTargetPath(_parser.ParseTosecFilename(name), name, config)
            .Should().Be(Path.Combine([destination, .. expected.Split('/')]));
    }

    [Fact]
    public void BuildTargetPath_ShouldHideTheMultiDiskSubfolderWhenThePlaylistsArePlacedInTheParent()
    {
        const string name = "Another World (1991)(Delphine)(Disk 1 of 2).adf";
        var config = new SortConfig
        {
            SourceDir = ".",
            DestinationDir = "out",
            Pattern = "{title}/",
            GenerateM3u = true,
            SubfolderMultidisk = true,
            M3uPlacement = M3uPlacementMode.ParentHidden
        };

        _organizer.BuildTargetPath(_parser.ParseTosecFilename(name), name, config)
            .Should().Be(Path.Combine("out", "Another World", ".Another World", name));
        _organizer.BuildTargetPath(_parser.ParseTosecFilename(name), name, config, isMultiMediaOverride: false)
            .Should().Be(Path.Combine("out", "Another World", name));
    }

    [Theory]
    [InlineData("{publisher}/{year}/{title}/", "", false)]
    [InlineData("{title} ({year})/{dump_status}", "", false)]
    [InlineData("{title}/{foo}/{Title}", "{foo},{Title}", false)]
    [InlineData("{title", "", true)]
    [InlineData("title}/", "", true)]
    [InlineData("{ti tle}/", "", true)]
    [InlineData("", "", false)]
    public void CheckPattern_ShouldReportUnknownVariablesAndUnbalancedBraces(string pattern, string unknown, bool unbalanced)
    {
        var check = _organizer.CheckPattern(pattern);

        check.UnknownVariables.Should().Equal(unknown.Length == 0 ? [] : unknown.Split(','));
        check.HasUnbalancedBraces.Should().Be(unbalanced);
        check.IsValid.Should().Be(unknown.Length == 0 && !unbalanced);
    }

    [Fact]
    public void PatternVariableNames_ShouldAllBeReplacedByBuildTargetRelativePath()
    {
        var parsed = _parser.ParseTosecFilename("Lemmings (1991)(Psygnosis)(PAL)(FR)(fr)[!].adf");
        foreach (var name in PathOrganizerService.PatternVariableNames)
        {
            _organizer.CheckPattern($"{{{name}}}").IsValid.Should().BeTrue();
            _organizer.BuildTargetRelativePath(parsed, $"x{{{name}}}").Should().NotBe("x", $"{{{name}}} has a value");
        }
    }
    [Theory]
    [InlineData("Lemmings (1991)(Psygnosis)(FR)[!].adf", "fr")]
    [InlineData("Lemmings (1991)(Psygnosis)(DE)(en)[!].adf", "en")]
    [InlineData("Lemmings (1991)(Psygnosis)(GB)[!].adf", "en")]
    [InlineData("Lemmings (1991)(Psygnosis)(M3)[!].adf", "M3")]
    [InlineData("Lemmings (1991)(Psygnosis)[!].adf", "Unknown")]
    public void LanguageVariable_ShouldFallBackOnTheLanguageOfTheCountry(string name, string expected)
    {
        // A TOSEC name omits the language of its country: (FR) alone is a French release
        _organizer.BuildTargetRelativePath(_parser.ParseTosecFilename(name), "{language}").Should().Be(expected);
    }

    [Fact]
    public void LanguageVariable_ShouldKeepTheLanguageOfANoIntroName()
    {
        var parser = new NoIntroRedumpParser(_parser);
        _organizer.BuildTargetRelativePath(parser.ParseRomFilename("Tomb Raider (France).iso", convention: NamingConvention.Redump), "{language}")
            .Should().Be("fr");
        _organizer.BuildTargetRelativePath(parser.ParseRomFilename("Super Mario World (USA).sfc", convention: NamingConvention.NoIntro), "{language}")
            .Should().Be("en");
    }
}
