using System.Globalization;
using System.IO;
using System.Text.Encodings.Web;
using System.Text.Json;
using RomSieve.Core.Abstractions;
using RomSieve.Core.Models;

namespace RomSieve.Core.Services;

/// <summary>
/// The choice of the user for each patch, in <c>patch_links.json</c> next to the configuration file in use
/// (<see cref="IPatchLinkService"/>). The file is small and read again at each use: nothing is kept in memory but
/// whether an unreadable file had to be left as it is.
/// </summary>
/// <remarks>
/// Format (<see cref="FormatVersion"/>):
/// <code>
/// { "version": 1, "links": [
///   { "patch_sha1": "…", "patch_size": 1234, "patch_name": "Game (T-Fr).ips", "kind": "origin",
///     "rom_crc32": "47e70e08", "rom_name": "Game (Japan).sfc" },
///   { "patch_sha1": "…", "patch_size": 99, "patch_name": "Game (T-En).bps", "kind": "ignored" } ] }
/// </code>
/// A file without <c>version</c> is read in the current format. A newer version is a format this build does not know:
/// the file is neither read nor written. An entry this build cannot read is written back as it was.
/// </remarks>
public sealed class PatchLinkService : IPatchLinkService
{
    public const string FileName = "patch_links.json";

    /// <summary>Written in the file; to raise only for a change an older build would misread.</summary>
    internal const int FormatVersion = 1;

    private const string KindOrigin = "origin";
    private const string KindIgnored = "ignored";

    private static readonly JsonWriterOptions WriterOptions = new()
    {
        Indented = true,

        // A file of the user, never served: the names of the patches stay readable
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private readonly IChecksumService _checksumService;
    private readonly ICrcCacheService _crcCache;
    private readonly object _lock = new();

    // An unreadable file that could not be copied aside: left as it is, never read nor written again in this session
    private bool _keptUnreadable;

    public PatchLinkService(string filePath, IChecksumService checksumService, ICrcCacheService crcCache)
    {
        FilePath = filePath;
        _checksumService = checksumService;
        _crcCache = crcCache;
    }

    public string FilePath { get; }

    /// <summary>The file next to a configuration file, as the journals, the profiles and the CRC cache are placed.</summary>
    public static string PathNextTo(string configFilePath) =>
        Path.Combine(Path.GetDirectoryName(Path.GetFullPath(configFilePath)) ?? string.Empty, FileName);

    public PatchLinkSnapshot Load()
    {
        lock (_lock)
        {
            var state = Read();
            return new PatchLinkSnapshot(state.Links, state.Warning);
        }
    }

    public Task<bool> UpdateAsync(
        IReadOnlyCollection<KeyValuePair<PatchKey, PatchLink?>> changes,
        Action<LogLevel, LocalizedMessage> log,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(changes);
        ArgumentNullException.ThrowIfNull(log);
        if (changes.Any(c => c.Value is { Kind: PatchLinkKind.Origin, OriginCrc32: null }))
        {
            throw new ArgumentException("A patch applied to a ROM needs the CRC32 of that ROM.", nameof(changes));
        }

        // Off the calling thread: the write is flushed to the disk
        return Task.Run(() => Update(changes, log), cancellationToken);
    }

    public async Task<IReadOnlyList<PatchOriginSuggestion>> GetOriginFormsAsync(
        ProcessItem origin,
        bool detectCopierHeaders,
        CancellationToken cancellationToken = default)
    {
        if (PatchOriginForms.IdentifiedForm(origin) is not { } identified)
        {
            return [];
        }

        var forms = new List<(long Offset, long Size, uint Crc32, bool OldInesHeader)> { (identified.Offset, identified.Size, identified.Crc32, false) };
        if (detectCopierHeaders)
        {
            var other = await PatchOriginForms.OtherFormAsync(origin, _ => true, _checksumService, _crcCache, cancellationToken).ConfigureAwait(false);
            if (other is { } otherForm)
            {
                forms.Add((otherForm.Offset, otherForm.Size, otherForm.Crc32, false));
            }

            // A NES file under an old iNES 1.0 header: the form the checksums of older patches and readmes give
            if (await PatchOriginForms.OldInesFormAsync(origin, other, _checksumService, cancellationToken).ConfigureAwait(false) is { } old)
            {
                forms.Add((0, old.Size, old.Crc32, true));
            }
        }

        // The whole file first (its own header, then the old one), the data without header last
        var largest = forms.Max(f => f.Size);
        return [.. forms
            .OrderBy(f => f.Offset)
            .ThenBy(f => f.OldInesHeader)
            .Select(f => new PatchOriginSuggestion(origin, f.Crc32, f.Offset, forms.Count > 1 ? f.Size == largest : null) { IsOldInesHeader = f.OldInesHeader })];
    }

    // =========================================================================
    // Reading
    // =========================================================================

    /// <summary>What the file holds: the choices, the entries this build cannot read, and whether it may be written.</summary>
    private sealed record FileState(
        Dictionary<PatchKey, PatchLink> Links,
        List<JsonElement> Unknown,
        LocalizedMessage? Warning,
        bool IsWritable);

    private FileState Read()
    {
        if (_keptUnreadable)
        {
            return Refused("log.patch_links_unreadable_kept");
        }

        byte[] bytes;
        try
        {
            if (!File.Exists(FilePath))
            {
                return new FileState([], [], null, IsWritable: true);
            }

            bytes = File.ReadAllBytes(FilePath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Locked or denied: perhaps for a moment only, so the file is not set aside
            return new FileState([], [], LocalizedMessage.Create(
                "log.patch_links_read_failed",
                ("path", FilePath),
                ("error", LocalizedMessage.Verbatim(ex.Message))), IsWritable: false);
        }

        return Parse(bytes) switch
        {
            null => SetAsideUnreadable(),
            { Version: > FormatVersion } => Refused("log.patch_links_newer"),
            var parsed => new FileState(parsed.Links, parsed.Unknown, null, IsWritable: true)
        };
    }

    private FileState Refused(string key) =>
        new([], [], LocalizedMessage.Create(key, ("path", FilePath)), IsWritable: false);

    /// <summary>
    /// An unreadable file is copied aside, then emptied, so that the copy is made once; a file that cannot be copied is
    /// left as it is for the whole session.
    /// </summary>
    private FileState SetAsideUnreadable()
    {
        if (ConfigurationService.PreserveUnreadableFile(FilePath) is { } copy)
        {
            try
            {
                Write(new Dictionary<PatchKey, PatchLink>(), []);
                return new FileState([], [], LocalizedMessage.Create(
                    "log.patch_links_unreadable_preserved",
                    ("path", FilePath),
                    ("copy", copy)), IsWritable: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // The copy exists, but the file stays unreadable: left as it is, so that it is not copied at each read
            }
        }

        _keptUnreadable = true;
        return Refused("log.patch_links_unreadable_kept");
    }

    private sealed record ParsedFile(int Version, Dictionary<PatchKey, PatchLink> Links, List<JsonElement> Unknown);

    /// <summary>The content of the file; <see langword="null"/> when it is not a file of associations.</summary>
    private static ParsedFile? Parse(byte[] bytes)
    {
        ReadOnlyMemory<byte> json = bytes;
        if (json.Span.StartsWith((ReadOnlySpan<byte>)[0xEF, 0xBB, 0xBF]))
        {
            json = json[3..];
        }

        if (json.Span.Trim((ReadOnlySpan<byte>)" \t\r\n"u8).IsEmpty)
        {
            // An empty file holds no choice: nothing to set aside
            return new ParsedFile(FormatVersion, [], []);
        }

        try
        {
            using var doc = JsonDocument.Parse(json, new JsonDocumentOptions
            {
                AllowTrailingCommas = true,
                CommentHandling = JsonCommentHandling.Skip
            });
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            var version = FormatVersion;
            // TryGetInt32 throws on a value that is not a number
            if (root.TryGetProperty("version", out var versionElement)
                && (versionElement.ValueKind != JsonValueKind.Number || !versionElement.TryGetInt32(out version)))
            {
                return null;
            }

            if (version > FormatVersion)
            {
                return new ParsedFile(version, [], []);
            }

            var links = new Dictionary<PatchKey, PatchLink>();
            var unknown = new List<JsonElement>();
            if (root.TryGetProperty("links", out var entries))
            {
                if (entries.ValueKind != JsonValueKind.Array)
                {
                    return null;
                }

                foreach (var entry in entries.EnumerateArray())
                {
                    if (ReadLink(entry) is { } read)
                    {
                        links[read.Key] = read.Link;
                    }
                    else
                    {
                        unknown.Add(entry.Clone());
                    }
                }
            }

            return new ParsedFile(version, links, unknown);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static (PatchKey Key, PatchLink Link)? ReadLink(JsonElement entry)
    {
        if (entry.ValueKind != JsonValueKind.Object
            || !TryGetString(entry, "patch_sha1", out var sha1)
            || !PatchKey.IsValidSha1(sha1)
            || !entry.TryGetProperty("patch_size", out var sizeElement)
            || sizeElement.ValueKind != JsonValueKind.Number
            || !sizeElement.TryGetInt64(out var size)
            || size < 0
            || !TryGetString(entry, "kind", out var kind))
        {
            return null;
        }

        var key = new PatchKey(sha1, size);
        var patchName = TryGetString(entry, "patch_name", out var name) ? name : string.Empty;
        switch (kind)
        {
            case KindIgnored:
                return (key, PatchLink.Ignore(patchName));
            case KindOrigin when TryGetString(entry, "rom_crc32", out var crcText)
                && crcText.Length == 8
                && uint.TryParse(crcText, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var crc):
                var romName = TryGetString(entry, "rom_name", out var rom) ? rom : string.Empty;
                return (key, PatchLink.ToOrigin(patchName, crc, romName));
            default:
                return null;
        }
    }

    private static bool TryGetString(JsonElement entry, string property, out string value)
    {
        if (entry.TryGetProperty(property, out var element) && element.ValueKind == JsonValueKind.String)
        {
            value = element.GetString() ?? string.Empty;
            return true;
        }

        value = string.Empty;
        return false;
    }

    // =========================================================================
    // Writing
    // =========================================================================

    private bool Update(IReadOnlyCollection<KeyValuePair<PatchKey, PatchLink?>> changes, Action<LogLevel, LocalizedMessage> log)
    {
        lock (_lock)
        {
            // Read again: a choice made meanwhile by the command line is kept
            var state = Read();
            if (!state.IsWritable)
            {
                log(LogLevel.Error, state.Warning!);
                return false;
            }

            if (state.Warning is { } warning)
            {
                log(LogLevel.Warn, warning);
            }

            foreach (var (key, link) in changes)
            {
                if (link is null)
                {
                    state.Links.Remove(key);
                }
                else
                {
                    state.Links[key] = link;
                }
            }

            try
            {
                Write(state.Links, state.Unknown);
                return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                log(LogLevel.Error, LocalizedMessage.Create(
                    "log.patch_links_write_failed",
                    ("path", FilePath),
                    ("error", LocalizedMessage.Verbatim(ex.Message))));
                return false;
            }
        }
    }

    private void Write(IReadOnlyDictionary<PatchKey, PatchLink> links, IReadOnlyList<JsonElement> unknown)
    {
        var dir = Path.GetDirectoryName(FilePath);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        AtomicFile.Write(FilePath, stream =>
        {
            using var writer = new Utf8JsonWriter(stream, WriterOptions);
            writer.WriteStartObject();
            writer.WriteNumber("version", FormatVersion);
            writer.WriteStartArray("links");

            // In the order of the names, so that the file reads well and changes little from one write to the next
            foreach (var (key, link) in links
                .OrderBy(l => l.Value.PatchName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(l => l.Key.Sha1, StringComparer.Ordinal))
            {
                writer.WriteStartObject();
                writer.WriteString("patch_sha1", key.Sha1);
                writer.WriteNumber("patch_size", key.Size);
                writer.WriteString("patch_name", link.PatchName);
                // UpdateAsync refuses an origin without CRC32
                if (link.Kind == PatchLinkKind.Origin && link.OriginCrc32 is { } crc)
                {
                    writer.WriteString("kind", KindOrigin);
                    writer.WriteString("rom_crc32", crc.ToString("x8", CultureInfo.InvariantCulture));
                    writer.WriteString("rom_name", link.OriginName ?? string.Empty);
                }
                else
                {
                    writer.WriteString("kind", KindIgnored);
                }

                writer.WriteEndObject();
            }

            foreach (var entry in unknown)
            {
                entry.WriteTo(writer);
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        });
    }
}
