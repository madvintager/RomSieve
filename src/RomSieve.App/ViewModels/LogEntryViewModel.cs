using System.Text.RegularExpressions;
using RomSieve.Core.Abstractions;
using RomSieve.Core.Models;

namespace RomSieve.App.ViewModels;

/// <summary>
/// Log entry with automatic deduplication of redundant prefixes
/// (<c>[OK]</c>, <c>[SKIP]</c>, <c>[UNMATCHED]</c>, <c>[PLANNED]</c>, <c>[INFO]</c>, <c>[WARN]</c>, <c>[ERROR]</c>, <c>[ERREUR]</c>).
/// The view colors the level tag with the <c>LogLevel{Level}Brush</c> theme resource.
/// </summary>
public sealed partial record LogEntryViewModel(
    string Level,
    string CleanMessage,
    string FormattedLine)
{
    /// <summary>Structured message of the entry, translated again when the interface language changes.</summary>
    public LocalizedMessage? Source { get; init; }

    /// <summary>Creates an entry from a structured message, translated in the active language.</summary>
    public static LogEntryViewModel Create(LogLevel level, LocalizedMessage message, ILocalizationService localization) =>
        Create(level.ToTag(), localization.Format(message)) with { Source = message };

    /// <summary>Translates the entry again in the active language (no-op for an entry without structured message).</summary>
    public LogEntryViewModel Retranslate(ILocalizationService localization) =>
        Source is null ? this : Create(Level, localization.Format(Source)) with { Source = Source };

    [GeneratedRegex(@"^\s*\[(OK|SKIP|UNMATCHED|PLANNED|INFO|WARN|WARNING|ERROR|ERREUR)\]\s*", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex LeadingLevelTagRegex();

    /// <summary>Bracketed prefix tag (e.g. <c>[INFO]</c>, <c>[OK]</c>, <c>[SKIP]</c>).</summary>
    public string LevelTag => $"[{Level}]";

    /// <summary>Message preceded by a space for inline display following <see cref="LevelTag"/>.</summary>
    public string SpacedCleanMessage => $" {CleanMessage}";

    public static LogEntryViewModel Create(string level, string message)
    {
        var normalizedLevel = string.IsNullOrWhiteSpace(level)
            ? "INFO"
            : level.Trim().ToUpperInvariant();

        if (normalizedLevel == "WARNING")
        {
            normalizedLevel = "WARN";
        }
        else if (normalizedLevel == "ERREUR")
        {
            normalizedLevel = "ERROR";
        }

        var cleanMsg = LeadingLevelTagRegex().Replace(message ?? string.Empty, string.Empty);
        var formatted = $"[{normalizedLevel}] {cleanMsg}";

        return new LogEntryViewModel(normalizedLevel, cleanMsg, formatted);
    }
}
