using System.Globalization;
using System.Text.RegularExpressions;
using RomSieve.Core.Models;
using RomSieve.Core.Services;

namespace RomSieve.Core.Tests;

/// <summary>
/// Unit tests for localization and internationalization service (<see cref="LocalizationService"/>).
/// </summary>
public sealed partial class LocalizationServiceTests
{
    private readonly LocalizationService _i18n = new();

    [Fact]
    public void DefaultLocale_ShouldBeEnglishAndReturnEnglishLabels()
    {
        new LocalizationService().CurrentLocale.Should().Be("en");
        _i18n.SetLocale("xx");

        _i18n.CurrentLocale.Should().Be("en");
        _i18n.Tr("app.title").Should().Be("RomSieve");
        _i18n.Tr("nav.collection").Should().Be("Collection");
        _i18n.Tr("nav.filters").Should().Be("Filters");
    }

    [Fact]
    public void FrenchLocale_ShouldReturnFrenchLabels()
    {
        var i18n = new LocalizationService("fr");

        i18n.CurrentLocale.Should().Be("fr");
        i18n.Tr("nav.filters").Should().Be("Filtres");
        i18n.Tr("nav.languages").Should().Contain("Langues");
    }

    [Fact]
    public void TranslationInterpolation_ShouldFormatNamedPlaceholders()
    {
        _i18n.SetLocale("fr");

        _i18n.Tr("stats.total", ("count", 150)).Should().Be("Total : 150");
        _i18n.Tr("stats.dat_completion", ("pct", "17.8"), ("matched", 38), ("total", 213))
            .Should().Be("Complétude : 17.8% (38/213)");
        _i18n.Tr("log.source_path", ("path", "/games/amiga"))
            .Should().Be("Source : /games/amiga");
        _i18n.Tr("dialog.worker_error_msg", ("error", "Disk full"))
            .Should().Contain("Disk full");
    }

    [Fact]
    public void SwitchToEnglish_ShouldReturnEnglishTranslations()
    {
        _i18n.SetLocale("en");

        _i18n.CurrentLocale.Should().Be("en");
        _i18n.Tr("nav.output").Should().Be("Output");
        _i18n.Tr("nav.filters").Should().Be("Filters");
        _i18n.Tr("nav.languages").Should().Be("Languages and 1G1R");
        _i18n.Tr("tab1.btn_browse").Should().Be("Browse...");
        _i18n.Tr("stats.total", ("count", 100)).Should().Be("Total: 100");
        _i18n.Tr("stats.dat_completion", ("pct", "17.8"), ("matched", 38), ("total", 213))
            .Should().Be("Completion: 17.8% (38/213)");
        _i18n.Tr("panel4.btn_simulate").Should().Be("Simulate (Dry-Run)");
        _i18n.Tr("reason.bad_dump_excluded").Should().Be("Bad Dump [b] excluded");
    }

    [Theory]
    [InlineData("fr")]
    [InlineData("en")]
    [InlineData("es")]
    public void BadDumpTag_ShouldBeTheSameForBothConventions(string locale)
    {
        // The filters tab shows one bad dump option for both conventions: No-Intro marks bad dumps with [b] as TOSEC
        // does, and "(Bad Dump)" appears in no No-Intro or Redump DAT
        _i18n.SetLocale(locale);

        _i18n.Tr("filters.tag_bad").Should().Be("[b]");
    }

    [Fact]
    public void FallbackMechanism_ShouldReturnDefaultOrKeyWhenKeyDoesNotExist()
    {
        _i18n.SetLocale("fr");

        _i18n.Tr("non.existent.key").Should().Be("non.existent.key");
        _i18n.Tr("non.existent.key", defaultValue: "Fallback Value").Should().Be("Fallback Value");
    }

    [Fact]
    public void GetAvailableLocales_ShouldListEnglishFrenchAndSpanishInThisOrder()
    {
        var codes = _i18n.GetAvailableLocales().Select(l => l.Code).ToList();
        codes.Should().Equal("en", "fr", "es");
    }

    /// <summary>The catalogs of every language, the French one being the reference.</summary>
    public static TheoryData<string> OtherLocales()
    {
        var data = new TheoryData<string>();
        foreach (var locale in LocalizationService.Translations.Keys.Where(l => l != "fr").Order(StringComparer.Ordinal))
        {
            data.Add(locale);
        }

        return data;
    }

    [Fact]
    public void SpanishLocale_ShouldTranslateAndFormatStructuredMessages()
    {
        var i18n = new LocalizationService();
        i18n.SetLocale("es-ES");

        i18n.CurrentLocale.Should().Be("es");
        i18n.Tr("panel4.btn_simulate").Should().Be("Simular (Dry-Run)");
        i18n.Format(LocalizedMessage.Create("reason.incomplete_multidisk_set", ("found", 1), ("total", 2)))
            .Should().Be("Conjunto de varios discos incompleto (1/2 discos encontrados)");
    }

    [Fact]
    public void DefaultCountriesPerLocale_ShouldReturnLocalizedCountryNames()
    {
        var countriesFr = _i18n.GetDefaultCountries("fr").ToDictionary(c => c.Code, c => c.Label);
        var countriesEn = _i18n.GetDefaultCountries("en").ToDictionary(c => c.Code, c => c.Label);

        countriesFr["US"].Should().Be("États-Unis");
        countriesEn["US"].Should().Be("United States");
        countriesFr["DE"].Should().Be("Allemagne");
        countriesEn["DE"].Should().Be("Germany");
        _i18n.GetDefaultCountries("es").Should().Contain(("ES", "España")).And.Contain(("US", "Estados Unidos"));
    }

    [Theory]
    [MemberData(nameof(OtherLocales))]
    public void EveryLocale_ShouldListTheSameCountriesAndLanguagesAsFrench(string locale)
    {
        LocalizationService.CountriesByLocale.Should().ContainKey(locale);
        LocalizationService.LanguagesByLocale.Should().ContainKey(locale);
        _i18n.GetDefaultCountries(locale).Select(c => c.Code)
            .Should().Equal(_i18n.GetDefaultCountries("fr").Select(c => c.Code));
        _i18n.GetDefaultLanguages(locale).Select(l => l.Code)
            .Should().Equal(_i18n.GetDefaultLanguages("fr").Select(l => l.Code));
        _i18n.GetDefaultCountries(locale).Should().OnlyContain(c => !string.IsNullOrWhiteSpace(c.Label));
    }

    [Fact]
    public void DefaultLanguagesPerLocale_ShouldReturnLocalizedLanguageNames()
    {
        var langsFr = _i18n.GetDefaultLanguages("fr").ToDictionary(l => l.Code, l => l.Label);
        var langsEn = _i18n.GetDefaultLanguages("en").ToDictionary(l => l.Code, l => l.Label);

        langsFr["fr"].Should().Contain("Français");
        langsEn["fr"].Should().Be("French");
        langsFr["en"].Should().Contain("Anglais");
        langsEn["en"].Should().Be("English");
        _i18n.GetDefaultLanguages("es").Should().Contain(l => l.Code == "es" && l.Label.StartsWith("Español"));
    }

    [Theory]
    [MemberData(nameof(OtherLocales))]
    public void LocalesKeyParity_EveryCatalogShouldHaveExactlyTheFrenchKeys(string locale)
    {
        var frKeys = LocalizationService.Translations["fr"].Keys.ToHashSet(StringComparer.Ordinal);
        var keys = LocalizationService.Translations[locale].Keys.ToHashSet(StringComparer.Ordinal);

        frKeys.Except(keys).Should().BeEmpty($"all French translation keys must also exist in '{locale}'");
        keys.Except(frKeys).Should().BeEmpty($"the '{locale}' catalog must not hold keys unknown in French");
        LocalizationService.Translations[locale].Where(e => string.IsNullOrWhiteSpace(e.Value) && !string.IsNullOrWhiteSpace(LocalizationService.Translations["fr"][e.Key]))
            .Should().BeEmpty("no translation may be left empty");
    }

    [Fact]
    public void FileDialogFilters_ShouldEachNameASingleFileType()
    {
        // A Qt-style "Category files (*.ini);;All files (*)" is shown by the pickers as one entry: "All files" could
        // never be chosen. Each file type is a filter of its own, with its own key.
        LocalizationService.Translations
            .SelectMany(catalog => catalog.Value
                .Where(e => e.Key.EndsWith("_filter", StringComparison.Ordinal) && e.Value.Contains(";;"))
                .Select(e => $"{catalog.Key}:{e.Key}"))
            .Should().BeEmpty();
    }

    [GeneratedRegex(@"(?:\bTr|LocalizedMessage\.Create)\(\s*""([^""]+)""", RegexOptions.CultureInvariant)]
    private static partial Regex TrCallRegex();

    [Fact]
    public void AllCodebaseTrKeysExist_EveryTrAndLocalizedMessageKeyShouldExistInBothCatalogs()
    {
        var catalogs = LocalizationService.Translations;

        // Locate src/RomSieve.Core directory relative to test execution or workspace root
        var currentDir = new DirectoryInfo(AppContext.BaseDirectory);
        while (currentDir is not null && !Directory.Exists(Path.Combine(currentDir.FullName, "src", "RomSieve.Core")))
        {
            currentDir = currentDir.Parent;
        }

        if (currentDir is null)
        {
            return;
        }

        var sourceDirs = new[] { "RomSieve.Core", "RomSieve.App", "RomSieve.Cli" }.Select(name => Path.Combine(currentDir.FullName, "src", name));
        var missing = new List<string>();

        foreach (var csFile in sourceDirs.SelectMany(dir => Directory.EnumerateFiles(dir, "*.cs", SearchOption.AllDirectories)))
        {
            var content = File.ReadAllText(csFile);
            foreach (Match m in TrCallRegex().Matches(content))
            {
                var key = m.Groups[1].Value;
                missing.AddRange(catalogs.Where(c => !c.Value.ContainsKey(key)).Select(c => $"{c.Key}: {key} ({Path.GetFileName(csFile)})"));
            }
        }

        missing.Should().BeEmpty("all keys passed to Tr() or LocalizedMessage.Create() must exist in every catalog");
    }

    [GeneratedRegex(@"\{(\w+)\}", RegexOptions.CultureInvariant)]
    private static partial Regex PlaceholderRegex();

    [Theory]
    [MemberData(nameof(OtherLocales))]
    public void LocalesPlaceholderParity_EveryKeyShouldTakeTheSameArgumentsInEveryLanguage(string locale)
    {
        static string[] Placeholders(string text) =>
            PlaceholderRegex().Matches(text).Select(m => m.Groups[1].Value).Distinct().Order(StringComparer.Ordinal).ToArray();

        var fr = LocalizationService.Translations["fr"];
        var other = LocalizationService.Translations[locale];
        var mismatches = fr.Keys
            .Where(other.ContainsKey)
            .Where(key => !Placeholders(fr[key]).SequenceEqual(Placeholders(other[key])))
            .ToList();

        mismatches.Should().BeEmpty("a structured message passes the same named arguments whatever the language");
    }

    [Fact]
    public void EmbeddedJsonResources_ShouldLoadTheFrenchEnglishAndSpanishTranslations()
    {
        LocalizationService.Translations.Keys.Should().BeEquivalentTo("fr", "en", "es");
        LocalizationService.Translations.Values.Should().OnlyContain(catalog => catalog.Count > 400);
    }

    [Fact]
    public void GetAvailableLocales_ShouldIncludeDynamicLabels()
    {
        var locales = _i18n.GetAvailableLocales();
        locales.Should().Contain(l => l.Code == "fr" && l.Label == "Français");
        locales.Should().Contain(l => l.Code == "en" && l.Label == "English");
        locales.Should().Contain(l => l.Code == "es" && l.Label == "Español");
    }

    [Theory]
    [InlineData("fr-FR", "fr")]
    [InlineData("fr-CA", "fr")]
    [InlineData("es-MX", "es")]
    [InlineData("en-GB", "en")]
    [InlineData("de-DE", "en")]
    [InlineData("ja-JP", "en")]
    public void LocaleOf_ShouldBeTheLanguageOfTheCultureWhenItHasACatalog(string culture, string expected)
    {
        LocalizationService.LocaleOf(CultureInfo.GetCultureInfo(culture)).Should().Be(expected);
        LocalizationService.LocaleOf(CultureInfo.InvariantCulture).Should().Be(LocalizationService.DefaultLocale);
    }
}
