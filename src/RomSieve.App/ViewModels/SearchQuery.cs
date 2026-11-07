using System.Globalization;
using System.Text;

namespace RomSieve.App.ViewModels;

/// <summary>
/// A search of the interface (table of the results, DATs of a lot), read as terms: every term must be found, in any
/// order, each in one of the fields searched; a term led by <c>-</c> must be found in none; words between double quotes
/// are one term. Case and accents are ignored, and a term matches a part of a word (<c>d64</c> finds <c>[D64]</c>).
/// A lone <c>-</c> is no term, so a name copied with its dashes (<c>Commodore C64 - Games</c>) still finds itself.
/// </summary>
public sealed class SearchQuery
{
    public static readonly SearchQuery Empty = new([], []);

    private readonly string[] _included;
    private readonly string[] _excluded;

    private SearchQuery(string[] included, string[] excluded)
    {
        _included = included;
        _excluded = excluded;
    }

    /// <summary>Whether the search has no term: everything matches.</summary>
    public bool IsEmpty => _included.Length == 0 && _excluded.Length == 0;

    /// <summary>Terms to find, normalized.</summary>
    public IReadOnlyList<string> Included => _included;

    /// <summary>Terms to leave out, normalized, without their <c>-</c>.</summary>
    public IReadOnlyList<string> Excluded => _excluded;

    /// <summary>Reads the text typed in a search box.</summary>
    public static SearchQuery Parse(string? text)
    {
        var normalized = Normalize(text);
        if (normalized.Length == 0)
        {
            return Empty;
        }

        var included = new List<string>();
        var excluded = new List<string>();
        var i = 0;
        while (i < normalized.Length)
        {
            if (char.IsWhiteSpace(normalized[i]))
            {
                i++;
                continue;
            }

            var exclude = normalized[i] == '-' && i + 1 < normalized.Length && !char.IsWhiteSpace(normalized[i + 1]);
            if (exclude)
            {
                i++;
            }

            string term;
            if (normalized[i] == '"')
            {
                // Words between double quotes, up to the closing quote or the end of the text
                var close = normalized.IndexOf('"', i + 1);
                var end = close < 0 ? normalized.Length : close;
                term = normalized[(i + 1)..end].Trim();
                i = close < 0 ? normalized.Length : close + 1;
            }
            else
            {
                var start = i;
                while (i < normalized.Length && !char.IsWhiteSpace(normalized[i]))
                {
                    i++;
                }

                term = normalized[start..i];
            }

            if (term.Length == 0 || term == "-")
            {
                continue;
            }

            (exclude ? excluded : included).Add(term);
        }

        return included.Count == 0 && excluded.Count == 0 ? Empty : new SearchQuery([.. included], [.. excluded]);
    }

    /// <summary>
    /// Text a search box shows to find <paramref name="phrase"/> as it is: between double quotes when it has several
    /// words (and no quote of its own), so that its words are not found apart.
    /// </summary>
    public static string Exact(string phrase)
    {
        var trimmed = phrase.Trim();
        return trimmed.Any(char.IsWhiteSpace) && !trimmed.Contains('"') ? $"\"{trimmed}\"" : trimmed;
    }

    /// <summary>
    /// Whether the fields match: each term to find is in one of them, no term to leave out is in any. The fields are
    /// normalized by <see cref="Normalize"/>.
    /// </summary>
    public bool Matches(params ReadOnlySpan<string> normalizedFields)
    {
        foreach (var term in _excluded)
        {
            if (AnyContains(normalizedFields, term))
            {
                return false;
            }
        }

        foreach (var term in _included)
        {
            if (!AnyContains(normalizedFields, term))
            {
                return false;
            }
        }

        return true;
    }

    private static bool AnyContains(ReadOnlySpan<string> fields, string term)
    {
        foreach (var field in fields)
        {
            if (field.Contains(term, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Text in lower case and without accents, as the search compares it.</summary>
    public static string Normalize(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        var decomposed = text.Trim().ToLowerInvariant().Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(decomposed.Length);
        foreach (var ch in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(ch) != UnicodeCategory.NonSpacingMark)
            {
                sb.Append(ch);
            }
        }

        return sb.ToString().Normalize(NormalizationForm.FormC);
    }
}
