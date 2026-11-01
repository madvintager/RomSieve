namespace RomSieve.Core.Models;

/// <summary>
/// Message stored as a translation key and its arguments, and translated only when displayed
/// (<c>ILocalizationService.Format</c>), so that it follows the active language.
/// An argument can itself be a <see cref="LocalizedMessage"/>, translated in turn.
/// </summary>
public sealed record LocalizedMessage
{
    private static readonly IReadOnlyDictionary<string, object?> NoArgs = new Dictionary<string, object?>(StringComparer.Ordinal);

    public LocalizedMessage(string key, IReadOnlyDictionary<string, object?>? args = null)
    {
        Key = key;
        Args = args ?? NoArgs;
    }

    /// <summary>Translation key (e.g. <c>reason.crack_excluded</c>).</summary>
    public string Key { get; }

    /// <summary>Values of the <c>{name}</c> placeholders of the translation.</summary>
    public IReadOnlyDictionary<string, object?> Args { get; }

    public static LocalizedMessage Create(string key, params (string Name, object? Value)[] args)
    {
        if (args.Length == 0)
        {
            return new LocalizedMessage(key);
        }

        var dict = new Dictionary<string, object?>(args.Length, StringComparer.Ordinal);
        foreach (var (name, value) in args)
        {
            dict[name] = value;
        }
        return new LocalizedMessage(key, dict);
    }

    /// <summary>
    /// Wraps a text that is not translatable, or already translated (e.g. an exception message), as a message.
    /// </summary>
    public static LocalizedMessage Verbatim(string text) => Create(VerbatimKey, ("text", text));

    /// <summary>Translation key whose text is its single <c>{text}</c> argument, in every language.</summary>
    public const string VerbatimKey = "text.verbatim";

    public bool Equals(LocalizedMessage? other)
    {
        if (other is null)
        {
            return false;
        }
        if (ReferenceEquals(this, other))
        {
            return true;
        }
        if (!string.Equals(Key, other.Key, StringComparison.Ordinal) || Args.Count != other.Args.Count)
        {
            return false;
        }

        foreach (var (name, value) in Args)
        {
            if (!other.Args.TryGetValue(name, out var otherValue) || !Equals(value, otherValue))
            {
                return false;
            }
        }
        return true;
    }

    public override int GetHashCode() => HashCode.Combine(StringComparer.Ordinal.GetHashCode(Key), Args.Count);

    /// <summary>Untranslated form, for diagnostics only.</summary>
    public override string ToString() =>
        Args.Count == 0 ? Key : $"{Key}({string.Join(", ", Args.Select(a => $"{a.Key}={a.Value}"))})";
}
