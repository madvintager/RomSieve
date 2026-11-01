using System.Globalization;
using System.Text.Json;
using RomSieve.Core.Abstractions;
using RomSieve.Core.Models;

namespace RomSieve.Core.Services;

/// <summary>
/// Internationalization (i18n) and localization (l10n) service for RomSieve.
/// </summary>
public sealed class LocalizationService : ILocalizationService
{
    public const string DefaultLocale = "en";

    private string _currentLocale = DefaultLocale;

    public LocalizationService()
    {
    }

    /// <summary>Starts in the given language (the default one when it has no catalog).</summary>
    public LocalizationService(string locale) => SetLocale(locale);

    /// <summary>
    /// Language of the interface for the language of <paramref name="culture"/> (that of the system, at the first
    /// launch): its catalog when there is one, otherwise <see cref="DefaultLocale"/>.
    /// </summary>
    public static string LocaleOf(CultureInfo culture) =>
        Translations.ContainsKey(culture.TwoLetterISOLanguageName.ToLowerInvariant())
            ? culture.TwoLetterISOLanguageName.ToLowerInvariant()
            : DefaultLocale;

    public string CurrentLocale => _currentLocale;

    public event EventHandler? LocaleChanged;

    public void SetLocale(string locale)
    {
        var normalized = (locale ?? string.Empty).Trim().ToLowerInvariant();
        string newLocale;
        if (Translations.ContainsKey(normalized))
        {
            newLocale = normalized;
        }
        else if (normalized.Contains('_') && Translations.ContainsKey(normalized.Split('_')[0]))
        {
            newLocale = normalized.Split('_')[0];
        }
        else if (normalized.Contains('-') && Translations.ContainsKey(normalized.Split('-')[0]))
        {
            newLocale = normalized.Split('-')[0];
        }
        else
        {
            newLocale = DefaultLocale;
        }

        if (_currentLocale != newLocale)
        {
            _currentLocale = newLocale;
            LocaleChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private static readonly Dictionary<string, string> KnownLocaleLabels = new(StringComparer.OrdinalIgnoreCase)
    {
        ["fr"] = "Français",
        ["en"] = "English",
        ["de"] = "Deutsch",
        ["es"] = "Español",
        ["it"] = "Italiano",
        ["ja"] = "日本語",
        ["pt"] = "Português",
        ["ru"] = "Русский"
    };

    public IReadOnlyList<(string Code, string Label)> GetAvailableLocales()
    {
        var list = new List<(string Code, string Label)>();
        var orderedKeys = Translations.Keys
            .OrderBy(k => k switch
            {
                "en" => 0,
                "fr" => 1,
                _ => 2
            })
            .ThenBy(k => k, StringComparer.OrdinalIgnoreCase);

        foreach (var code in orderedKeys)
        {
            var label = KnownLocaleLabels.TryGetValue(code, out var known) ? known : code.ToUpperInvariant();
            list.Add((code, label));
        }

        return list;
    }

    public string Tr(string key, string? defaultValue = null, IReadOnlyDictionary<string, object?>? args = null)
    {
        string? template = null;
        if (Translations.TryGetValue(_currentLocale, out var localeDict))
        {
            localeDict.TryGetValue(key, out template);
        }

        if (template is null && _currentLocale != DefaultLocale && Translations.TryGetValue(DefaultLocale, out var defaultDict))
        {
            defaultDict.TryGetValue(key, out template);
        }

        template ??= defaultValue ?? key;

        if (args is { Count: > 0 })
        {
            var formatted = template;
            foreach (var (k, v) in args)
            {
                formatted = formatted.Replace($"{{{k}}}", v?.ToString() ?? string.Empty, StringComparison.Ordinal);
            }
            return formatted;
        }

        return template;
    }

    public string Tr(string key, params (string Key, object? Value)[] args)
    {
        if (args is null || args.Length == 0)
        {
            return Tr(key, defaultValue: null, args: null);
        }

        var dict = new Dictionary<string, object?>(args.Length, StringComparer.Ordinal);
        foreach (var (k, v) in args)
        {
            dict[k] = v;
        }
        return Tr(key, defaultValue: null, dict);
    }

    public string Format(LocalizedMessage? message)
    {
        if (message is null)
        {
            return string.Empty;
        }

        if (message.Args.Count == 0)
        {
            return Tr(message.Key, defaultValue: null, args: null);
        }

        var args = new Dictionary<string, object?>(message.Args.Count, StringComparer.Ordinal);
        foreach (var (name, value) in message.Args)
        {
            args[name] = value is LocalizedMessage nested ? Format(nested) : value;
        }
        return Tr(message.Key, defaultValue: null, args);
    }

    public IReadOnlyList<(string Code, string Label)> GetDefaultCountries(string? locale = null)
    {
        var loc = (locale ?? _currentLocale).ToLowerInvariant();
        return CountriesByLocale.TryGetValue(loc, out var list) ? list : CountriesByLocale[DefaultLocale];
    }

    public IReadOnlyList<(string Code, string Label)> GetDefaultLanguages(string? locale = null)
    {
        var loc = (locale ?? _currentLocale).ToLowerInvariant();
        return LanguagesByLocale.TryGetValue(loc, out var list) ? list : LanguagesByLocale[DefaultLocale];
    }

    public static readonly IReadOnlyDictionary<string, IReadOnlyList<(string Code, string Label)>> CountriesByLocale =
        new Dictionary<string, IReadOnlyList<(string Code, string Label)>>(StringComparer.OrdinalIgnoreCase)
        {
            ["fr"] =
            [
                ("FR", "France"), ("EU", "Europe"), ("WO", "Monde (World)"), ("US", "États-Unis"),
                ("GB", "Royaume-Uni"), ("DE", "Allemagne"), ("JP", "Japon"), ("IT", "Italie"),
                ("ES", "Espagne"), ("NL", "Pays-Bas"), ("PT", "Portugal"), ("BE", "Belgique"),
                ("CH", "Suisse"), ("AT", "Autriche"), ("SE", "Suède"), ("NO", "Norvège"),
                ("DK", "Danemark"), ("FI", "Finlande"), ("SCAN", "Scandinavie"), ("AU", "Australie"),
                ("CA", "Canada"), ("NZ", "Nouvelle-Zélande"), ("IE", "Irlande"), ("BR", "Brésil"),
                ("MX", "Mexique"), ("AR", "Argentine"), ("LA", "Amérique latine"), ("KR", "Corée du Sud"),
                ("CN", "Chine"), ("TW", "Taïwan"), ("HK", "Hong Kong"), ("AS", "Asie"),
                ("PL", "Pologne"), ("RU", "Russie"), ("GR", "Grèce"), ("CZ", "République tchèque"), ("HU", "Hongrie")
            ],
            ["en"] =
            [
                ("FR", "France"), ("EU", "Europe"), ("WO", "World"), ("US", "United States"),
                ("GB", "United Kingdom"), ("DE", "Germany"), ("JP", "Japan"), ("IT", "Italy"),
                ("ES", "Spain"), ("NL", "Netherlands"), ("PT", "Portugal"), ("BE", "Belgium"),
                ("CH", "Switzerland"), ("AT", "Austria"), ("SE", "Sweden"), ("NO", "Norway"),
                ("DK", "Denmark"), ("FI", "Finland"), ("SCAN", "Scandinavia"), ("AU", "Australia"),
                ("CA", "Canada"), ("NZ", "New Zealand"), ("IE", "Ireland"), ("BR", "Brazil"),
                ("MX", "Mexico"), ("AR", "Argentina"), ("LA", "Latin America"), ("KR", "South Korea"),
                ("CN", "China"), ("TW", "Taiwan"), ("HK", "Hong Kong"), ("AS", "Asia"),
                ("PL", "Poland"), ("RU", "Russia"), ("GR", "Greece"), ("CZ", "Czech Republic"), ("HU", "Hungary")
            ],
            ["es"] =
            [
                ("FR", "Francia"), ("EU", "Europa"), ("WO", "Mundo (World)"), ("US", "Estados Unidos"),
                ("GB", "Reino Unido"), ("DE", "Alemania"), ("JP", "Japón"), ("IT", "Italia"),
                ("ES", "España"), ("NL", "Países Bajos"), ("PT", "Portugal"), ("BE", "Bélgica"),
                ("CH", "Suiza"), ("AT", "Austria"), ("SE", "Suecia"), ("NO", "Noruega"),
                ("DK", "Dinamarca"), ("FI", "Finlandia"), ("SCAN", "Escandinavia"), ("AU", "Australia"),
                ("CA", "Canadá"), ("NZ", "Nueva Zelanda"), ("IE", "Irlanda"), ("BR", "Brasil"),
                ("MX", "México"), ("AR", "Argentina"), ("LA", "Latinoamérica"), ("KR", "Corea del Sur"),
                ("CN", "China"), ("TW", "Taiwán"), ("HK", "Hong Kong"), ("AS", "Asia"),
                ("PL", "Polonia"), ("RU", "Rusia"), ("GR", "Grecia"), ("CZ", "República Checa"), ("HU", "Hungría")
            ]
        };

    public static readonly IReadOnlyDictionary<string, IReadOnlyList<(string Code, string Label)>> LanguagesByLocale =
        new Dictionary<string, IReadOnlyList<(string Code, string Label)>>(StringComparer.OrdinalIgnoreCase)
        {
            ["fr"] =
            [
                ("M", "Multilingue (flags M / M2 à M9)"), ("en", "Anglais (English)"), ("fr", "Français (French)"),
                ("de", "Allemand (German)"), ("es", "Espagnol (Spanish)"), ("it", "Italien (Italian)"),
                ("ja", "Japonais (Japanese)"), ("nl", "Néerlandais (Dutch)"), ("pt", "Portugais (Portuguese)"),
                ("sv", "Suédois (Swedish)"), ("no", "Norvégien (Norwegian)"), ("da", "Danois (Danish)"),
                ("fi", "Finnois (Finnish)"), ("pl", "Polonais (Polish)"), ("ru", "Russe (Russian)"),
                ("ar", "Arabe (Arabic)"), ("bg", "Bulgare (Bulgarian)"), ("bs", "Bosnien (Bosnian)"),
                ("cs", "Tchèque (Czech)"), ("cy", "Gallois (Welsh)"), ("el", "Grec (Greek)"),
                ("eo", "Espéranto (Esperanto)"), ("et", "Estonien (Estonian)"), ("fa", "Persan (Persian)"),
                ("ga", "Irlandais (Irish)"), ("gu", "Gujarati"), ("he", "Hébreu (Hebrew)"),
                ("hi", "Hindi"), ("hr", "Croate (Croatian)"), ("hu", "Hongrois (Hungarian)"),
                ("is", "Islandais (Icelandic)"), ("ko", "Coréen (Korean)"), ("lt", "Lituanien (Lithuanian)"),
                ("lv", "Letton (Latvian)"), ("ms", "Malais (Malay)"), ("ro", "Roumain (Romanian)"),
                ("sk", "Slovaque (Slovakian)"), ("sl", "Slovène (Slovenian)"), ("sq", "Albanais (Albanian)"),
                ("sr", "Serbe (Serbian)"), ("th", "Thaï (Thai)"), ("tr", "Turc (Turkish)"),
                ("ur", "Ourdou (Urdu)"), ("vi", "Vietnamien (Vietnamese)"), ("yi", "Yiddish"), ("zh", "Chinois (Chinese)")
            ],
            ["en"] =
            [
                ("M", "Multilingual (flags M / M2 to M9)"), ("en", "English"), ("fr", "French"),
                ("de", "German"), ("es", "Spanish"), ("it", "Italian"),
                ("ja", "Japanese"), ("nl", "Dutch"), ("pt", "Portuguese"),
                ("sv", "Swedish"), ("no", "Norwegian"), ("da", "Danish"),
                ("fi", "Finnish"), ("pl", "Polish"), ("ru", "Russian"),
                ("ar", "Arabic"), ("bg", "Bulgarian"), ("bs", "Bosnian"),
                ("cs", "Czech"), ("cy", "Welsh"), ("el", "Greek"),
                ("eo", "Esperanto"), ("et", "Estonian"), ("fa", "Persian"),
                ("ga", "Irish"), ("gu", "Gujarati"), ("he", "Hebrew"),
                ("hi", "Hindi"), ("hr", "Croatian"), ("hu", "Hungarian"),
                ("is", "Icelandic"), ("ko", "Korean"), ("lt", "Lithuanian"),
                ("lv", "Latvian"), ("ms", "Malay"), ("ro", "Romanian"),
                ("sk", "Slovak"), ("sl", "Slovenian"), ("sq", "Albanian"),
                ("sr", "Serbian"), ("th", "Thai"), ("tr", "Turkish"),
                ("ur", "Urdu"), ("vi", "Vietnamese"), ("yi", "Yiddish"), ("zh", "Chinese")
            ],
            ["es"] =
            [
                ("M", "Multilingüe (indicadores M / M2 a M9)"), ("en", "Inglés (English)"), ("fr", "Francés (French)"),
                ("de", "Alemán (German)"), ("es", "Español (Spanish)"), ("it", "Italiano (Italian)"),
                ("ja", "Japonés (Japanese)"), ("nl", "Neerlandés (Dutch)"), ("pt", "Portugués (Portuguese)"),
                ("sv", "Sueco (Swedish)"), ("no", "Noruego (Norwegian)"), ("da", "Danés (Danish)"),
                ("fi", "Finés (Finnish)"), ("pl", "Polaco (Polish)"), ("ru", "Ruso (Russian)"),
                ("ar", "Árabe (Arabic)"), ("bg", "Búlgaro (Bulgarian)"), ("bs", "Bosnio (Bosnian)"),
                ("cs", "Checo (Czech)"), ("cy", "Galés (Welsh)"), ("el", "Griego (Greek)"),
                ("eo", "Esperanto"), ("et", "Estonio (Estonian)"), ("fa", "Persa (Persian)"),
                ("ga", "Irlandés (Irish)"), ("gu", "Guyaratí (Gujarati)"), ("he", "Hebreo (Hebrew)"),
                ("hi", "Hindi"), ("hr", "Croata (Croatian)"), ("hu", "Húngaro (Hungarian)"),
                ("is", "Islandés (Icelandic)"), ("ko", "Coreano (Korean)"), ("lt", "Lituano (Lithuanian)"),
                ("lv", "Letón (Latvian)"), ("ms", "Malayo (Malay)"), ("ro", "Rumano (Romanian)"),
                ("sk", "Eslovaco (Slovak)"), ("sl", "Esloveno (Slovenian)"), ("sq", "Albanés (Albanian)"),
                ("sr", "Serbio (Serbian)"), ("th", "Tailandés (Thai)"), ("tr", "Turco (Turkish)"),
                ("ur", "Urdu"), ("vi", "Vietnamita (Vietnamese)"), ("yi", "Yidis (Yiddish)"), ("zh", "Chino (Chinese)")
            ]
        };

    public static readonly IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> Translations = LoadTranslations();

    private static IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> LoadTranslations()
    {
        var result = new Dictionary<string, IReadOnlyDictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
        var assembly = typeof(LocalizationService).Assembly;

        foreach (var resourceName in assembly.GetManifestResourceNames())
        {
            if (!resourceName.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var parts = resourceName.Split('.');
            if (parts.Length >= 2)
            {
                var locale = parts[^2].ToLowerInvariant();
                using var stream = assembly.GetManifestResourceStream(resourceName);
                if (stream is not null)
                {
                    try
                    {
                        var dict = JsonSerializer.Deserialize<Dictionary<string, string>>(stream);
                        if (dict is not null)
                        {
                            result[locale] = new Dictionary<string, string>(dict, StringComparer.Ordinal);
                        }
                    }
                    catch
                    {
                        // Ignore malformed JSON resource during initialization
                    }
                }
            }
        }

        if (!result.ContainsKey(DefaultLocale))
        {
            result[DefaultLocale] = new Dictionary<string, string>(StringComparer.Ordinal);
        }
        if (!result.ContainsKey("en"))
        {
            result["en"] = new Dictionary<string, string>(StringComparer.Ordinal);
        }

        return result;
    }
}
