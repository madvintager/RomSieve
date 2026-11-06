using Microsoft.Extensions.DependencyInjection;
using RomSieve.Core.Abstractions;
using RomSieve.Core.Models;
using RomSieve.Core.Services;
using RomSieve.Core.Tests.Fixtures;

namespace RomSieve.Core.Tests;

/// <summary>
/// Unit tests for filter engine, canonical title normalization,
/// cleanliness and media fidelity scores, and language/region priorities.
/// </summary>
public sealed class RomFilterServiceTests
{
    private readonly CoreTestServices _services = new();
    private TosecParser Tosec => _services.TosecParser;
    private NoIntroRedumpParser NoIntro => _services.NoIntroParser;
    private RomFilterService Filter => _services.FilterService;

    [Fact]
    public void Container_ShouldResolveTheSplitServicesBehindTheFacade()
    {
        using var provider = new ServiceCollection().AddRomSieveCore().BuildServiceProvider();
        var facade = provider.GetRequiredService<IRomFilterService>();
        var gameKeys = provider.GetRequiredService<IGameKeyService>();
        var scoring = provider.GetRequiredService<IReleaseScoringService>();
        var filters = provider.GetRequiredService<IFilterEvaluator>();

        var parsed = Tosec.ParseFilename("Lemmings v1.1 (1991)(Psygnosis)(DE)(Disk 1 of 2)[cr NBC].adf");
        facade.GetReleaseKey(parsed, smartMultidisk: true).Should().Be(gameKeys.GetReleaseKey(parsed, smartMultidisk: true));
        facade.GetReleaseCleanlinessScore(parsed).Should().Be(scoring.GetReleaseCleanlinessScore(parsed));
        facade.EvaluateFilter(parsed, new FilterConfig()).Should().Be(filters.EvaluateFilter(parsed, new FilterConfig()));
        facade.EvaluateFilter(parsed, new FilterConfig()).IsKept.Should().BeFalse("cracks are excluded by default");
    }

    [Fact]
    public void FilterBadDumps_ShouldExcludeWhenEnabledAndKeepWhenDisabled()
    {
        var p = Tosec.ParseFilename("Game (1991)(Ocean)[b1].adf");

        var cfgExclude = new FilterConfig { ExcludeBadDumps = true };
        var (ok1, reason1) = Filter.EvaluateFilter(p, cfgExclude);
        ok1.Should().BeFalse();
        _services.I18n.Format(reason1).Should().Contain("Bad Dump");

        cfgExclude.ExcludeBadDumps = false;
        var (ok2, _) = Filter.EvaluateFilter(p, cfgExclude);
        ok2.Should().BeTrue();
    }

    [Fact]
    public void FilterVerifiedOnly_ShouldOnlyAcceptVerifiedBracketExclamationDumps()
    {
        var pClean = Tosec.ParseFilename("Game (1991)(Ocean).adf");
        var pVerif = Tosec.ParseFilename("Game (1991)(Ocean)[!].adf");

        var cfg = new FilterConfig { DumpsVerifiedOnly = true };

        var (ok1, reason1) = Filter.EvaluateFilter(pClean, cfg);
        ok1.Should().BeFalse();
        _services.I18n.Format(reason1).Should().Contain("non vérifié");

        var (ok2, _) = Filter.EvaluateFilter(pVerif, cfg);
        ok2.Should().BeTrue();
    }

    [Fact]
    public void FilterDemoMode_ShouldExcludeDemosOnlyWhenExcludeDemosIsTrue()
    {
        var pDemo = Tosec.ParseFilename("Game (demo) (1991)(Ocean).adf");
        var pFull = Tosec.ParseFilename("Game (1991)(Ocean).adf");

        var cfg = new FilterConfig { ExcludeDemos = true };
        Filter.EvaluateFilter(pDemo, cfg).IsKept.Should().BeFalse();
        Filter.EvaluateFilter(pFull, cfg).IsKept.Should().BeTrue();

        cfg.ExcludeDemos = false;
        Filter.EvaluateFilter(pDemo, cfg).IsKept.Should().BeTrue();
        Filter.EvaluateFilter(pFull, cfg).IsKept.Should().BeTrue();
    }

    [Fact]
    public void FilterLanguages_ShouldRankExplicitAndUntaggedTosecLanguages()
    {
        var pFr = Tosec.ParseFilename("Game (1991)(Ocean)(fr).adf");
        var pDe = Tosec.ParseFilename("Game (1991)(Ocean)(de).adf");
        var pNone = Tosec.ParseFilename("Game (1991)(Ocean).adf");

        string[] prioFrEn = ["fr", "en"];
        Filter.GetLanguagePriorityRank(pFr, prioFrEn).Should().Be(0);
        Filter.GetLanguagePriorityRank(pDe, prioFrEn).Should().BeNull();

        // Untagged matches "en" per TNC convention if "en" is in priority
        Filter.GetLanguagePriorityRank(pNone, prioFrEn).Should().Be(1);

        // Untagged does NOT match if "en" is not in priority
        Filter.GetLanguagePriorityRank(pNone, ["fr"]).Should().BeNull();
    }

    [Fact]
    public void FilterMultilingualMFlag_ShouldMatchOpaqueMAndExplicitMultiLanguageSharingPriorityLanguages()
    {
        var pM3 = Tosec.ParseFilename("Game (1991)(Ocean)(M3).adf");
        var pDual = Tosec.ParseFilename("Game (1991)(Ocean)(de-fr).adf");
        var pFr = Tosec.ParseFilename("Game (1991)(Ocean)(fr).adf");
        var pDe = Tosec.ParseFilename("Game (1991)(Ocean)(de).adf");

        // 1. Priority ["fr"]: keeps fr and de-fr (contains fr), but not M3 or de
        Filter.GetLanguagePriorityRank(pFr, ["fr"]).Should().Be(0);
        Filter.GetLanguagePriorityRank(pDual, ["fr"]).Should().Be(0);
        Filter.GetLanguagePriorityRank(pM3, ["fr"]).Should().BeNull();
        Filter.GetLanguagePriorityRank(pDe, ["fr"]).Should().BeNull();

        // 2. Priority ["fr", "m"]: keeps fr and de-fr at rank 0, and M3 at rank 1
        Filter.GetLanguagePriorityRank(pFr, ["fr", "m"]).Should().Be(0);
        Filter.GetLanguagePriorityRank(pDual, ["fr", "m"]).Should().Be(0);
        Filter.GetLanguagePriorityRank(pM3, ["fr", "m"]).Should().Be(1);

        // 3. Priority specifically with ["m"]
        Filter.GetLanguagePriorityRank(pM3, ["m"]).Should().Be(0);
        Filter.GetLanguagePriorityRank(pDual, ["m"]).Should().Be(0);
        Filter.GetLanguagePriorityRank(pFr, ["m"]).Should().BeNull();

        // 4. Explicit foreign multilingual tags (e.g. No-Intro '(Ja,Ko)' or '(Es,Pt)') must NOT match 'm'
        var pJaKo = NoIntro.ParseFilename("Taekwon-Do (Japan) (Ja,Ko).sfc");
        var pEsPt = NoIntro.ParseFilename("Super Copa (USA) (Es,Pt).sfc");
        var pEnJa = NoIntro.ParseFilename("Wizardry V (Japan) (En,Ja).sfc");
        var pEnFrDe = NoIntro.ParseFilename("Tintin in Tibet (Europe) (En,Fr,De).sfc");

        Filter.GetLanguagePriorityRank(pJaKo, ["fr", "m", "en"]).Should().BeNull();
        Filter.GetLanguagePriorityRank(pJaKo, ["m", "fr", "en", "de", "es", "it"]).Should().BeNull();
        Filter.GetLanguagePriorityRank(pJaKo, ["en", "m"]).Should().BeNull();
        Filter.GetLanguagePriorityRank(pEsPt, ["fr", "m", "en"]).Should().BeNull();

        // (En,Ja) only has 1 language ('en') in ['fr', 'm', 'en'], so it matches 'en' (rank 2), not 'm' (rank 1)
        Filter.GetLanguagePriorityRank(pEnJa, ["fr", "m", "en"]).Should().Be(2);
        Filter.GetLanguagePriorityRank(pEnFrDe, ["fr", "m", "en"]).Should().Be(0);
        Filter.GetLanguagePriorityRank(pEnFrDe, ["m", "fr", "en"]).Should().Be(0);
    }

    [Fact]
    public void FilterIgnoreModificationsInPriority_ShouldBypassTosecModificationFiltersWhenEnabled()
    {
        var pCrack = Tosec.ParseFilename("Robocop 3 (1991)(Ocean)(fr)[cr Flashtro].adf");
        var pHack = Tosec.ParseFilename("Robocop 3 (1991)(Ocean)(fr)[h RedCompany].adf");
        var pDev = Tosec.ParseFilename("Robocop 3 (1991)(Ocean)(fr)(proto).adf");

        // 1. Strict mode
        var cfgStrict = new FilterConfig
        {
            OneGameOneRom = true,
            IgnoreModificationsInPriority = false,
            ExcludeModifications = true,
            ExcludeDevVersions = true
        };
        var (okCr, reasonCr) = Filter.EvaluateFilter(pCrack, cfgStrict);
        okCr.Should().BeFalse();
        _services.I18n.Format(reasonCr).Should().Contain("Crack");

        var (okH, reasonH) = Filter.EvaluateFilter(pHack, cfgStrict);
        okH.Should().BeFalse();
        _services.I18n.Format(reasonH).Should().Contain("Hack");

        var (okDev, reasonDev) = Filter.EvaluateFilter(pDev, cfgStrict);
        okDev.Should().BeFalse();
        _services.I18n.Format(reasonDev).Should().Contain("développement");

        // 2. Ignore modifications in priority
        var cfgIgnore = new FilterConfig
        {
            OneGameOneRom = true,
            IgnoreModificationsInPriority = true,
            ExcludeModifications = true,
            ExcludeDevVersions = true
        };
        Filter.EvaluateFilter(pCrack, cfgIgnore).IsKept.Should().BeTrue();
        Filter.EvaluateFilter(pHack, cfgIgnore).IsKept.Should().BeTrue();
        Filter.EvaluateFilter(pDev, cfgIgnore).IsKept.Should().BeTrue();
    }

    [Fact]
    public void ParseVersionRevisionDateVideoAndMediaCategory_ShouldExtractAndCompareCorrectly()
    {
        // Versions
        ReleaseScoringService.CompareTuples(Filter.ParseVersionTuple("v1.2"), Filter.ParseVersionTuple("v1.1")).Should().BePositive();
        ReleaseScoringService.CompareTuples(Filter.ParseVersionTuple("v1.0a"), Filter.ParseVersionTuple("v1.0")).Should().BePositive();
        ReleaseScoringService.CompareTuples(Filter.ParseVersionTuple("v1.0b"), Filter.ParseVersionTuple("v1.0a")).Should().BePositive();
        ReleaseScoringService.CompareTuples(Filter.ParseVersionTuple("r2.03"), Filter.ParseVersionTuple("r2.01")).Should().BePositive();
        ReleaseScoringService.CompareTuples(Filter.ParseVersionTuple("r01.2000"), Filter.ParseVersionTuple("r01.1000")).Should().BePositive();
        ReleaseScoringService.CompareTuples(Filter.ParseVersionTuple("v1.1d r0.037"), Filter.ParseVersionTuple("v1.1d r0.031")).Should().BePositive();
        ReleaseScoringService.CompareTuples(Filter.ParseVersionTuple("v1.009 r32"), Filter.ParseVersionTuple("v1.009 r30")).Should().BePositive();

        var p3d201 = Tosec.ParseFilename("3D Construction Kit II r2.01 (1992)(Domark)(Disk 1 of 2)[h Ministry][construction kit].adf");
        var p3d203 = Tosec.ParseFilename("3D Construction Kit II r2.03 (1992)(Domark)(FR)(Disk 1 of 2)[construction kit].adf");
        Filter.GetCanonicalGameKey(p3d201).Should().Be(Filter.GetCanonicalGameKey(p3d203));
        Filter.GetEffectiveVersion(p3d201).Should().Be("r2.01");
        Filter.GetEffectiveVersion(p3d203).Should().Be("r2.03");

        // Revisions
        var pRev1 = Tosec.ParseFilename("Game (1991)(Ocean)(Rev 1).adf");
        var pRev2 = Tosec.ParseFilename("Game (1991)(Ocean)(Rev 2).adf");
        var pRevA = Tosec.ParseFilename("Game (1991)(Ocean)(Rev A).adf");
        var pNone = Tosec.ParseFilename("Game (1991)(Ocean).adf");

        ReleaseScoringService.CompareTuples(Filter.ParseRevisionTuple(pRev2), Filter.ParseRevisionTuple(pRev1)).Should().BePositive();
        ReleaseScoringService.CompareTuples(Filter.ParseRevisionTuple(pRevA), Filter.ParseRevisionTuple(pNone)).Should().BePositive();

        // Dates
        Filter.ParseDateTuple("1991-05-12").CompareTo(Filter.ParseDateTuple("1991-05")).Should().BePositive();
        Filter.ParseDateTuple("1991-05").CompareTo(Filter.ParseDateTuple("1991")).Should().BePositive();
        Filter.ParseDateTuple("1991").CompareTo(Filter.ParseDateTuple("1990")).Should().BePositive();

        // Video inference
        var pPal = Tosec.ParseFilename("Game (1991)(Ocean)(PAL).adf");
        var pNtsc = Tosec.ParseFilename("Game (1991)(Ocean)(NTSC).adf");
        var pFr = Tosec.ParseFilename("Game (1991)(Ocean)(FR).adf");
        var pUs = Tosec.ParseFilename("Game (1991)(Ocean)(US).adf");

        Filter.GetEffectiveVideo(pPal).Should().Be("pal");
        Filter.GetEffectiveVideo(pNtsc).Should().Be("ntsc");
        Filter.GetEffectiveVideo(pFr).Should().Be("pal");
        Filter.GetEffectiveVideo(pUs).Should().Be("ntsc");

        // Media categories
        Filter.GetMediaCategory("game.adf").Should().Be("floppy");
        Filter.GetMediaCategory("game.jfd").Should().Be("floppy");
        Filter.GetMediaCategory("game.d64").Should().Be("floppy");
        Filter.GetMediaCategory("game.crt").Should().Be("cartridge");
        Filter.GetMediaCategory("game.nes").Should().Be("cartridge");
        Filter.GetMediaCategory("game.iso").Should().Be("cd");
        Filter.GetMediaCategory("game.lha").Should().Be("hd");
    }

    [Fact]
    public void NoIntroSpecificFilters_ShouldEnforceStrictWatertightnessWithTosecFilters()
    {
        var pPirate = NoIntro.ParseFilename("Super Mario World (USA) (Pirate).sfc");
        var pHack = NoIntro.ParseFilename("Super Mario World (USA) (Hack).sfc");
        var pAlt = NoIntro.ParseFilename("Street Fighter II (USA) (Alt 1).sfc");
        var pUnl = NoIntro.ParseFilename("Micro Mages (USA) (Unl).nes");

        // 1. Pirate filter
        var (okPOn, reasonP) = Filter.EvaluateFilter(pPirate, new FilterConfig { NoIntroExcludePirate = true });
        okPOn.Should().BeFalse();
        _services.I18n.Format(reasonP).ToLowerInvariant().Should().Contain("pirate");
        Filter.EvaluateFilter(pPirate, new FilterConfig { NoIntroExcludePirate = false }).IsKept.Should().BeTrue();

        // 2. Hack filter
        var (okHOn, reasonH) = Filter.EvaluateFilter(pHack, new FilterConfig { NoIntroExcludeHack = true });
        okHOn.Should().BeFalse();
        _services.I18n.Format(reasonH).ToLowerInvariant().Should().Contain("hack");
        Filter.EvaluateFilter(pHack, new FilterConfig { NoIntroExcludeHack = false }).IsKept.Should().BeTrue();

        // 3. Alternate filter
        Filter.EvaluateFilter(pAlt, new FilterConfig { NoIntroExcludeAlternate = true }).IsKept.Should().BeFalse();
        Filter.EvaluateFilter(pAlt, new FilterConfig { NoIntroExcludeAlternate = false }).IsKept.Should().BeTrue();

        // 4. Unlicensed filter
        Filter.EvaluateFilter(pUnl, new FilterConfig { NoIntroExcludeUnlicensed = true }).IsKept.Should().BeFalse();
        Filter.EvaluateFilter(pUnl, new FilterConfig { NoIntroExcludeUnlicensed = false }).IsKept.Should().BeTrue();

        // 5. Strict watertightness: TOSEC options (including IgnoreModificationsInPriority) must NEVER bypass No-Intro filters
        var pBiosProto = NoIntro.ParseFilename("[BIOS] Super NES CD-ROM - Boot ROM (Japan) (En) (Proto).sfc");
        var cfgNoIntroWithTosecIgnore = new FilterConfig
        {
            OneGameOneRom = true,
            IgnoreModificationsInPriority = true,
            ExcludeDevVersions = false,
            ExcludeModifications = false,
            NoIntroExcludeDevVersions = true,
            NoIntroExcludeHack = true,
            NoIntroExcludePirate = true
        };
        var (okProto, reasonProto) = Filter.EvaluateFilter(pBiosProto, cfgNoIntroWithTosecIgnore);
        okProto.Should().BeFalse();
        _services.I18n.Format(reasonProto).Should().Contain("Proto");

        Filter.EvaluateFilter(pHack, cfgNoIntroWithTosecIgnore).IsKept.Should().BeFalse();
        Filter.EvaluateFilter(pPirate, cfgNoIntroWithTosecIgnore).IsKept.Should().BeFalse();

        // Conversely, No-Intro options must NEVER affect TOSEC items
        var pTosecProto = Tosec.ParseFilename("Game (1991)(Ocean)(proto).adf");
        var cfgTosecOnly = new FilterConfig
        {
            ExcludeDevVersions = false,
            NoIntroExcludeDevVersions = true,
            NoIntroExcludeBadDumps = true
        };
        Filter.EvaluateFilter(pTosecProto, cfgTosecOnly).IsKept.Should().BeTrue();
    }

    [Fact]
    public void CountryPriorityRankAndEffectiveCountries_ShouldNormalizeUkGbAndSupportMultilingualCountryAffinity()
    {
        var pFr = NoIntro.ParseFilename("Rayman (France).bin");
        var pCaFr = NoIntro.ParseFilename("Rayman (Canada) (Fr).bin");
        var pUk = Tosec.ParseFilename("Alien Breed (1991)(Team17)(UK).adf");
        var pMulti = NoIntro.ParseFilename("Sonic (USA, Europe).md");
        var pNone = Tosec.ParseFilename("Alien Breed (1991)(Team17).adf");

        Filter.GetEffectiveCountries(pFr).Should().Contain("FR");
        Filter.GetEffectiveCountries(pCaFr).Should().Contain("CA");
        Filter.GetEffectiveCountries(pUk).Should().Contain(["UK", "GB"]);
        Filter.GetEffectiveCountries(pMulti).Should().Contain(["US", "EU"]);
        Filter.GetEffectiveCountries(pNone).Should().BeEmpty();

        string[] prio = ["FR", "EU", "US", "GB", "CA"];
        Filter.GetCountryPriorityRank(pFr, prio).Should().Be(0);
        Filter.GetCountryPriorityRank(pMulti, prio).Should().Be(1); // EU at 1, US at 2 -> min is 1
        Filter.GetCountryPriorityRank(pUk, prio).Should().Be(3);    // UK matches GB at index 3
        Filter.GetCountryPriorityRank(pCaFr, prio).Should().Be(4);
        Filter.GetCountryPriorityRank(pNone, prio).Should().BeNull();

        // Bilingual country affinity: Canada (CA) is native for both 'fr' and 'en'
        Filter.GetRegionLanguageAffinity(pCaFr, ["fr", "en"], 0).Should().Be(2);
    }

    [Theory]
    [InlineData("Street Fighter II - The World Warrior", "Street Fighter II: The World Warrior")]
    [InlineData("Legend of Zelda, The - Link to the Past, A", "Legend of Zelda, The: A Link to the Past")]
    [InlineData("Pokémon - Version Rouge", "Pokemon: Version Rouge")]
    [InlineData("Astérix & Obélix", "Asterix and Obelix")]
    [InlineData("Sonic & Knuckles", "Sonic and Knuckles")]
    [InlineData("Mario + Rabbids", "Mario and Rabbids")]
    [InlineData("Simpsons, The - Bart vs. the Space Mutants", "The Simpsons: Bart versus the Space Mutants")]
    [InlineData("Tony Hawk's Pro Skater II", "Tony Hawks Pro Skater 2")]
    [InlineData("Dr. Mario", "Dr Mario")]
    [InlineData("Defender II", "Defender 2")]
    [InlineData("Double Dragon III - The Sacred Stones", "Double Dragon 3: The Sacred Stones")]
    [InlineData("Ultima V - Warriors of Destiny", "Ultima 5: Warriors of Destiny")]
    [InlineData("Final Fantasy VII", "Final Fantasy 7")]
    public void NormalizeCanonicalTitle_ShouldUnifyEquivalentGameTitles(string titleA, string titleB)
    {
        Filter.NormalizeCanonicalTitle(titleA).Should().Be(Filter.NormalizeCanonicalTitle(titleB));
    }

    [Fact]
    public void NormalizeCanonicalTitleAndGameKey_ShouldPreserveRomanNumeralGuardsAndUnifyGameKeys()
    {
        Filter.NormalizeCanonicalTitle("0 ß\t").Should().Be("0 ss", "a tab is a word separator, not part of a key");
        Filter.NormalizeCanonicalTitle("Pac\tMan").Should().Be("pac man");
        Filter.NormalizeCanonicalTitle("Cœur de Lion").Should().Be("coeur de lion");
        Filter.NormalizeCanonicalTitle("I, Robot").Should().Be("i robot");
        Filter.NormalizeCanonicalTitle("Mega Man X").Should().Be("mega man x");
        Filter.NormalizeCanonicalTitle("Mega Man X").Should().NotBe(Filter.NormalizeCanonicalTitle("Mega Man 10"));
        Filter.NormalizeCanonicalTitle("V-Rally").Should().Be("v rally");
        Filter.NormalizeCanonicalTitle("V - The Visitors").Should().Be("v the visitors");

        var p1 = NoIntro.ParseFilename("Double Dragon II - The Revenge (USA).nes");
        var p2 = NoIntro.ParseFilename("Double Dragon 2: The Revenge (Europe).nes");
        Filter.GetCanonicalGameKey(p1).Should().Be(Filter.GetCanonicalGameKey(p2));
    }

    [Fact]
    public void CleanlinessScoreExtendedAdfAndModified_ShouldPenalizeExtendedAdfAndModifiedHierarchy()
    {
        var pClean = Tosec.ParseFilename("Nitro (1990)(Psygnosis).adf");
        var pExt = Tosec.ParseFilename("Nitro (1990)(Psygnosis)[extended adf].adf");
        var pCr = Tosec.ParseFilename("Nitro (1990)(Psygnosis)(Disk 2 of 2)(Game)[cr HZ][bootable].adf");
        var pCrf = Tosec.ParseFilename("Nitro (1990)(Psygnosis)[cr PDX][f AGA Carlos Ace].adf");
        var pCrt = Tosec.ParseFilename("Nitro (1990)(Psygnosis)(Disk 2 of 2)(Game)[cr HZ][t +5 Full-Evil][bootable].adf");
        var pCrm = Tosec.ParseFilename("Nitro (1990)(Psygnosis)(Disk 2 of 2)(Game)[cr HZ][m highscore][bootable].adf");
        var pCrht = Tosec.ParseFilename("Nitro (1990)(Psygnosis)(Disk 2 of 2)(Game)[cr HZ][h Spaikers][t +5 Full-Evil][bootable].adf");
        var pMbad = Tosec.ParseFilename("Nitro (1990)(Psygnosis)(Disk 2 of 2)(Game)[cr HZ][m baddump - highscore][bootable].adf");

        Filter.IsExtendedAdf(pExt).Should().BeTrue();
        Filter.IsExtendedAdf(pClean).Should().BeFalse();

        Filter.GetReleaseCleanlinessScore(pClean).Should().BeGreaterThan(Filter.GetReleaseCleanlinessScore(pCr));
        Filter.GetReleaseCleanlinessScore(pCr).Should().BeGreaterThan(Filter.GetReleaseCleanlinessScore(pCrf));
        Filter.GetReleaseCleanlinessScore(pCrf).Should().BeGreaterThan(Filter.GetReleaseCleanlinessScore(pCrt));
        Filter.GetReleaseCleanlinessScore(pCrt).Should().BeGreaterThan(Filter.GetReleaseCleanlinessScore(pCrm));
        Filter.GetReleaseCleanlinessScore(pCrm).Should().BeGreaterThan(Filter.GetReleaseCleanlinessScore(pCrht));
        Filter.GetReleaseCleanlinessScore(pCrht).Should().BeGreaterThan(Filter.GetReleaseCleanlinessScore(pExt));
        Filter.GetReleaseCleanlinessScore(pMbad).Should().BeNegative();
    }

    [Fact]
    public void CumulativeCleanlinessScore_ShouldPenalizeStackedModificationsAndNumberedVariants()
    {
        var pClean = Tosec.ParseFilename("Game (1991)(Ocean).adf");
        var pF1 = Tosec.ParseFilename("Game (1991)(Ocean)[f AGA].adf");
        var pF2 = Tosec.ParseFilename("Game (1991)(Ocean)[f2 AGA].adf");
        var pTr = Tosec.ParseFilename("Game (1991)(Ocean)[tr fr].adf");
        var pA1 = Tosec.ParseFilename("Game (1991)(Ocean)[a].adf");
        var pA2 = Tosec.ParseFilename("Game (1991)(Ocean)[a2].adf");
        var pCr1 = Tosec.ParseFilename("Game (1991)(Ocean)[cr FLT].adf");
        var pCr2 = Tosec.ParseFilename("Game (1991)(Ocean)[cr2 FLT].adf");
        var pT1 = Tosec.ParseFilename("Game (1991)(Ocean)[t +5].adf");
        var pCrf = Tosec.ParseFilename("Game (1991)(Ocean)[cr FLT][f AGA].adf");
        var pCrt = Tosec.ParseFilename("Game (1991)(Ocean)[cr FLT][t +5].adf");
        var pCrtf = Tosec.ParseFilename("Game (1991)(Ocean)[cr FLT][t +5][f AGA].adf");
        var pCrtfh = Tosec.ParseFilename("Game (1991)(Ocean)[cr FLT][t +5][f AGA][h Group].adf");

        Filter.IsReleaseClean(pClean).Should().BeTrue();
        Filter.IsReleaseClean(pF1).Should().BeFalse();
        Filter.IsReleaseClean(pCr1).Should().BeFalse();

        // 1. Category hierarchy: clean > [f] > [tr] > [a] > [cr] > [t]
        Filter.GetReleaseCleanlinessScore(pClean).Should().BeGreaterThan(Filter.GetReleaseCleanlinessScore(pF1));
        Filter.GetReleaseCleanlinessScore(pF1).Should().BeGreaterThan(Filter.GetReleaseCleanlinessScore(pTr));
        Filter.GetReleaseCleanlinessScore(pTr).Should().BeGreaterThan(Filter.GetReleaseCleanlinessScore(pA1));
        Filter.GetReleaseCleanlinessScore(pA1).Should().BeGreaterThan(Filter.GetReleaseCleanlinessScore(pCr1));
        Filter.GetReleaseCleanlinessScore(pCr1).Should().BeGreaterThan(Filter.GetReleaseCleanlinessScore(pT1));

        // 2. Cumulative stacking penalties: [cr] > [cr][f] > [cr][t] > [cr][t][f] > [cr][t][f][h]
        Filter.GetReleaseCleanlinessScore(pCr1).Should().BeGreaterThan(Filter.GetReleaseCleanlinessScore(pCrf));
        Filter.GetReleaseCleanlinessScore(pCrf).Should().BeGreaterThan(Filter.GetReleaseCleanlinessScore(pCrt));
        Filter.GetReleaseCleanlinessScore(pCrt).Should().BeGreaterThan(Filter.GetReleaseCleanlinessScore(pCrtf));
        Filter.GetReleaseCleanlinessScore(pCrtf).Should().BeGreaterThan(Filter.GetReleaseCleanlinessScore(pCrtfh));

        // 3. Numbered variant penalties: primary variant > secondary variant ([cr] > [cr2], [f] > [f2], [a] > [a2])
        Filter.GetReleaseCleanlinessScore(pF1).Should().BeGreaterThan(Filter.GetReleaseCleanlinessScore(pF2));
        Filter.GetReleaseCleanlinessScore(pA1).Should().BeGreaterThan(Filter.GetReleaseCleanlinessScore(pA2));
        Filter.GetReleaseCleanlinessScore(pCr1).Should().BeGreaterThan(Filter.GetReleaseCleanlinessScore(pCr2));
    }

    [Fact]
    public void NoIntroRereleaseFilterAndCleanlinessScore_ShouldPenalizeRereleasesBy100AndSupportOptionalExclusion()
    {
        var pOrig = NoIntro.ParseFilename("Super Mario Bros. 3 (USA).nes");
        var pVc = NoIntro.ParseFilename("Super Mario Bros. 3 (USA) (Virtual Console).nes");
        var pNso = NoIntro.ParseFilename("Star Fox 2 (USA, Europe) (Classic Mini, Switch Online).sfc");
        var pHack = NoIntro.ParseFilename("Super Mario Bros. 3 (USA) (Hack).nes");

        Filter.IsReleaseClean(pOrig).Should().BeTrue();
        Filter.IsReleaseClean(pVc).Should().BeFalse();
        Filter.IsReleaseClean(pNso).Should().BeFalse();

        Filter.GetReleaseCleanlinessScore(pOrig).Should().Be(1250);
        Filter.GetReleaseCleanlinessScore(pVc).Should().Be(1150);
        Filter.GetReleaseCleanlinessScore(pOrig).Should().BeGreaterThan(Filter.GetReleaseCleanlinessScore(pVc));
        Filter.GetReleaseCleanlinessScore(pVc).Should().BeGreaterThan(Filter.GetReleaseCleanlinessScore(pHack));

        Filter.GetReleaseKey(pOrig).Should().NotBe(Filter.GetReleaseKey(pVc));

        var cfgDefault = new FilterConfig { NoIntroExcludeRereleases = false };
        Filter.EvaluateFilter(pVc, cfgDefault).IsKept.Should().BeTrue();

        var cfgExclude = new FilterConfig { NoIntroExcludeRereleases = true };
        var (okExc, reasonExc) = Filter.EvaluateFilter(pVc, cfgExclude);
        okExc.Should().BeFalse();
        _services.I18n.Format(reasonExc).Should().Contain("Virtual Console");
        Filter.EvaluateFilter(pOrig, cfgExclude).IsKept.Should().BeTrue();
    }

    [Theory]
    [InlineData("Game (1991)(Pub).ipf", 30)]
    [InlineData("Game (1991)(Pub).stx", 30)]
    [InlineData("Game (1991)(Pub).g64", 30)]
    [InlineData("Game (United Kingdom).jfd", 30)]
    [InlineData("Game (1991)(Pub).adf", 20)]
    [InlineData("Game (1991)(Pub).d64", 20)]
    [InlineData("Game (1991)(Pub).dsk", 20)]
    [InlineData("Game (1991)(Pub).dms", 10)]
    [InlineData("Game (1991)(Pub).dmz", 10)]
    [InlineData("Game (Europe).chd", 32)]
    [InlineData("Game (Europe).cue", 30)]
    [InlineData("Game (Europe).gdi", 30)]
    [InlineData("Game (Europe).cdi", 20)]
    [InlineData("Game (Europe).iso", 10)]
    [InlineData("Game (1987)(Pub).tzx", 30)]
    [InlineData("Game (1987)(Pub).cdt", 30)]
    [InlineData("Game (1987)(Pub).tap", 20)]
    [InlineData("Game (1987)(Pub).wav", 10)]
    public void MediaFormatFidelityScore_ShouldReturnExpectedIntraFamilyRank(string filename, int expectedScore)
    {
        Filter.GetMediaFormatFidelityScore(filename).Should().Be(expectedScore);
    }

    [Theory]
    [InlineData("Alone in the Dark (United Kingdom) (Demo).adf", "Alone in the Dark (United Kingdom) (Disk 1).jfd")]
    [InlineData("Wolfchild (demo) (1992)(Core Design).adf", "Wolfchild (1992)(Core Design)(Disk 1 of 2).adf")]
    [InlineData("Hamsters (United Kingdom) (v1.10) (Demo).adf", "Hamsters (United Kingdom) (v1.10).adf")]
    public void OriginalReleaseScore_Demo_ShouldBeLowerThanTheFullGame(string demo, string fullGame)
    {
        var pDemo = NoIntro.ParseRomFilename(demo);
        var pGame = NoIntro.ParseRomFilename(fullGame);

        pDemo.Demo.Should().NotBeNullOrEmpty();
        Filter.GetOriginalReleaseScore(pDemo).Should().BeLessThan(Filter.GetOriginalReleaseScore(pGame));
    }

    [Fact]
    public void ReleaseKeySegregation_ShouldStrictlySegregateDifferentExtensionsWithinSameMediaCategory()
    {
        var pIpf = Tosec.ParseFilename("Turrican II (1991)(Rainbow Arts)(Disk 1 of 2).ipf");
        var pAdf = Tosec.ParseFilename("Turrican II (1991)(Rainbow Arts)(Disk 1 of 2).adf");

        Filter.GetMediaCategory(pIpf.RawFilename).Should().Be("floppy");
        Filter.GetMediaCategory(pAdf.RawFilename).Should().Be("floppy");
        Filter.GetMediaExtension(pIpf.RawFilename).Should().Be(".ipf");
        Filter.GetMediaExtension(pAdf.RawFilename).Should().Be(".adf");
        Filter.GetReleaseKey(pIpf, smartMultidisk: false).Should().NotBe(Filter.GetReleaseKey(pAdf, smartMultidisk: false));
        Filter.GetReleaseKey(pIpf, smartMultidisk: true).Should().NotBe(Filter.GetReleaseKey(pAdf, smartMultidisk: true));
    }
}
