using System.IO.Compression;
using RomSieve.Core.Models;
using RomSieve.Core.Services;
using RomSieve.Core.Tests.Fixtures;
using static RomSieve.Core.Tests.Fixtures.PatchTestEncoder;

namespace RomSieve.Core.Tests;

/// <summary>
/// Why a ROM is proposed for a patch (<see cref="PatchOriginSuggestion.Reason"/>), through a simulation: a name of the
/// patch (its own, its archive's or its folder's) that starts with the name or the title of the ROM, a name or a readme
/// that designates several ROMs, a readme that gives both forms of a ROM; and the language of a translation, read from
/// its tag (<see cref="PatchMatcher.TranslationLanguage"/>), which puts the ROMs already in that language after the
/// others, on No-Intro and TOSEC names.
/// </summary>
public sealed class PatchSuggestionTests : IDisposable
{
    private readonly CoreTestServices _services = new();
    private readonly TempDirectoryFixture _temp = new();
    private readonly string _src;
    private readonly string _patches;

    private static readonly byte[] Japan = Data(1, 4096);
    private static readonly byte[] Usa = Data(2, 4096);

    public PatchSuggestionTests()
    {
        _src = _temp.CreateSubdirectory("src");
        _patches = _temp.CreateSubdirectory("patches");
    }

    public void Dispose() => _temp.Dispose();

    private static byte[] Data(int seed, int size)
    {
        var data = new byte[size];
        new Random(seed).NextBytes(data);
        return data;
    }

    private static byte[] Patch(byte[] origin) => IpsDiff(origin, [.. origin, .. Ascii("Translated")]);

    private string WriteDat(string header, params (string Rom, byte[] Data)[] roms)
    {
        var games = string.Concat(roms.Select(r =>
        {
            var game = Path.GetFileNameWithoutExtension(r.Rom);
            return $"""<game name="{game}"><description>{game}</description><rom name="{r.Rom}" size="{r.Data.Length}" crc="{Crc(r.Data):x8}"/></game>""";
        }));
        return _temp.WriteText("dat/collection.dat", $"""<?xml version="1.0"?><datafile><header><name>{header}</name></header>{games}</datafile>""");
    }

    /// <summary>The Japanese and American releases of a game, the American one kept by the 1G1R.</summary>
    private string WriteJapanAndUsa()
    {
        _temp.WriteBytes("src/Game (Japan).sfc", Japan);
        _temp.WriteBytes("src/Game (USA).sfc", Usa);
        return WriteDat("Nintendo - Super Nintendo Entertainment System", ("Game (Japan).sfc", Japan), ("Game (USA).sfc", Usa));
    }

    private async Task<SortExecutionResult> SimulateAsync(string dat, NamingConvention convention = NamingConvention.NoIntro) =>
        await _services.Processor.ExecuteAsync(
            new SortConfig
            {
                SourceDir = _src,
                DestinationDir = _temp.CreateSubdirectory("dst"),
                NamingConvention = convention,
                DatFile = dat,
                PatchDir = _patches,
                Filters = new FilterConfig { OneGameOneRom = true, PriorityLanguages = ["en", "ja"] }
            },
            isDryRun: true);

    private string Why(PatchOriginSuggestion suggestion) => _services.I18n.Format(suggestion.Reason!);

    // =========================================================================
    // The language of a translation
    // =========================================================================

    [Theory]
    [InlineData("Lemmings (1991)(Psygnosis)[tr es]", "es")]
    [InlineData("Lemmings (1991)(Psygnosis)[tr fr-partial]", "fr")]
    [InlineData("Game (Japan) (T-En)", "en")]
    [InlineData("Game (T-En by Someone v1.2)", "en")]
    [InlineData("Game [T+Eng]", "en")]
    [InlineData("Game (T-Ger)", "de")]
    [InlineData("Game (T-Fre)", "fr")]
    public void TheLanguageOfATranslation_ShouldBeReadFromItsTag(string name, string language)
    {
        PatchMatcher.TranslationLanguage(name).Should().Be(language);
    }

    [Theory]
    [InlineData("Game (Japan)")]
    [InlineData("Game (Tec Toy)")]
    [InlineData("Game (The Lost Levels)")]
    [InlineData("Tetris (Tetris Company)")]
    [InlineData("Speedball 2 (1990)(Bitmap Brothers)(GB)[t +2 Skid Row]")]
    [InlineData("Pinball Dreams (1992)(21st Century)[h Turbo]")]
    [InlineData("Game (Traduction FR)")]
    [InlineData("SMB3_eng_v1.2")]
    public void ANameWithoutTranslationTag_ShouldGiveNoLanguage(string name)
    {
        PatchMatcher.TranslationLanguage(name).Should().BeNull();
    }

    // =========================================================================
    // Why a ROM is proposed
    // =========================================================================

    [Fact]
    public async Task ARomProposedByTheNameOfThePatch_ShouldSayWhetherItsNameOrItsTitleFits()
    {
        var dat = WriteJapanAndUsa();
        _temp.WriteBytes("patches/Game (Japan) (Hack).ips", Patch(Japan));

        var result = await SimulateAsync(dat);

        var suggestions = result.UnappliedPatches.Single().Suggestions;
        suggestions.Select(s => s.Origin.DisplayName).Should().Equal("Game (Japan).sfc", "Game (USA).sfc");
        suggestions[0].Reason!.Key.Should().Be("patches.why_name");
        Why(suggestions[0]).Should().Be("Le nom du patch commence par le nom de cette ROM");
        Why(suggestions[1]).Should().Be("Le nom du patch commence par le titre de cette ROM");
    }

    [Fact]
    public async Task ARomProposedByTheNameOfAnArchive_OrOfAFolder_ShouldSaySo()
    {
        var dat = WriteJapanAndUsa();
        var zip = _temp.GetFilePath("patches/Game (Japan) translation.zip");
        using (var archive = ZipFile.Open(zip, ZipArchiveMode.Create))
        using (var stream = archive.CreateEntry("patch.ips").Open())
        {
            stream.Write(Patch(Japan));
        }

        _temp.WriteBytes("patches/Game (USA)/patch.ips", Patch(Usa));

        var result = await SimulateAsync(dat);

        var byArchive = result.UnappliedPatches.Single(u => u.File.ArchiveMember is not null).Suggestions[0];
        Why(byArchive).Should().Be("Le nom de son archive commence par le nom de cette ROM");
        var byFolder = result.UnappliedPatches.Single(u => u.File.ArchiveMember is null).Suggestions[0];
        byFolder.Origin.DisplayName.Should().Be("Game (USA).sfc");
        Why(byFolder).Should().Be("Le nom de son dossier commence par le nom de cette ROM");
    }

    [Fact]
    public async Task TheRomsANameDesignates_ShouldSayItIsItsName()
    {
        var dat = WriteJapanAndUsa();
        _temp.WriteBytes($"patches/Game [{Crc(Japan):x8}] [{Crc(Usa):x8}].ips", Patch(Japan));

        var result = await SimulateAsync(dat);

        result.UnappliedPatches.Single().Suggestions.Select(Why).Should().Equal(
            "Le nom du patch désigne cette ROM",
            "Le nom du patch désigne cette ROM");
    }

    [Fact]
    public async Task TheRomsAReadmeDesignates_ShouldNameTheReadme()
    {
        var dat = WriteJapanAndUsa();
        _temp.WriteBytes("patches/Game.ips", Patch(Japan));
        _temp.WriteText("patches/Game.txt", $"ROM CRC32: {Crc(Japan):X8}\nOther ROM CRC32: {Crc(Usa):X8}");

        var result = await SimulateAsync(dat);

        result.UnappliedPatches.Single().Suggestions.Select(Why).Should().OnlyContain(w => w == "Game.txt désigne cette ROM");
    }

    [Fact]
    public async Task TheTwoFormsAReadmeGives_ShouldSaySo()
    {
        var data = Data(3, 8192);
        var file = (byte[])[.. Ascii("NES\u001a"), .. new byte[12], .. data];
        _temp.WriteBytes("src/Hero (USA).nes", file);
        var dat = WriteDat("Nintendo - Nintendo Entertainment System", ("Hero (USA).nes", data));
        _temp.WriteBytes("patches/Hero (Hack).ips", Patch(data));
        _temp.WriteText("patches/Hero (Hack).txt", $"File CRC32: {Crc(file):X8}\nROM CRC32: {Crc(data):X8}");

        var result = await SimulateAsync(dat);

        var suggestions = result.UnappliedPatches.Single().Suggestions;
        suggestions.Should().HaveCount(2);
        suggestions.Select(Why).Should().OnlyContain(w => w == "Hero (Hack).txt donne cette ROM avec et sans son en-tête copieur");
    }

    // =========================================================================
    // The language of a translation in the order of the ROMs proposed
    // =========================================================================

    [Fact]
    public async Task ATranslationIntoEnglish_ShouldProposeTheJapaneseReleaseBeforeTheAmericanOne()
    {
        // Without the tag, the American release would come first: the 1G1R keeps it
        var dat = WriteJapanAndUsa();
        _temp.WriteBytes("patches/Game (T-En).ips", Patch(Japan));

        var result = await SimulateAsync(dat);

        var suggestions = result.UnappliedPatches.Single().Suggestions;
        suggestions.Select(s => s.Origin.DisplayName).Should().Equal("Game (Japan).sfc", "Game (USA).sfc");
        Why(suggestions[0]).Should().Be("Le nom du patch commence par le titre de cette ROM");
        Why(suggestions[1]).Should().Be(
            "Le nom du patch commence par le titre de cette ROM ; proposée après les autres : elle est déjà dans la langue de la traduction (EN)");
    }

    [Fact]
    public async Task ATosecTranslation_ShouldProposeTheReleasesInItsLanguageLast()
    {
        var french = Data(5, 4096);
        var english = Data(6, 4096);
        var german = Data(7, 4096);
        _temp.WriteBytes("src/Lemmings (1991)(Psygnosis)(FR)[!].adf", french);
        _temp.WriteBytes("src/Lemmings (1991)(Psygnosis)(GB)[!].adf", english);
        _temp.WriteBytes("src/Lemmings (1991)(Psygnosis)(DE)[a].adf", german);
        var dat = WriteDat(
            "Commodore Amiga - Games",
            ("Lemmings (1991)(Psygnosis)(FR)[!].adf", french),
            ("Lemmings (1991)(Psygnosis)(GB)[!].adf", english),
            ("Lemmings (1991)(Psygnosis)(DE)[a].adf", german));
        _temp.WriteBytes("patches/Lemmings (1991)(Psygnosis)[tr fr].ips", Patch(english));

        var result = await SimulateAsync(dat, NamingConvention.Tosec);

        var suggestions = result.UnappliedPatches.Single().Suggestions;
        suggestions[^1].Origin.DisplayName.Should().Be("Lemmings (1991)(Psygnosis)(FR)[!].adf");
        suggestions[^1].Reason!.Key.Should().Be("patches.why_in_target_language");
        suggestions.SkipLast(1).Should().OnlyContain(s => s.Reason!.Key != "patches.why_in_target_language");
    }

    [Fact]
    public async Task ATranslationIntoALanguageNoRomHas_ShouldKeepTheUsualOrder()
    {
        var dat = WriteJapanAndUsa();
        _temp.WriteBytes("patches/Game (T-Es).ips", Patch(Japan));

        var result = await SimulateAsync(dat);

        var suggestions = result.UnappliedPatches.Single().Suggestions;
        suggestions.Select(s => s.Origin.DisplayName).Should().Equal(["Game (USA).sfc", "Game (Japan).sfc"], "the release the sort writes first");
        suggestions.Should().OnlyContain(s => s.Reason!.Key == "patches.why_title");
    }
}
