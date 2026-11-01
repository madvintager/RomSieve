using System.Collections.Concurrent;
using System.IO;
using System.Text.Json;
using RomSieve.Core.Abstractions;
using RomSieve.Core.Models;

namespace RomSieve.Core.Services;

/// <summary>
/// Persistent disk cache of the headers of archives (their members, with the size and CRC32 they record) and of CHDs
/// (<see cref="ChdInfo"/>), in <c>header_cache.json</c>. An entry is valid while the file keeps the last write time
/// and the size it had when it was read. Loaded at its first use, since it may weigh several megabytes (the members
/// of a whole MAME collection), and written to a temporary file then moved, so that an interrupted write never leaves
/// a truncated cache. The verification of the content of the archives (<see cref="SortConfig.VerifyArchiveContents"/>),
/// which decompresses them, is kept as well (<c>verified</c>). A missing, corrupted or other-format file gives an empty
/// cache.
/// </summary>
public sealed class HeaderCacheService : IHeaderCache
{
    public const string FileName = "header_cache.json";

    // Written in the file: another value makes the whole file ignored
    private const int FormatVersion = 1;

    private readonly object _loadLock = new();
    private ConcurrentDictionary<string, ArchiveEntry> _archives = new(StringComparer.Ordinal);
    private ConcurrentDictionary<string, ChdEntry> _chds = new(StringComparer.Ordinal);
    private ConcurrentDictionary<string, VerificationEntry> _verifications = new(StringComparer.Ordinal);
    private volatile bool _isLoaded;
    private volatile bool _dirty;

    private readonly record struct FileStamp(double MtimeSeconds, long Size);

    private sealed record CachedMember(string Name, long Size, string Crc32);

    private sealed record ArchiveEntry(FileStamp Stamp, IReadOnlyList<CachedMember> Members);

    private sealed record ChdEntry(FileStamp Stamp, ChdInfo Info);

    private sealed record VerificationEntry(FileStamp Stamp, IReadOnlyList<DamagedArchiveMember> Damaged);

    public string CachePath { get; }

    public HeaderCacheService(string cachePath)
    {
        CachePath = cachePath;
    }

    public IReadOnlyList<ArchiveMember> GetArchiveMembers(string archivePath, IChecksumService checksumService)
    {
        EnsureLoaded();
        var key = Path.GetFullPath(archivePath);
        var stamp = GetStamp(key);
        if (stamp is { } current && _archives.TryGetValue(key, out var cached) && cached.Stamp == current)
        {
            return cached.Members.Select(m => new ArchiveMember(archivePath, m.Name, m.Size, m.Crc32)).ToList();
        }

        // The stamp is taken before the read: a file changed meanwhile is read again next time
        var members = checksumService.GetArchiveMembers(archivePath);
        if (stamp is { } readStamp && members.Count > 0)
        {
            _archives[key] = new ArchiveEntry(readStamp, members.Select(m => new CachedMember(m.MemberName, m.Size, m.Crc32)).ToList());
            _dirty = true;
        }

        return members;
    }

    public ChdInfo ReadChdInfo(string path, IChecksumService checksumService)
    {
        EnsureLoaded();
        var key = Path.GetFullPath(path);
        var stamp = GetStamp(key);
        if (stamp is { } current && _chds.TryGetValue(key, out var cached) && cached.Stamp == current)
        {
            return cached.Info;
        }

        var info = checksumService.ReadChdInfo(path);
        if (stamp is { } readStamp)
        {
            _chds[key] = new ChdEntry(readStamp, info);
            _dirty = true;
        }

        return info;
    }

    public IReadOnlyList<DamagedArchiveMember> FindDamagedMembers(string archivePath, IChecksumService checksumService, CancellationToken cancellationToken = default)
    {
        EnsureLoaded();
        var key = Path.GetFullPath(archivePath);
        var stamp = GetStamp(key);
        if (stamp is { } current && _verifications.TryGetValue(key, out var cached) && cached.Stamp == current)
        {
            return cached.Damaged;
        }

        // The stamp is taken before the read: a file changed meanwhile is checked again next time
        var damaged = checksumService.FindDamagedMembers(archivePath, cancellationToken);
        if (stamp is { } readStamp)
        {
            _verifications[key] = new VerificationEntry(readStamp, damaged);
            _dirty = true;
        }

        return damaged;
    }

    /// <summary>Forgets what was loaded: the file is read again at the next use.</summary>
    public void Reload()
    {
        lock (_loadLock)
        {
            _archives = new(StringComparer.Ordinal);
            _chds = new(StringComparer.Ordinal);
            _verifications = new(StringComparer.Ordinal);
            _isLoaded = false;
            _dirty = false;
        }
    }

    public async Task SaveAsync(CancellationToken cancellationToken = default)
    {
        if (!_dirty)
        {
            return;
        }

        try
        {
            var dir = Path.GetDirectoryName(CachePath);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }

            // Cleared before the snapshot: an entry added while writing makes the cache dirty again
            _dirty = false;
            await AtomicFile.WriteAsync(CachePath, async (stream, ct) =>
            {
                await using var writer = new Utf8JsonWriter(stream);
                Write(writer);
                await writer.FlushAsync(ct).ConfigureAwait(false);
            }, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or OperationCanceledException)
        {
            // Ignore cache write errors: the entries are written again by the next save
            _dirty = true;
        }
    }

    private void Write(Utf8JsonWriter writer)
    {
        writer.WriteStartObject();
        writer.WriteNumber("format", FormatVersion);

        writer.WriteStartObject("archives");
        foreach (var (path, entry) in _archives.OrderBy(kvp => kvp.Key, StringComparer.Ordinal))
        {
            writer.WriteStartArray(path);
            WriteStamp(writer, entry.Stamp);
            writer.WriteStartArray();
            foreach (var member in entry.Members)
            {
                writer.WriteStartArray();
                writer.WriteStringValue(member.Name);
                writer.WriteNumberValue(member.Size);
                writer.WriteStringValue(member.Crc32);
                writer.WriteEndArray();
            }
            writer.WriteEndArray();
            writer.WriteEndArray();
        }
        writer.WriteEndObject();

        writer.WriteStartObject("chds");
        foreach (var (path, entry) in _chds.OrderBy(kvp => kvp.Key, StringComparer.Ordinal))
        {
            var info = entry.Info;
            writer.WriteStartArray(path);
            WriteStamp(writer, entry.Stamp);
            writer.WriteNumberValue(info.Version);
            writer.WriteNumberValue(info.LogicalBytes);
            writer.WriteStringValue(info.Sha1);
            writer.WriteStringValue(info.RawSha1);
            if (info.ParentSha1 is null)
            {
                writer.WriteNullValue();
            }
            else
            {
                writer.WriteStringValue(info.ParentSha1);
            }
            writer.WriteStringValue(info.MediaKind.ToString());
            writer.WriteStartArray();
            foreach (var track in info.Tracks)
            {
                writer.WriteStartArray();
                writer.WriteNumberValue(track.Number);
                writer.WriteStringValue(track.Type);
                writer.WriteNumberValue(track.Frames);
                writer.WriteEndArray();
            }
            writer.WriteEndArray();
            writer.WriteEndArray();
        }
        writer.WriteEndObject();

        writer.WriteStartObject("verified");
        foreach (var (path, entry) in _verifications.OrderBy(kvp => kvp.Key, StringComparer.Ordinal))
        {
            writer.WriteStartArray(path);
            WriteStamp(writer, entry.Stamp);
            writer.WriteStartArray();
            foreach (var member in entry.Damaged)
            {
                writer.WriteStartArray();
                writer.WriteStringValue(member.MemberName);
                writer.WriteStringValue(member.ExpectedCrc32);
                writer.WriteStringValue(member.ActualCrc32);
                writer.WriteStringValue(member.Error);
                writer.WriteEndArray();
            }
            writer.WriteEndArray();
            writer.WriteEndArray();
        }
        writer.WriteEndObject();

        writer.WriteEndObject();
    }

    private static void WriteStamp(Utf8JsonWriter writer, FileStamp stamp)
    {
        writer.WriteNumberValue(stamp.MtimeSeconds);
        writer.WriteNumberValue(stamp.Size);
    }

    private void EnsureLoaded()
    {
        if (_isLoaded)
        {
            return;
        }

        lock (_loadLock)
        {
            if (_isLoaded)
            {
                return;
            }

            try
            {
                if (File.Exists(CachePath))
                {
                    using var stream = File.OpenRead(CachePath);
                    using var doc = JsonDocument.Parse(stream);
                    Read(doc.RootElement);
                }
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                // A corrupted cache, whatever its defect (invalid JSON, missing or mistyped values), is an empty cache
                _archives.Clear();
                _chds.Clear();
                _verifications.Clear();
            }

            _isLoaded = true;
        }
    }

    private void Read(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("format", out var format)
            || format.ValueKind != JsonValueKind.Number
            || format.GetInt32() != FormatVersion)
        {
            return;
        }

        if (root.TryGetProperty("archives", out var archives) && archives.ValueKind == JsonValueKind.Object)
        {
            foreach (var prop in archives.EnumerateObject())
            {
                var arr = prop.Value;
                var members = arr[2].EnumerateArray()
                    .Select(m => new CachedMember(m[0].GetString() ?? string.Empty, m[1].GetInt64(), m[2].GetString() ?? string.Empty))
                    .ToList();
                _archives[prop.Name] = new ArchiveEntry(ReadStamp(arr), members);
            }
        }

        if (root.TryGetProperty("chds", out var chds) && chds.ValueKind == JsonValueKind.Object)
        {
            foreach (var prop in chds.EnumerateObject())
            {
                var arr = prop.Value;
                var tracks = arr[8].EnumerateArray()
                    .Select(t => new ChdTrack(t[0].GetInt32(), t[1].GetString() ?? string.Empty, t[2].GetInt32()))
                    .ToList();
                var info = new ChdInfo(
                    Version: arr[2].GetInt32(),
                    LogicalBytes: arr[3].GetInt64(),
                    Sha1: arr[4].GetString() ?? string.Empty,
                    RawSha1: arr[5].GetString() ?? string.Empty,
                    ParentSha1: arr[6].ValueKind == JsonValueKind.String ? arr[6].GetString() : null,
                    MediaKind: Enum.TryParse<ChdMediaKind>(arr[7].GetString(), out var kind) ? kind : ChdMediaKind.Unknown,
                    Tracks: tracks);
                _chds[prop.Name] = new ChdEntry(ReadStamp(arr), info);
            }
        }

        if (root.TryGetProperty("verified", out var verified) && verified.ValueKind == JsonValueKind.Object)
        {
            foreach (var prop in verified.EnumerateObject())
            {
                var arr = prop.Value;
                var damaged = arr[2].EnumerateArray()
                    .Select(m => new DamagedArchiveMember(m[0].GetString() ?? string.Empty, m[1].GetString() ?? string.Empty, m[2].GetString(), m[3].GetString()))
                    .ToList();
                _verifications[prop.Name] = new VerificationEntry(ReadStamp(arr), damaged);
            }
        }
    }

    private static FileStamp ReadStamp(JsonElement arr) => new(arr[0].GetDouble(), arr[1].GetInt64());

    // Last write time (to the millisecond, as the CRC cache) and size of a file; null when it cannot be read
    private static FileStamp? GetStamp(string path)
    {
        try
        {
            var fi = new FileInfo(path);
            return fi.Exists
                ? new FileStamp(new DateTimeOffset(fi.LastWriteTimeUtc).ToUnixTimeMilliseconds() / 1000.0, fi.Length)
                : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
