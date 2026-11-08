using System.Collections.Concurrent;
using System.IO;
using System.Text.Json;
using RomSieve.Core.Abstractions;
using RomSieve.Core.Models;

namespace RomSieve.Core.Services;

/// <summary>
/// Persistent disk cache of the DATs of the folders and archives of DATs as their headers tell them (name and
/// convention), in <c>dat_header_cache.json</c> next to <c>crc_cache.json</c>: reading the list of a pack of thousands
/// of DATs opens none of them once it is cached. A DAT of the disk is valid while it keeps its last write time (to the
/// tick) and its size; a member of an archive while it keeps the size and the CRC32 the archive records for it (the last
/// write time and the size of the archive for a member without a recorded CRC32). The file is tied to the build of the
/// Core that wrote it (its deterministic module id): another build, which may tell the conventions otherwise, ignores
/// it. Loaded at its first use, written to a temporary file then moved; a missing, corrupted or other-format file gives
/// an empty cache.
/// </summary>
public sealed class DatHeaderCacheService : IDatHeaderCache
{
    public const string FileName = "dat_header_cache.json";

    // Written in the file: another value makes the whole file ignored
    private const int FormatVersion = 1;

    private readonly string _build;
    private readonly object _loadLock = new();
    private readonly object _saveLock = new();
    private readonly ConcurrentDictionary<string, FileEntry> _files = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<(string Archive, string Member), MemberEntry> _members = new();
    private volatile bool _isLoaded;
    private volatile bool _dirty;

    private readonly record struct FileStamp(long Ticks, long Size);

    private sealed record FileEntry(FileStamp Stamp, DatHeaderInfo Info);

    // A member with a recorded CRC32 has no archive stamp (0, 0): the archive may change around it
    private sealed record MemberEntry(long Size, string Crc32, FileStamp ArchiveStamp, DatHeaderInfo Info);

    public string CachePath { get; }

    public DatHeaderCacheService(string cachePath)
        : this(cachePath, typeof(DatHeaderCacheService).Module.ModuleVersionId.ToString("N"))
    {
    }

    /// <summary>A cache tied to <paramref name="build"/> (tests: a file written by another build is ignored).</summary>
    internal DatHeaderCacheService(string cachePath, string build)
    {
        CachePath = cachePath;
        _build = build;
    }

    /// <summary>The cache next to a CRC cache (<see cref="ICrcCacheService.CachePath"/>), as the caches are placed.</summary>
    public static DatHeaderCacheService NextTo(string crcCachePath) =>
        new(Path.Combine(Path.GetDirectoryName(Path.GetFullPath(crcCachePath)) ?? string.Empty, FileName));

    public DatHeaderInfo GetOrRead(string path, Func<DatHeaderInfo> read)
    {
        EnsureLoaded();
        var key = Path.GetFullPath(path);
        var stamp = GetStamp(key);
        if (stamp is { } current && _files.TryGetValue(key, out var cached) && cached.Stamp == current)
        {
            return cached.Info;
        }

        // The stamp is taken before the read: a file changed meanwhile is read again next time
        var info = read();
        if (stamp is { } readStamp)
        {
            _files[key] = new FileEntry(readStamp, info);
            _dirty = true;
        }

        return info;
    }

    public DatHeaderInfo GetOrRead(ArchiveMember member, Func<DatHeaderInfo> read)
    {
        EnsureLoaded();
        var key = (Path.GetFullPath(member.ArchivePath), member.MemberName);
        var crc = member.Crc32;
        var archiveStamp = crc.Length > 0 ? new FileStamp(0, 0) : GetStamp(key.Item1);
        if (archiveStamp is { } current
            && _members.TryGetValue(key, out var cached)
            && cached.Size == member.Size
            && string.Equals(cached.Crc32, crc, StringComparison.OrdinalIgnoreCase)
            && cached.ArchiveStamp == current)
        {
            return cached.Info;
        }

        var info = read();
        if (archiveStamp is { } readStamp)
        {
            _members[key] = new MemberEntry(member.Size, crc, readStamp, info);
            _dirty = true;
        }

        return info;
    }

    public void Save()
    {
        if (!_dirty)
        {
            return;
        }

        lock (_saveLock)
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
                AtomicFile.Write(CachePath, stream =>
                {
                    using var writer = new Utf8JsonWriter(stream);
                    Write(writer);
                });
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Ignore cache write errors: the entries are written again by the next save
                _dirty = true;
            }
        }
    }

    private void Write(Utf8JsonWriter writer)
    {
        writer.WriteStartObject();
        writer.WriteNumber("format", FormatVersion);
        writer.WriteString("build", _build);

        writer.WriteStartObject("files");
        foreach (var (path, entry) in _files.OrderBy(kvp => kvp.Key, StringComparer.Ordinal))
        {
            writer.WriteStartArray(path);
            writer.WriteNumberValue(entry.Stamp.Ticks);
            writer.WriteNumberValue(entry.Stamp.Size);
            WriteInfo(writer, entry.Info);
            writer.WriteEndArray();
        }
        writer.WriteEndObject();

        writer.WriteStartObject("archives");
        foreach (var archive in _members.GroupBy(kvp => kvp.Key.Archive).OrderBy(group => group.Key, StringComparer.Ordinal))
        {
            writer.WriteStartObject(archive.Key);
            foreach (var (key, entry) in archive.OrderBy(kvp => kvp.Key.Member, StringComparer.Ordinal))
            {
                writer.WriteStartArray(key.Member);
                writer.WriteNumberValue(entry.Size);
                writer.WriteStringValue(entry.Crc32);
                writer.WriteNumberValue(entry.ArchiveStamp.Ticks);
                writer.WriteNumberValue(entry.ArchiveStamp.Size);
                WriteInfo(writer, entry.Info);
                writer.WriteEndArray();
            }
            writer.WriteEndObject();
        }
        writer.WriteEndObject();

        writer.WriteEndObject();
    }

    private static void WriteInfo(Utf8JsonWriter writer, DatHeaderInfo info)
    {
        writer.WriteStringValue(info.Name);
        writer.WriteStringValue(info.Convention.ToString());
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
                _files.Clear();
                _members.Clear();
            }

            _isLoaded = true;
        }
    }

    private void Read(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("format", out var format)
            || format.ValueKind != JsonValueKind.Number
            || format.GetInt32() != FormatVersion
            || !root.TryGetProperty("build", out var build)
            || build.ValueKind != JsonValueKind.String
            || build.GetString() != _build)
        {
            return;
        }

        if (root.TryGetProperty("files", out var files) && files.ValueKind == JsonValueKind.Object)
        {
            foreach (var prop in files.EnumerateObject())
            {
                var arr = prop.Value;
                if (ReadInfo(arr, 2) is { } info)
                {
                    _files[prop.Name] = new FileEntry(new FileStamp(arr[0].GetInt64(), arr[1].GetInt64()), info);
                }
            }
        }

        if (root.TryGetProperty("archives", out var archives) && archives.ValueKind == JsonValueKind.Object)
        {
            foreach (var archive in archives.EnumerateObject())
            {
                foreach (var prop in archive.Value.EnumerateObject())
                {
                    var arr = prop.Value;
                    if (ReadInfo(arr, 4) is { } info)
                    {
                        _members[(archive.Name, prop.Name)] = new MemberEntry(
                            arr[0].GetInt64(),
                            arr[1].GetString() ?? string.Empty,
                            new FileStamp(arr[2].GetInt64(), arr[3].GetInt64()),
                            info);
                    }
                }
            }
        }
    }

    // The name and the convention written from index; an unknown convention drops the entry, which is read again
    private static DatHeaderInfo? ReadInfo(JsonElement arr, int index) =>
        Enum.TryParse<NamingConvention>(arr[index + 1].GetString(), ignoreCase: false, out var convention)
        && Enum.IsDefined(convention)
            ? new DatHeaderInfo(arr[index].GetString() ?? string.Empty, convention)
            : null;

    // Last write time and size of a file; null when it cannot be read
    private static FileStamp? GetStamp(string path)
    {
        try
        {
            var fi = new FileInfo(path);
            return fi.Exists ? new FileStamp(fi.LastWriteTimeUtc.Ticks, fi.Length) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
