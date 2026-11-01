using System.Collections.Concurrent;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.IO.Hashing;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml;
using RomSieve.Core.Abstractions;
using RomSieve.Core.Models;
using SharpCompress.Archives;
using ArchiveReaderOptions = SharpCompress.Readers.ReaderOptions;

namespace RomSieve.Core.Services;

/// <summary>
/// Service for computing checksums (hardware CRC32, MD5, SHA1), detecting copier headers,
/// and inspecting ZIP archives without prior extraction.
/// </summary>
public sealed class ChecksumService : IChecksumService
{

    /// <summary>
    /// Detects the size in bytes of a standard copier/emulator header, or returns 0:
    /// - iNES header (16 bytes): starts with "NES\x1a"
    /// - Atari Lynx header (64 bytes): starts with "LYNX"
    /// - SNES copier header (512 bytes): fileSize % 1024 == 512
    /// </summary>
    public int DetectCopierHeader(ReadOnlySpan<byte> headerBytes, long fileSize)
    {
        if (headerBytes.Length >= 16 &&
            headerBytes[0] == (byte)'N' &&
            headerBytes[1] == (byte)'E' &&
            headerBytes[2] == (byte)'S' &&
            headerBytes[3] == 0x1A)
        {
            return 16;
        }

        if (headerBytes.Length >= 64 &&
            headerBytes[0] == (byte)'L' &&
            headerBytes[1] == (byte)'Y' &&
            headerBytes[2] == (byte)'N' &&
            headerBytes[3] == (byte)'X')
        {
            return 64;
        }

        if (fileSize > 512 && (fileSize % 1024 == 512))
        {
            return 512;
        }

        return 0;
    }

    public async Task<string> ComputeFileCrc32Async(
        string path,
        long offset = 0,
        int chunkSize = 65536,
        CancellationToken cancellationToken = default)
    {
        var crc32 = new Crc32();
        var buffer = new byte[chunkSize];

        await using var fs = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: chunkSize,
            useAsync: true);

        if (offset > 0)
        {
            fs.Seek(offset, SeekOrigin.Begin);
        }

        int bytesRead;
        while ((bytesRead = await fs.ReadAsync(buffer.AsMemory(0, chunkSize), cancellationToken).ConfigureAwait(false)) > 0)
        {
            crc32.Append(buffer.AsSpan(0, bytesRead));
        }

        return $"{crc32.GetCurrentHashAsUInt32():x8}";
    }

    public async Task<(string Crc32, string? Md5, string? Sha1)> ComputeFileChecksumsAsync(
        string path,
        long offset = 0,
        int chunkSize = 65536,
        bool computeHashes = true,
        CancellationToken cancellationToken = default)
    {
        var crc32 = new Crc32();
        using var md5 = computeHashes ? IncrementalHash.CreateHash(HashAlgorithmName.MD5) : null;
        using var sha1 = computeHashes ? IncrementalHash.CreateHash(HashAlgorithmName.SHA1) : null;
        var buffer = new byte[chunkSize];

        await using var fs = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: chunkSize,
            useAsync: true);

        if (offset > 0)
        {
            fs.Seek(offset, SeekOrigin.Begin);
        }

        int bytesRead;
        while ((bytesRead = await fs.ReadAsync(buffer.AsMemory(0, chunkSize), cancellationToken).ConfigureAwait(false)) > 0)
        {
            crc32.Append(buffer.AsSpan(0, bytesRead));
            if (computeHashes)
            {
                md5!.AppendData(buffer, 0, bytesRead);
                sha1!.AppendData(buffer, 0, bytesRead);
            }
        }

        var crcStr = $"{crc32.GetCurrentHashAsUInt32():x8}";
        var md5Str = computeHashes ? Convert.ToHexString(md5!.GetHashAndReset()).ToLowerInvariant() : null;
        var sha1Str = computeHashes ? Convert.ToHexString(sha1!.GetHashAndReset()).ToLowerInvariant() : null;
        return (crcStr, md5Str, sha1Str);
    }

    public async Task<string> ComputeFileSha1Async(string path, long offset = 0, CancellationToken cancellationToken = default)
    {
        await using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: 65536, useAsync: true);
        if (offset > 0)
        {
            fs.Seek(offset, SeekOrigin.Begin);
        }

        return await ComputeSha1Async(fs, cancellationToken).ConfigureAwait(false);
    }

    public async Task<string> ComputeArchiveMemberSha1Async(string archivePath, string memberName, CancellationToken cancellationToken = default)
    {
        await using var stream = OpenArchiveMember(archivePath, memberName);
        return await ComputeSha1Async(stream, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// SHA-1 of a stream read chunk by chunk, with an explicit cancellation check (ZIP entry streams ignore the token).
    /// </summary>
    private static async Task<string> ComputeSha1Async(Stream stream, CancellationToken cancellationToken)
    {
        using var sha1 = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);
        var buffer = new byte[65536];
        int bytesRead;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            bytesRead = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (bytesRead == 0)
            {
                break;
            }

            sha1.AppendData(buffer, 0, bytesRead);
        }

        return Convert.ToHexStringLower(sha1.GetHashAndReset());
    }

    public ChdInfo ReadChdInfo(string path) => ChdReader.Read(path);

    public bool IsZipArchive(string path) =>
        string.Equals(Path.GetExtension(path), ".zip", StringComparison.OrdinalIgnoreCase);

    public IReadOnlyList<ArchiveMember> GetZipArchiveMembers(string zipPath)
    {
        var results = new List<ArchiveMember>();
        if (!File.Exists(zipPath))
        {
            return results;
        }

        try
        {
            using var zf = ZipFile.OpenRead(zipPath);
            foreach (var entry in zf.Entries)
            {
                // Skip directories inside ZIP
                if (string.IsNullOrEmpty(entry.Name) && entry.FullName.EndsWith('/'))
                {
                    continue;
                }

                var crc = $"{entry.Crc32:x8}";
                results.Add(new ArchiveMember(
                    ArchivePath: zipPath,
                    MemberName: entry.FullName,
                    Size: entry.Length,
                    Crc32: crc));
            }
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            // Ignore malformed or unreadable archives
        }

        return results;
    }

    // ZIP archives are read by System.IO.Compression, 7z and RAR archives by SharpCompress
    private static readonly string[] SharpCompressExtensions = [".7z", ".rar"];

    public bool IsSupportedArchive(string path) =>
        IsZipArchive(path) || SharpCompressExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);

    public IReadOnlyList<ArchiveMember> GetArchiveMembers(string archivePath)
    {
        if (IsZipArchive(archivePath))
        {
            return GetZipArchiveMembers(archivePath);
        }

        var results = new List<ArchiveMember>();
        if (!File.Exists(archivePath))
        {
            return results;
        }

        try
        {
            using var archive = ArchiveFactory.OpenArchive(archivePath, new ArchiveReaderOptions());
            foreach (var entry in archive.Entries)
            {
                if (entry.IsDirectory || entry.Key is null)
                {
                    continue;
                }

                results.Add(new ArchiveMember(archivePath, entry.Key, entry.Size, FormatArchiveCrc(entry.Crc, entry.Size)));
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Like a malformed ZIP, an unreadable 7z or RAR archive is handled as a plain file
            results.Clear();
        }

        return results;
    }

    public ArchiveMemberStream OpenArchiveMember(string archivePath, string memberName)
    {
        if (IsZipArchive(archivePath))
        {
            var zip = ZipFile.OpenRead(archivePath);
            try
            {
                var zipEntry = zip.GetEntry(memberName) ?? throw new FileNotFoundException($"Archive member not found: {memberName}");
                return new ArchiveMemberStream(zipEntry.Open(), zip, $"{zipEntry.Crc32:x8}");
            }
            catch
            {
                zip.Dispose();
                throw;
            }
        }

        var archive = ArchiveFactory.OpenArchive(archivePath, new ArchiveReaderOptions());
        try
        {
            var entry = archive.Entries.FirstOrDefault(e => !e.IsDirectory && string.Equals(e.Key, memberName, StringComparison.Ordinal))
                ?? throw new FileNotFoundException($"Archive member not found: {memberName}");

            // In a solid 7z archive, the members of a block are compressed together: opening one decompresses
            // the block from its start, so the cost of a member grows with its position in the block
            return new ArchiveMemberStream(entry.OpenEntryStream(), archive, FormatArchiveCrc(entry.Crc, entry.Size));
        }
        catch
        {
            archive.Dispose();
            throw;
        }
    }

    public IReadOnlyList<DamagedArchiveMember> FindDamagedMembers(string archivePath, CancellationToken cancellationToken = default)
    {
        try
        {
            return ReadDamagedMembers(archivePath, cancellationToken);
        }
        catch (Exception ex) when (ex is SharpCompress.Common.SharpCompressException or InvalidOperationException or NotSupportedException)
        {
            // The callers know the archives through this service only
            throw new InvalidDataException(ex.Message, ex);
        }
    }

    private IReadOnlyList<DamagedArchiveMember> ReadDamagedMembers(string archivePath, CancellationToken cancellationToken)
    {
        var damaged = new List<DamagedArchiveMember>();
        if (IsZipArchive(archivePath))
        {
            using var zip = ZipFile.OpenRead(archivePath);
            foreach (var entry in zip.Entries.Where(e => !e.FullName.EndsWith('/')))
            {
                CheckMember(entry.FullName, $"{entry.Crc32:x8}", entry.Open, damaged, cancellationToken);
            }

            return damaged;
        }

        using var archive = ArchiveFactory.OpenArchive(archivePath, new ArchiveReaderOptions());
        if (archive.IsSolid)
        {
            // The members of a solid archive are read in sequence: opening each one alone would decompress its block
            // from the start every time
            using var reader = archive.ExtractAllEntries();
            while (reader.MoveToNextEntry())
            {
                var entry = reader.Entry;
                if (!entry.IsDirectory)
                {
                    CheckMember(entry.Key ?? string.Empty, FormatArchiveCrc(entry.Crc, entry.Size), reader.OpenEntryStream, damaged, cancellationToken);
                }
            }
        }
        else
        {
            foreach (var entry in archive.Entries.Where(e => !e.IsDirectory))
            {
                CheckMember(entry.Key ?? string.Empty, FormatArchiveCrc(entry.Crc, entry.Size), entry.OpenEntryStream, damaged, cancellationToken);
            }
        }

        return damaged;
    }

    // Decompresses a member and compares its CRC32 with the recorded one, when there is one
    private static void CheckMember(
        string name, string expectedCrc, Func<Stream> open, List<DamagedArchiveMember> damaged, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(expectedCrc))
        {
            return;
        }

        try
        {
            using var stream = open();
            var crc = new Crc32();
            var buffer = new byte[65536];
            int read;
            while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                crc.Append(buffer.AsSpan(0, read));
            }

            var actual = $"{crc.GetCurrentHashAsUInt32():x8}";
            if (!string.Equals(actual, expectedCrc, StringComparison.OrdinalIgnoreCase))
            {
                damaged.Add(new DamagedArchiveMember(name, expectedCrc, actual, null));
            }
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or SharpCompress.Common.SharpCompressException)
        {
            damaged.Add(new DamagedArchiveMember(name, expectedCrc, null, ex.Message));
        }
    }

    /// <summary>
    /// CRC32 recorded for a 7z or RAR member; empty when the archive records none (0 for a member that is not empty).
    /// </summary>
    private static string FormatArchiveCrc(long crc, long size) =>
        crc != 0 || size == 0 ? $"{(uint)crc:x8}" : string.Empty;
}

/// <summary>
/// Persistent disk cache of computed CRC32 checksums (<c>crc_cache.json</c>), and of the headers of archives and CHDs
/// next to it (<see cref="HeaderCacheService"/>).
/// </summary>
public sealed class CrcCacheService : ICrcCacheService
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    // Concurrent: the CRC32 of several files may be computed, and cached, in parallel during the classification
    private readonly ConcurrentDictionary<string, CacheEntry> _data = new(StringComparer.Ordinal);
    private volatile bool _dirty;

    // An entry is stored as [mtime, size, crc], or [mtime, size, crc, sha1] once its SHA-1 was computed
    private sealed record CacheEntry(double MtimeSeconds, long Size, string Crc, string? Sha1 = null);

    private readonly HeaderCacheService _headers;

    public string CachePath { get; }

    public IHeaderCache Headers => _headers;

    public CrcCacheService(string? cachePath = null)
    {
        CachePath = string.IsNullOrEmpty(cachePath) ? GetDefaultCrcCachePath() : cachePath;
        _headers = new HeaderCacheService(Path.Combine(Path.GetDirectoryName(Path.GetFullPath(CachePath)) ?? string.Empty, HeaderCacheService.FileName));
        LoadSynchronously();
    }

    public static string GetDefaultCrcCachePath()
    {
        string baseDir;
        if (OperatingSystem.IsWindows())
        {
            var localAppData = Environment.GetEnvironmentVariable("LOCALAPPDATA");
            baseDir = !string.IsNullOrEmpty(localAppData)
                ? localAppData
                : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "AppData", "Local");
        }
        else
        {
            var xdgCache = Environment.GetEnvironmentVariable("XDG_CACHE_HOME");
            baseDir = !string.IsNullOrEmpty(xdgCache)
                ? xdgCache
                : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".cache");
        }

        return Path.Combine(baseDir, "RomSieve", "crc_cache.json");
    }

    public async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        _headers.Reload();
        if (!File.Exists(CachePath))
        {
            return;
        }

        try
        {
            var json = await File.ReadAllTextAsync(CachePath, Encoding.UTF8, cancellationToken).ConfigureAwait(false);
            ParseCacheJson(json);
        }
        catch
        {
            _data.Clear();
        }
    }

    private void LoadSynchronously()
    {
        if (!File.Exists(CachePath))
        {
            return;
        }

        try
        {
            var json = File.ReadAllText(CachePath, Encoding.UTF8);
            ParseCacheJson(json);
        }
        catch
        {
            _data.Clear();
        }
    }

    private void ParseCacheJson(string json)
    {
        _data.Clear();
        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        foreach (var prop in doc.RootElement.EnumerateObject())
        {
            if (prop.Value.ValueKind == JsonValueKind.Array && prop.Value.GetArrayLength() is 3 or 4)
            {
                var arr = prop.Value;
                var mtime = arr[0].GetDouble();
                var size = arr[1].GetInt64();
                var crc = arr[2].GetString() ?? string.Empty;
                var sha1 = arr.GetArrayLength() == 4 && arr[3].ValueKind == JsonValueKind.String ? arr[3].GetString() : null;
                _data[prop.Name] = new CacheEntry(mtime, size, crc, string.IsNullOrEmpty(sha1) ? null : sha1);
            }
        }
    }

    public async Task SaveAsync(CancellationToken cancellationToken = default)
    {
        await _headers.SaveAsync(cancellationToken).ConfigureAwait(false);
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

            var rawDict = _data.OrderBy(kvp => kvp.Key, StringComparer.Ordinal).ToDictionary(
                kvp => kvp.Key,
                kvp => kvp.Value.Sha1 is null
                    ? new object[] { kvp.Value.MtimeSeconds, kvp.Value.Size, kvp.Value.Crc }
                    : new object[] { kvp.Value.MtimeSeconds, kvp.Value.Size, kvp.Value.Crc, kvp.Value.Sha1 },
                StringComparer.Ordinal);

            var json = JsonSerializer.Serialize(rawDict, JsonOptions);
            await AtomicFile.WriteAllTextAsync(CachePath, json, new UTF8Encoding(false), cancellationToken).ConfigureAwait(false);
            _dirty = false;
        }
        catch
        {
            // Ignore cache write errors
        }
    }

    public string? Get(string path) => GetValidEntry(path)?.Crc;

    public string? GetSha1(string path) => GetValidEntry(path)?.Sha1;

    public void Set(string path, string crc) => Store(path, crc, sha1: null);

    public void SetSha1(string path, string crc, string sha1) => Store(path, crc, sha1);

    /// <summary>Cached entry of a file whose last write time and size are unchanged since it was cached.</summary>
    private CacheEntry? GetValidEntry(string path)
    {
        var key = Path.GetFullPath(path);
        if (!_data.TryGetValue(key, out var entry))
        {
            return null;
        }

        try
        {
            var fi = new FileInfo(key);
            if (!fi.Exists)
            {
                return null;
            }

            var mtime = new DateTimeOffset(fi.LastWriteTimeUtc).ToUnixTimeMilliseconds() / 1000.0;
            if (Math.Abs(mtime - entry.MtimeSeconds) < 0.001 && fi.Length == entry.Size)
            {
                return entry;
            }
        }
        catch (IOException)
        {
        }

        return null;
    }

    private void Store(string path, string crc, string? sha1)
    {
        var key = Path.GetFullPath(path);
        try
        {
            var fi = new FileInfo(key);
            if (!fi.Exists)
            {
                return;
            }

            var mtime = new DateTimeOffset(fi.LastWriteTimeUtc).ToUnixTimeMilliseconds() / 1000.0;
            _data[key] = new CacheEntry(mtime, fi.Length, crc, sha1);
            _dirty = true;
        }
        catch (IOException)
        {
        }
    }

    public void Rename(string oldPath, string newPath)
    {
        var oldKey = Path.GetFullPath(oldPath);
        if (_data.TryRemove(oldKey, out var entry))
        {
            _data[Path.GetFullPath(newPath)] = entry;
            _dirty = true;
        }
    }
}

/// <summary>
/// Parser and exporter for DAT files (CLRMAMEPRO and Logiqx XML).
/// </summary>
public sealed partial class DatService : IDatService
{

    private readonly IChecksumService _checksumService;

    public DatService(IChecksumService checksumService)
    {
        _checksumService = checksumService;
    }

    [GeneratedRegex(
        @"""(?<quoted>(?:\\.|[^""\\])*)""|(?<paren>[()])|(?<comment>#.*|//.*)|(?<word>[^\s()]+)",
        RegexOptions.CultureInvariant)]
    private static partial Regex ClrMameProTokenRegex();

    private enum TokenType { Quoted, Paren, Word }

    private static List<(TokenType Type, string Value)> TokenizeClrMamePro(string text)
    {
        var tokens = new List<(TokenType Type, string Value)>();
        foreach (Match m in ClrMameProTokenRegex().Matches(text))
        {
            if (m.Groups["comment"].Success)
            {
                continue;
            }

            if (m.Groups["quoted"].Success)
            {
                var val = m.Groups["quoted"].Value
                    .Replace(@"\""", "\"", StringComparison.Ordinal)
                    .Replace(@"\\", @"\", StringComparison.Ordinal);
                tokens.Add((TokenType.Quoted, val));
            }
            else if (m.Groups["paren"].Success)
            {
                tokens.Add((TokenType.Paren, m.Groups["paren"].Value));
            }
            else if (m.Groups["word"].Success)
            {
                tokens.Add((TokenType.Word, m.Groups["word"].Value));
            }
        }

        return tokens;
    }

    public DatCatalog ParseClrMameProText(string text)
    {
        var catalog = new DatCatalog();
        var tokens = TokenizeClrMamePro(text);
        var i = 0;
        var n = tokens.Count;

        while (i < n)
        {
            var (tType, tVal) = tokens[i++];
            if (tType != TokenType.Word)
            {
                continue;
            }

            var blockName = tVal.ToLowerInvariant();
            if (i < n && tokens[i].Type == TokenType.Paren && tokens[i].Value == "(")
            {
                i++; // Skip '('
            }
            else
            {
                continue;
            }

            if (blockName == "clrmamepro")
            {
                while (i < n)
                {
                    if (tokens[i].Type == TokenType.Paren && tokens[i].Value == ")")
                    {
                        i++;
                        break;
                    }

                    var keyToken = tokens[i++];
                    if (keyToken.Type is TokenType.Word or TokenType.Quoted &&
                        i < n &&
                        tokens[i].Type is TokenType.Word or TokenType.Quoted)
                    {
                        var valToken = tokens[i++];
                        var k = keyToken.Value.ToLowerInvariant();
                        var v = valToken.Value;
                        switch (k)
                        {
                            case "name": catalog.Header.Name = v; break;
                            case "description": catalog.Header.Description = v; break;
                            case "version": catalog.Header.Version = v; break;
                            case "author": catalog.Header.Author = v; break;
                            case "comment": catalog.Header.Comment = v; break;
                            case "homepage" or "url": catalog.Header.Homepage = v; break;
                        }
                    }
                }
            }
            else if (blockName is "game" or "machine" or "resource")
            {
                var gameName = string.Empty;
                var gameDesc = string.Empty;
                string? gameId = null;
                string? cloneOf = null;
                string? cloneOfId = null;
                string? romOf = null;
                string? year = null;
                string? manufacturer = null;
                string? publisher = null;
                var driverStatus = MameDriverStatus.Unknown;
                var emulationStatus = MameDriverStatus.Unknown;
                var roms = new List<DatRomEntry>();

                while (i < n)
                {
                    if (tokens[i].Type == TokenType.Paren && tokens[i].Value == ")")
                    {
                        i++;
                        break;
                    }

                    var subToken = tokens[i++];
                    var isDisk = subToken.Type == TokenType.Word && subToken.Value.Equals("disk", StringComparison.OrdinalIgnoreCase);
                    if (subToken.Type == TokenType.Word && (isDisk || subToken.Value.Equals("rom", StringComparison.OrdinalIgnoreCase)))
                    {
                        if (i < n && tokens[i].Type == TokenType.Paren && tokens[i].Value == "(")
                        {
                            i++;
                        }

                        var romProps = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                        while (i < n)
                        {
                            if (tokens[i].Type == TokenType.Paren && tokens[i].Value == ")")
                            {
                                i++;
                                break;
                            }

                            var rkToken = tokens[i++];
                            if (rkToken.Type is TokenType.Word or TokenType.Quoted)
                            {
                                var rk = rkToken.Value.ToLowerInvariant();
                                if (i < n && tokens[i].Type is TokenType.Word or TokenType.Quoted)
                                {
                                    romProps[rk] = tokens[i++].Value;
                                }
                                else
                                {
                                    romProps[rk] = string.Empty;
                                }
                            }
                        }

                        var romName = romProps.GetValueOrDefault("name", string.Empty);
                        var romSizeRaw = romProps.GetValueOrDefault("size", "0");
                        _ = long.TryParse(romSizeRaw, out var romSize);
                        var romCrc = romProps.GetValueOrDefault("crc", string.Empty);
                        romProps.TryGetValue("md5", out var romMd5);
                        romProps.TryGetValue("sha1", out var romSha1);
                        romProps.TryGetValue("merge", out var romMerge);
                        var romStatus = EnumExtensions.ParseDatRomStatus(
                            romProps.TryGetValue("status", out var statusValue) ? statusValue : romProps.GetValueOrDefault("flags"));

                        // A CHD disk without SHA-1 (status "nodump") cannot be identified
                        if (isDisk && string.IsNullOrEmpty(romSha1))
                        {
                            continue;
                        }

                        roms.Add(new DatRomEntry
                        {
                            Name = romName,
                            Size = isDisk ? 0 : romSize,
                            Crc = isDisk ? string.Empty : romCrc.ToLowerInvariant(),
                            Md5 = romMd5?.ToLowerInvariant(),
                            Sha1 = romSha1?.ToLowerInvariant(),
                            GameName = gameName,
                            Description = gameDesc,
                            IsDisk = isDisk,
                            Merge = string.IsNullOrEmpty(romMerge) ? null : romMerge,
                            Status = romStatus
                        });
                    }
                    else if (subToken.Type is TokenType.Word or TokenType.Quoted)
                    {
                        var k = subToken.Value.ToLowerInvariant();
                        if (i < n && tokens[i].Type == TokenType.Paren && tokens[i].Value == "(")
                        {
                            i++;
                            var depth = 1;
                            while (i < n && depth > 0)
                            {
                                if (tokens[i].Type == TokenType.Paren)
                                {
                                    if (tokens[i].Value == "(") depth++;
                                    else if (tokens[i].Value == ")") depth--;
                                }
                                // Emulation status of a MAME driver: driver ( status good emulation good ... )
                                else if (k == "driver" &&
                                         depth == 1 &&
                                         i + 1 < n &&
                                         tokens[i + 1].Type is TokenType.Word or TokenType.Quoted)
                                {
                                    if (tokens[i].Value.Equals("status", StringComparison.OrdinalIgnoreCase))
                                    {
                                        driverStatus = EnumExtensions.ParseMameDriverStatus(tokens[++i].Value);
                                    }
                                    else if (tokens[i].Value.Equals("emulation", StringComparison.OrdinalIgnoreCase))
                                    {
                                        emulationStatus = EnumExtensions.ParseMameDriverStatus(tokens[++i].Value);
                                    }
                                }
                                i++;
                            }
                        }
                        else if (i < n && tokens[i].Type is TokenType.Word or TokenType.Quoted)
                        {
                            var v = tokens[i++].Value;
                            switch (k)
                            {
                                case "name": gameName = v; break;
                                case "description": gameDesc = v; break;
                                case "id": gameId = v; break;
                                case "cloneof": cloneOf = v; break;
                                case "cloneofid": cloneOfId = v; break;
                                case "romof": romOf = v; break;
                                case "year": year = v; break;
                                case "manufacturer": manufacturer = v; break;
                                case "publisher": publisher = v; break;
                            }
                        }
                    }
                }

                foreach (var r in roms)
                {
                    if (string.IsNullOrEmpty(r.Name))
                    {
                        r.Name = gameName;
                    }
                    r.GameName = gameName;
                    r.Description = gameDesc;
                }

                catalog.AddGame(new DatGame
                {
                    Name = gameName,
                    Description = gameDesc,
                    Roms = roms,
                    Id = gameId,
                    CloneOf = cloneOf,
                    CloneOfId = cloneOfId,
                    RomOf = romOf,
                    IsBios = blockName == "resource",
                    Year = year,
                    Manufacturer = manufacturer,
                    Publisher = publisher,
                    DriverStatus = driverStatus,
                    EmulationStatus = emulationStatus
                });
            }
            else
            {
                var depth = 1;
                while (i < n && depth > 0)
                {
                    if (tokens[i].Type == TokenType.Paren)
                    {
                        if (tokens[i].Value == "(") depth++;
                        else if (tokens[i].Value == ")") depth--;
                    }
                    i++;
                }
            }
        }

        catalog.Header.Convention = catalog.DetectConvention();
        BuildCloneToParentMap(catalog);
        CompleteSoftwareList(catalog);
        return catalog;
    }

    // The software of a MAME list belong to it, and its clones take their shared ROMs from their parent
    private static void CompleteSoftwareList(DatCatalog catalog)
    {
        if (catalog.Header.Convention == NamingConvention.MameSoftwareList)
        {
            catalog.CompleteSoftwareList();
        }
    }

    public DatCatalog ParseLogiqxXml(string text)
    {
        using var reader = new StringReader(text);
        return ParseLogiqxXml(reader);
    }

    /// <summary>
    /// Reads a Logiqx XML DAT, or the output of <c>mame -listxml</c>, as a stream: a DAT of several hundred megabytes is
    /// never held in memory as a document. Only the elements the catalog uses are read; the others (inputs, DIP switches,
    /// chips, slots... of a MAME machine) are skipped.
    /// </summary>
    public DatCatalog ParseLogiqxXml(TextReader textReader, CancellationToken cancellationToken = default) =>
        ParseLogiqxXml(textReader, int.MaxValue, cancellationToken);

    // Reads at most maxGames games: the preview of a DAT stops after its first ones. A MAME software list read from
    // sourceFile is marked as such (DatHeader.SoftwareListFile)
    private DatCatalog ParseLogiqxXml(TextReader textReader, int maxGames, CancellationToken cancellationToken, string? sourceFile = null)
    {
        var catalog = new DatCatalog();
        var settings = new XmlReaderSettings
        {
            // Same rules as XDocument.Parse: the internal DTD of a DAT is read, never an external one, and entity
            // expansion is bounded
            DtdProcessing = DtdProcessing.Parse,
            XmlResolver = null,
            MaxCharactersFromEntities = 10_000_000,
            IgnoreComments = true,
            IgnoreProcessingInstructions = true,
            IgnoreWhitespace = true
        };

        using var xml = XmlReader.Create(textReader, settings);
        var isRootRead = false;

        // MAME software list of the software being read, and the lists met (an output of MAME may hold several)
        string? softwareList = null;
        var listHeaders = new Dictionary<string, DatHeader>(StringComparer.OrdinalIgnoreCase);
        while (xml.Read())
        {
            if (xml.NodeType != XmlNodeType.Element)
            {
                continue;
            }

            if (!isRootRead)
            {
                isRootRead = true;

                // The output of mame -listxml has no header: its root carries the MAME version
                if (xml.LocalName == "mame")
                {
                    catalog.Header.Name = "MAME";
                    catalog.Header.Version = xml.GetAttribute("build") ?? string.Empty;
                }

                // A software list of MAME (hash/<list>.xml) names itself in its root
                if (xml.LocalName == "softwarelist")
                {
                    softwareList = ReadSoftwareListHeader(xml, catalog.Header, sourceFile);
                }

                // The output of mame -getsoftlist and -listsoftware holds its lists in a root of its own
                if (xml.LocalName == "softwarelists")
                {
                    catalog.Header.Author = "MAME";
                    catalog.Header.Convention = NamingConvention.MameSoftwareList;
                }

                continue;
            }

            if (xml.LocalName == "header" && xml.Depth == 1)
            {
                ReadXmlHeader(xml, catalog.Header);
            }
            else if (xml.LocalName == "softwarelist" && xml.Depth == 1)
            {
                // The header names the list while the output holds a single one
                var header = new DatHeader();
                softwareList = ReadSoftwareListHeader(xml, header, sourceFile);
                listHeaders.TryAdd(header.Name, header);
                if (listHeaders.Count == 1)
                {
                    ReadSoftwareListHeader(xml, catalog.Header, sourceFile);
                }
            }
            else if (xml.LocalName is "game" or "machine" or "software")
            {
                cancellationToken.ThrowIfCancellationRequested();
                var game = xml.LocalName == "software" ? ReadXmlSoftware(xml) : ReadXmlGame(xml);
                game.SoftwareList = softwareList;
                catalog.AddGame(game);
                if (catalog.Games.Count >= maxGames)
                {
                    break;
                }
            }
        }

        // Several lists: their short names are only unique in each of them
        if (listHeaders.Count > 1)
        {
            return SplitSoftwareLists(catalog.Games, listHeaders, "MAME");
        }

        catalog.Header.Convention = catalog.DetectConvention();
        BuildCloneToParentMap(catalog);
        CompleteSoftwareList(catalog);
        return catalog;
    }

    /// <summary>
    /// Catalog of each software list of <paramref name="games"/> (in the order of the lists), completed as a catalog
    /// of a single list, gathered under a header named <paramref name="name"/>.
    /// </summary>
    private static DatCatalog SplitSoftwareLists(IEnumerable<DatGame> games, IReadOnlyDictionary<string, DatHeader> headers, string name)
    {
        var lists = new List<DatCatalog>();
        foreach (var group in games.GroupBy(g => g.SoftwareList ?? string.Empty, StringComparer.OrdinalIgnoreCase))
        {
            var list = new DatCatalog
            {
                Header = headers.TryGetValue(group.Key, out var header)
                    ? header
                    : new DatHeader { Name = group.Key, Author = "MAME", Convention = NamingConvention.MameSoftwareList }
            };
            foreach (var game in group)
            {
                list.AddGame(game);
            }

            BuildCloneToParentMap(list);
            list.CompleteSoftwareList();
            lists.Add(list);
        }

        return DatCatalog.GatherSoftwareLists(lists, name);
    }

    // Name and description of a <softwarelist> element, written to the header; the reader stays on the element, whose
    // software follow
    private static string? ReadSoftwareListHeader(XmlReader xml, DatHeader header, string? sourceFile)
    {
        header.Name = xml.GetAttribute("name") ?? string.Empty;
        header.Description = xml.GetAttribute("description") ?? string.Empty;
        header.Author = "MAME";
        header.Convention = NamingConvention.MameSoftwareList;
        header.SoftwareListFile = sourceFile;
        return string.IsNullOrWhiteSpace(header.Name) ? null : header.Name.Trim();
    }

    // Reads the header element; the reader is left on its end
    private static void ReadXmlHeader(XmlReader xml, DatHeader header)
    {
        if (xml.IsEmptyElement)
        {
            return;
        }

        var depth = xml.Depth;
        while (xml.Read() && xml.Depth > depth)
        {
            if (xml.NodeType != XmlNodeType.Element || xml.Depth != depth + 1)
            {
                continue;
            }

            switch (xml.LocalName)
            {
                case "name": header.Name = ReadXmlElementText(xml); break;
                case "description": header.Description = ReadXmlElementText(xml); break;
                case "version": header.Version = ReadXmlElementText(xml); break;
                case "author": header.Author = ReadXmlElementText(xml); break;
                case "comment": header.Comment = ReadXmlElementText(xml); break;
                case "homepage": header.Homepage = ReadXmlElementText(xml); break;
                case "url": header.Url = ReadXmlElementText(xml); break;
            }
        }
    }

    // Reads a game or machine element; the reader is left on its end
    private static DatGame ReadXmlGame(XmlReader xml)
    {
        var game = new DatGame
        {
            Name = xml.GetAttribute("name") ?? string.Empty,
            Id = xml.GetAttribute("id"),
            CloneOf = xml.GetAttribute("cloneof"),
            CloneOfId = xml.GetAttribute("cloneofid"),
            RomOf = xml.GetAttribute("romof"),
            IsBios = IsXmlYes(xml.GetAttribute("isbios")),
            IsDevice = IsXmlYes(xml.GetAttribute("isdevice")),
            IsMechanical = IsXmlYes(xml.GetAttribute("ismechanical")),
            IsRunnable = !string.Equals(xml.GetAttribute("runnable"), "no", StringComparison.OrdinalIgnoreCase)
        };

        if (xml.IsEmptyElement)
        {
            return game;
        }

        var depth = xml.Depth;
        var cabinet = new CabinetReader();
        while (xml.Read() && xml.Depth > depth)
        {
            if (xml.NodeType != XmlNodeType.Element)
            {
                continue;
            }

            // The controls are the children of the input element
            if (xml.Depth == depth + 2 && xml.LocalName == "control")
            {
                cabinet.ReadControl(xml);
                continue;
            }

            if (xml.Depth != depth + 1)
            {
                continue;
            }

            switch (xml.LocalName)
            {
                case "display":
                    cabinet.ReadDisplay(xml);
                    break;
                case "input":
                    cabinet.ReadInput(xml);
                    break;
                case "description":
                    game.Description = ReadXmlElementText(xml);
                    break;
                case "year":
                    game.Year = ReadXmlElementText(xml);
                    break;
                case "manufacturer":
                    game.Manufacturer = ReadXmlElementText(xml);
                    break;
                case "publisher":
                    game.Publisher = ReadXmlElementText(xml);
                    break;
                case "rom":
                    game.Roms.Add(ReadXmlRom(xml, isDisk: false));
                    break;
                case "disk":
                    // A disk without SHA-1 (status "nodump") cannot be identified
                    if (!string.IsNullOrEmpty(xml.GetAttribute("sha1")))
                    {
                        game.Roms.Add(ReadXmlRom(xml, isDisk: true));
                    }
                    break;
                case "device_ref" when xml.GetAttribute("name") is { Length: > 0 } device:
                    // Thousands of machines share a few devices: one string per device name
                    game.DeviceRefs.Add(xml.NameTable.Add(device));
                    break;
                case "driver":
                    game.DriverStatus = EnumExtensions.ParseMameDriverStatus(xml.GetAttribute("status"));
                    game.EmulationStatus = EnumExtensions.ParseMameDriverStatus(xml.GetAttribute("emulation"));
                    game.RequiresArtwork = IsXmlYes(xml.GetAttribute("requiresartwork"));
                    break;
                case "feature" when IsUnemulated(xml.GetAttribute("status")) || IsUnemulated(xml.GetAttribute("overall")):
                    game.UnemulatedFeatures |= EnumExtensions.ParseMameFeature(xml.GetAttribute("type"));
                    break;
            }
        }

        foreach (var rom in game.Roms)
        {
            if (string.IsNullOrEmpty(rom.Name))
            {
                rom.Name = game.Name;
            }
            rom.GameName = game.Name;
            rom.Description = game.Description;
        }

        game.Cabinet = cabinet.ToCabinet();
        return game;
    }

    /// <summary>
    /// Reads a software of a MAME software list (<c>hash/&lt;list&gt;.xml</c>); the reader is left on its end. Its ROMs
    /// are those of the data areas of its parts, its disks those of their disk areas; a ROM element without a name only
    /// goes on loading the previous ROM (<c>loadflag="continue"</c>, <c>"ignore"</c>) and is not a file. Its support by
    /// MAME (<c>supported</c>: yes, partial, no) is read as a driver status.
    /// </summary>
    private static DatGame ReadXmlSoftware(XmlReader xml)
    {
        var game = new DatGame
        {
            Name = xml.GetAttribute("name") ?? string.Empty,
            CloneOf = xml.GetAttribute("cloneof"),
            DriverStatus = xml.GetAttribute("supported")?.ToLowerInvariant() switch
            {
                "no" => MameDriverStatus.Preliminary,
                "partial" => MameDriverStatus.Imperfect,
                _ => MameDriverStatus.Good
            }
        };
        game.EmulationStatus = game.DriverStatus;

        if (xml.IsEmptyElement)
        {
            return game;
        }

        var depth = xml.Depth;
        DatRomEntry? previousRom = null;
        while (xml.Read() && xml.Depth > depth)
        {
            if (xml.NodeType != XmlNodeType.Element)
            {
                continue;
            }

            switch (xml.LocalName)
            {
                case "description" when xml.Depth == depth + 1:
                    game.Description = ReadXmlElementText(xml);
                    break;
                case "year" when xml.Depth == depth + 1:
                    game.Year = ReadXmlElementText(xml);
                    break;
                case "publisher" when xml.Depth == depth + 1:
                    game.Publisher = ReadXmlElementText(xml);
                    break;
                case "dataarea":
                    previousRom = null;
                    break;
                case "rom" when !string.IsNullOrEmpty(xml.GetAttribute("name")):
                    previousRom = ReadXmlRom(xml, isDisk: false);
                    game.Roms.Add(previousRom);
                    break;
                case "rom" when previousRom is not null && xml.GetAttribute("loadflag") is "continue" or "ignore":
                    // The hash file gives the size of the first piece of a ROM loaded in several: the file holds them
                    // all (mame -getsoftlist writes its whole size); a reload or a fill reads nothing more from it
                    previousRom.Size += ParseXmlSize(xml.GetAttribute("size"));
                    break;
                case "disk" when !string.IsNullOrEmpty(xml.GetAttribute("name")) && !string.IsNullOrEmpty(xml.GetAttribute("sha1")):
                    game.Roms.Add(ReadXmlRom(xml, isDisk: true));
                    break;
            }
        }

        foreach (var rom in game.Roms)
        {
            rom.GameName = game.Name;
            rom.Description = game.Description;

            // A hash file may leave out the leading zeros of a CRC (8e40a86), which MAME writes back (08e40a86)
            if (rom.Crc.Length is > 0 and < 8)
            {
                rom.Crc = rom.Crc.PadLeft(8, '0');
            }
        }

        return game;
    }

    /// <summary>
    /// Screen and controls of a machine, read from its <c>display</c>, <c>input</c> and <c>control</c> elements: the first
    /// screen gives the orientation and the type, the input the number of players, the controls their kinds and the
    /// most buttons of one of them.
    /// </summary>
    private sealed class CabinetReader
    {
        private MameOrientation _orientation;
        private MameScreenType _screen;
        private int _players;
        private MameControls _controls;
        private int _buttons;
        private bool _hasDisplay;
        private bool _hasInput;

        public void ReadDisplay(XmlReader xml)
        {
            if (_hasDisplay)
            {
                return;
            }

            _hasDisplay = true;
            _screen = EnumExtensions.ParseMameScreenType(xml.GetAttribute("type"));
            _orientation = xml.GetAttribute("rotate") is "90" or "270" ? MameOrientation.Vertical : MameOrientation.Horizontal;
        }

        public void ReadInput(XmlReader xml)
        {
            _hasInput = true;
            _ = int.TryParse(xml.GetAttribute("players"), NumberStyles.Integer, CultureInfo.InvariantCulture, out _players);
        }

        public void ReadControl(XmlReader xml)
        {
            _controls |= EnumExtensions.ParseMameControlType(xml.GetAttribute("type"));
            if (int.TryParse(xml.GetAttribute("buttons"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var buttons))
            {
                _buttons = Math.Max(_buttons, buttons);
            }
        }

        public MameCabinet? ToCabinet() =>
            _hasDisplay || _hasInput ? new MameCabinet(_orientation, _screen, Math.Max(_players, 0), _controls, _buttons) : null;
    }

    private static bool IsUnemulated(string? featureStatus) =>
        string.Equals(featureStatus, "unemulated", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Size of a ROM: decimal in the DATs and in the output of MAME, sometimes hexadecimal in the hash files of MAME
    /// (<c>size="0x1000"</c>); 0 when unreadable.
    /// </summary>
    private static long ParseXmlSize(string? value)
    {
        if (value is not null && value.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            return long.TryParse(value.AsSpan(2), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var hex) ? hex : 0;
        }

        return long.TryParse(value, out var size) ? size : 0;
    }

    private static DatRomEntry ReadXmlRom(XmlReader xml, bool isDisk)
    {
        var size = ParseXmlSize(xml.GetAttribute("size"));
        var merge = xml.GetAttribute("merge");
        return new DatRomEntry
        {
            Name = xml.GetAttribute("name") ?? string.Empty,
            Size = isDisk ? 0 : size,
            Crc = isDisk ? string.Empty : xml.GetAttribute("crc")?.ToLowerInvariant() ?? string.Empty,
            Md5 = xml.GetAttribute("md5")?.ToLowerInvariant(),
            Sha1 = xml.GetAttribute("sha1")?.ToLowerInvariant(),
            IsDisk = isDisk,
            Merge = string.IsNullOrEmpty(merge) ? null : merge,
            Status = EnumExtensions.ParseDatRomStatus(xml.GetAttribute("status"))
        };
    }

    // Text of an element, like XElement.Value; the reader is left on the end of the element, so that the caller's
    // loop goes on with the next sibling
    private static string ReadXmlElementText(XmlReader xml)
    {
        if (xml.IsEmptyElement)
        {
            return string.Empty;
        }

        var depth = xml.Depth;
        string? first = null;
        StringBuilder? builder = null;
        while (xml.Read() && xml.Depth > depth)
        {
            if (xml.NodeType is not (XmlNodeType.Text or XmlNodeType.CDATA or XmlNodeType.SignificantWhitespace))
            {
                continue;
            }

            if (first is null)
            {
                first = xml.Value;
            }
            else
            {
                builder ??= new StringBuilder(first);
                builder.Append(xml.Value);
            }
        }

        return builder?.ToString() ?? first ?? string.Empty;
    }

    private static bool IsXmlYes(string? value) => string.Equals(value, "yes", StringComparison.OrdinalIgnoreCase);
    private static void BuildCloneToParentMap(DatCatalog catalog)
    {
        foreach (var g in catalog.Games)
        {
            string? parentName = null;
            if (!string.IsNullOrEmpty(g.CloneOf))
            {
                parentName = g.CloneOf;
            }
            else if (!string.IsNullOrEmpty(g.CloneOfId) && catalog.GamesById.TryGetValue(g.CloneOfId, out var parentGame))
            {
                parentName = parentGame.Name;
                g.CloneOf = parentName;
            }

            if (!string.IsNullOrEmpty(parentName) && !string.IsNullOrEmpty(g.Name))
            {
                catalog.CloneToParent[g.Name.ToLowerInvariant()] = parentName;
            }
        }
    }

    public async Task<DatCatalog> ParseDatFileAsync(string path, CancellationToken cancellationToken = default)
    {
        if (Directory.Exists(path))
        {
            return await ParseDatFolderAsync(path, cancellationToken).ConfigureAwait(false);
        }

        var catalog = await ParseDatFileContentAsync(path, cancellationToken).ConfigureAwait(false);
        catalog.Header.SourceFile = path;
        return catalog;
    }

    private async Task<DatCatalog> ParseDatFileContentAsync(string path, CancellationToken cancellationToken)
    {
        // A file is read as UTF-8 (or as announced by its byte order mark, UTF-16 for a "mame -listxml > file" of
        // PowerShell), and as Latin-1 when it is not valid UTF-8
        var utf8Strict = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

        if (IsXmlDatFile(path))
        {
            try
            {
                return ParseXmlDatFile(path, utf8Strict, cancellationToken);
            }
            catch (DecoderFallbackException)
            {
                return ParseXmlDatFile(path, Encoding.Latin1, cancellationToken);
            }
        }

        string content;
        try
        {
            content = await File.ReadAllTextAsync(path, utf8Strict, cancellationToken).ConfigureAwait(false);
        }
        catch (DecoderFallbackException)
        {
            content = await File.ReadAllTextAsync(path, Encoding.Latin1, cancellationToken).ConfigureAwait(false);
        }

        return ParseClrMameProText(content);
    }

    // DATs of a folder read at the same time
    private const int DatFolderParallelism = 8;

    /// <summary>DATs of a folder of DATs: its <c>.xml</c> and <c>.dat</c> files, not its subfolders, in name order.</summary>
    public static IReadOnlyList<string> GetDatFolderFiles(string folder) =>
        Directory.EnumerateFiles(folder)
            .Where(f => Path.GetExtension(f).ToLowerInvariant() is ".xml" or ".dat")
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToList();

    /// <summary>
    /// Reads a folder of DATs of MAME software lists (the <c>Software List ROMs</c> DATs of a ROM manager, the hash
    /// folder of MAME): one catalog per list, gathered (<see cref="DatCatalog.SoftwareLists"/>), each list named by its
    /// DAT, or by its file without a name. Refused (<see cref="DatFolderException"/>) when it holds no DAT, a DAT of
    /// another convention, or two DATs of the same list.
    /// </summary>
    private async Task<DatCatalog> ParseDatFolderAsync(string folder, CancellationToken cancellationToken)
    {
        var files = GetDatFolderFiles(folder);
        if (files.Count == 0)
        {
            throw new DatFolderException(LocalizedMessage.Create("dat.folder_empty", ("path", folder)));
        }

        // Opening a file costs more than reading it (hundreds of DATs, an antivirus that checks each one): the DATs are
        // read in parallel, then taken in name order
        var catalogs = new DatCatalog[files.Count];
        await Parallel.ForEachAsync(
            Enumerable.Range(0, files.Count),
            new ParallelOptions { MaxDegreeOfParallelism = DatFolderParallelism, CancellationToken = cancellationToken },
            async (index, token) => catalogs[index] = await ParseDatFileAsync(files[index], token).ConfigureAwait(false)).ConfigureAwait(false);

        var lists = new List<DatCatalog>();
        var fileOfList = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index < files.Count; index++)
        {
            var file = files[index];
            var catalog = catalogs[index];
            if (catalog.Header.Convention != NamingConvention.MameSoftwareList)
            {
                throw new DatFolderException(LocalizedMessage.Create("dat.folder_not_software_list", ("file", Path.GetFileName(file))));
            }

            foreach (var list in catalog.SoftwareLists?.Values ?? [catalog])
            {
                if (string.IsNullOrWhiteSpace(list.Header.Name))
                {
                    list.Header.Name = Path.GetFileNameWithoutExtension(file);
                    list.CompleteSoftwareList();
                }

                if (!fileOfList.TryAdd(list.Header.Name, file))
                {
                    throw new DatFolderException(LocalizedMessage.Create(
                        "dat.folder_duplicate_list",
                        ("list", list.Header.Name),
                        ("first", Path.GetFileName(fileOfList[list.Header.Name])),
                        ("second", Path.GetFileName(file))));
                }

                lists.Add(list);
            }
        }

        return DatCatalog.GatherSoftwareLists(lists, Path.GetFileName(Path.TrimEndingDirectorySeparator(folder)));
    }

    /// <summary>Games read by <see cref="ReadDatPreview"/>: enough to recognize a MAME DAT by its machines.</summary>
    public const int PreviewGameCount = 50;

    // Characters of a CLRMAMEPRO DAT read by its preview
    private const int ClrMameProPreviewLength = 256 * 1024;

    // Start of a game block of a CLRMAMEPRO DAT, at the beginning of a line
    [GeneratedRegex(@"^[ \t]*(?:game|machine|resource|set)[ \t]*\(", RegexOptions.Multiline | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ClrMameProGameStartRegex();

    /// <summary>
    /// Reads the header and the first games of a DAT (at most <paramref name="maxGames"/> of an XML DAT, those of its
    /// first 256 KB for a CLRMAMEPRO DAT), as <see cref="ParseDatFileAsync"/> reads the whole file: its convention
    /// (<see cref="DatHeader.Convention"/>) is known in milliseconds, even for a DAT of several hundred megabytes. The
    /// counts of the catalog are those of the games read.
    /// </summary>
    public DatCatalog ReadDatPreview(string path, int maxGames = PreviewGameCount)
    {
        // A folder of DATs: its first DAT tells the convention, the folder names it
        if (Directory.Exists(path))
        {
            var files = GetDatFolderFiles(path);
            if (files.Count == 0)
            {
                throw new DatFolderException(LocalizedMessage.Create("dat.folder_empty", ("path", path)));
            }

            var first = ReadDatPreview(files[0], maxGames);
            first.Header.Name = Path.GetFileName(Path.TrimEndingDirectorySeparator(path));
            return first;
        }

        var utf8Strict = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
        if (IsXmlDatFile(path))
        {
            try
            {
                return ReadXmlDatPreview(path, utf8Strict, maxGames);
            }
            catch (DecoderFallbackException)
            {
                return ReadXmlDatPreview(path, Encoding.Latin1, maxGames);
            }
        }

        string head;
        try
        {
            head = ReadHead(path, utf8Strict);
        }
        catch (DecoderFallbackException)
        {
            head = ReadHead(path, Encoding.Latin1);
        }

        // A file longer than the preview is cut before its last game, which may be incomplete
        if (head.Length >= ClrMameProPreviewLength)
        {
            var lastGame = ClrMameProGameStartRegex().Matches(head).LastOrDefault();
            if (lastGame is { Index: > 0 })
            {
                head = head[..lastGame.Index];
            }
        }

        return ParseClrMameProText(head);

        static string ReadHead(string path, Encoding encoding)
        {
            using var reader = new StreamReader(path, encoding, detectEncodingFromByteOrderMarks: true);
            var buffer = new char[ClrMameProPreviewLength];
            var read = reader.ReadBlock(buffer, 0, buffer.Length);
            return new string(buffer, 0, read);
        }
    }

    private DatCatalog ReadXmlDatPreview(string path, Encoding encoding, int maxGames)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16);
        using var reader = new StreamReader(stream, encoding, detectEncodingFromByteOrderMarks: true);
        return ParseLogiqxXml(reader, maxGames, CancellationToken.None);
    }

    // An XML DAT starts with its declaration, or shows its root element in its first characters
    private static bool IsXmlDatFile(string path)
    {
        using var reader = new StreamReader(path, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        var buffer = new char[1024];
        var read = reader.ReadBlock(buffer, 0, buffer.Length);
        var head = new string(buffer, 0, read).TrimStart();
        var headSpan = head.Length > 500 ? head[..500] : head;
        return head.StartsWith("<?xml", StringComparison.OrdinalIgnoreCase) ||
               headSpan.Contains("<datafile", StringComparison.OrdinalIgnoreCase) ||
               headSpan.Contains("<mame", StringComparison.OrdinalIgnoreCase) ||
               headSpan.Contains("<softwarelist", StringComparison.OrdinalIgnoreCase);
    }

    private DatCatalog ParseXmlDatFile(string path, Encoding encoding, CancellationToken cancellationToken)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, FileOptions.SequentialScan);
        using var reader = new StreamReader(stream, encoding, detectEncodingFromByteOrderMarks: true);
        return ParseLogiqxXml(reader, int.MaxValue, cancellationToken, Path.GetFullPath(path));
    }

    public async Task<DatCatalog> CreateDatFromProcessItemsAsync(
        IEnumerable<ProcessItem> items,
        DatHeader? header = null,
        bool onlyValid = true,
        bool computeMissingChecksums = true,
        DatCatalog? datCatalog = null,
        IProgress<(int Current, int Total, string Filename)>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var catalog = new DatCatalog();
        if (header is not null)
        {
            catalog.Header = header;
        }
        else
        {
            var origName = datCatalog is not null && !string.IsNullOrEmpty(datCatalog.Header.Name)
                ? datCatalog.Header.Name
                : "RomSieve Collection";
            catalog.Header = new DatHeader
            {
                Name = datCatalog is not null ? $"{origName} (Filtered)" : "RomSieve Collection (Filtered)",
                Description = "Filtered ROM collection exported by RomSieve",
                Version = DateTime.Now.ToString("yyyy-MM-dd"),
                Author = "RomSieve",
                Convention = datCatalog?.Header.Convention ?? NamingConvention.Auto
            };
        }

        var gamesDict = new Dictionary<string, DatGame>(StringComparer.OrdinalIgnoreCase);
        var exportedItems = items.Where(it => !onlyValid || it.Status == FileStatus.Valid).ToList();

        for (var index = 0; index < exportedItems.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var item = exportedItems[index];
            progress?.Report((index, exportedItems.Count, item.DisplayName));

            // A MAME set (an archive or one of its CHDs) is the machine of the DAT: it is exported as described there,
            // without reading the archive, whose own checksums would describe nothing a ROM manager can use. A merged set
            // also holds clones of its machine, sorted with it: they are written too, else a ROM manager would take
            // their ROMs for files the archive should not hold
            if (TryGetMameSetGame(item, datCatalog) is { } mameGame)
            {
                AddMameGame(mameGame);
                foreach (var clone in item.MameSet?.HeldClones ?? [])
                {
                    if (datCatalog is not null && datCatalog.GameByName.TryGetValue(clone, out var cloneGame))
                    {
                        AddMameGame(cloneGame);
                    }
                }

                continue;
            }

            var romName = !string.IsNullOrEmpty(item.TargetFilename) ? item.TargetFilename : item.DisplayName;
            var (size, crc, md5, sha1) = await ResolveItemChecksumsAsync(item, datCatalog, computeMissingChecksums, cancellationToken).ConfigureAwait(false);

            var origGame = datCatalog?.GetGameForRom(romName, item.DatGameName);
            if (origGame is null && datCatalog is not null && !string.Equals(item.DisplayName, romName, StringComparison.Ordinal))
            {
                origGame = datCatalog.GetGameForRom(item.DisplayName);
            }

            string gameName;
            string gameDesc;
            string? cloneOf;
            string? cloneOfId;
            string? gameId;

            if (origGame is not null)
            {
                gameName = origGame.Name;
                gameDesc = !string.IsNullOrEmpty(origGame.Description) ? origGame.Description : origGame.Name;
                cloneOf = origGame.CloneOf;
                cloneOfId = origGame.CloneOfId;
                gameId = origGame.Id;
            }
            else
            {
                gameName = Path.GetFileNameWithoutExtension(romName);
                gameDesc = gameName;
                cloneOf = !string.IsNullOrEmpty(item.Parsed?.CloneOf) ? item.Parsed.CloneOf : null;
                cloneOfId = !string.IsNullOrEmpty(item.Parsed?.CloneOfId) ? item.Parsed.CloneOfId : null;
                gameId = null;
            }

            if (string.IsNullOrEmpty(cloneOf) && datCatalog is not null)
            {
                if (datCatalog.CloneToParent.TryGetValue(gameName.ToLowerInvariant(), out var parentFromMap))
                {
                    cloneOf = parentFromMap;
                }
                else if (!string.IsNullOrEmpty(cloneOfId) && datCatalog.GamesById.TryGetValue(cloneOfId, out var parentGame))
                {
                    cloneOf = parentGame.Name;
                }
            }

            if (!gamesDict.TryGetValue(gameName, out var gameObj))
            {
                gameObj = new DatGame
                {
                    Name = gameName,
                    Description = gameDesc,
                    Id = gameId,
                    CloneOf = cloneOf,
                    CloneOfId = cloneOfId,
                    Roms = []
                };
                gamesDict[gameName] = gameObj;
                catalog.AddGame(gameObj);
            }

            var romEntry = new DatRomEntry
            {
                Name = romName,
                Size = size,
                Crc = crc,
                Md5 = md5,
                Sha1 = sha1,
                GameName = gameName,
                Description = gameDesc
            };

            if (gameObj.Roms.All(r => !string.Equals(r.Name, romEntry.Name, StringComparison.Ordinal)))
            {
                gameObj.Roms.Add(romEntry);
                catalog.AddEntry(romEntry);
            }

            foreach (var relItem in item.RelatedItems)
            {
                var relRomName = !string.IsNullOrEmpty(relItem.TargetFilename) ? relItem.TargetFilename : relItem.DisplayName;
                var (relSize, relCrc, relMd5, relSha1) = await ResolveItemChecksumsAsync(relItem, datCatalog, computeMissingChecksums, cancellationToken).ConfigureAwait(false);
                var relEntry = new DatRomEntry
                {
                    Name = relRomName,
                    Size = relSize,
                    Crc = relCrc,
                    Md5 = relMd5,
                    Sha1 = relSha1,
                    GameName = gameName,
                    Description = gameDesc
                };

                if (gameObj.Roms.All(r => !string.Equals(r.Name, relEntry.Name, StringComparison.Ordinal)))
                {
                    gameObj.Roms.Add(relEntry);
                    catalog.AddEntry(relEntry);
                }
            }
        }

        progress?.Report((exportedItems.Count, exportedItems.Count, string.Empty));
        return catalog;

        void AddMameGame(DatGame game)
        {
            if (!gamesDict.ContainsKey(game.Name))
            {
                var copy = DatCatalog.CopyGame(game, game.Roms);
                gamesDict[copy.Name] = copy;
                catalog.AddGame(copy);
            }
        }
    }

    public async Task<DatCatalog> ExportDatFileAsync(
        IEnumerable<ProcessItem> items,
        string outputPath,
        DatExportFormat format = DatExportFormat.LogiqxXml,
        DatHeader? header = null,
        bool onlyValid = true,
        bool computeMissingChecksums = true,
        DatCatalog? datCatalog = null,
        IProgress<(int Current, int Total, string Filename)>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var catalog = await CreateDatFromProcessItemsAsync(
            items,
            header,
            onlyValid,
            computeMissingChecksums,
            datCatalog,
            progress,
            cancellationToken).ConfigureAwait(false);

        if (IsClrMameProOutput(format, outputPath))
        {
            await catalog.WriteClrMameProAsync(outputPath, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            await catalog.WriteLogiqxXmlAsync(outputPath, cancellationToken).ConfigureAwait(false);
        }

        return catalog;
    }

    /// <summary>
    /// The DATs (<c>.xml</c>, <c>.dat</c>) at the root of <paramref name="folder"/>, by path, with the time they were
    /// last written; none when the folder does not exist. Taken before an export into the folder, it tells which DATs
    /// the export replaces and which it leaves (<see cref="GetUntouchedDatFiles"/>).
    /// </summary>
    public static IReadOnlyDictionary<string, DateTime> GetDatFiles(string folder)
    {
        var files = new Dictionary<string, DateTime>(StringComparer.Ordinal);
        try
        {
            if (!Directory.Exists(folder))
            {
                return files;
            }

            foreach (var file in Directory.EnumerateFiles(folder))
            {
                if (Path.GetExtension(file).ToLowerInvariant() is ".xml" or ".dat")
                {
                    files[file] = File.GetLastWriteTimeUtc(file);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            // A folder that cannot be listed has no DAT to report
        }

        return files;
    }

    /// <summary>
    /// File names of the DATs of <paramref name="before"/> (<see cref="GetDatFiles"/>) that an export into their folder
    /// did not rewrite, sorted: a ROM manager that reads the folder reads them too.
    /// </summary>
    public static IReadOnlyList<string> GetUntouchedDatFiles(IReadOnlyDictionary<string, DateTime> before)
    {
        var untouched = new List<string>();
        foreach (var (file, written) in before)
        {
            try
            {
                if (File.Exists(file) && File.GetLastWriteTimeUtc(file) == written)
                {
                    untouched.Add(Path.GetFileName(file));
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                // A DAT that cannot be read is not reported
            }
        }

        untouched.Sort(StringComparer.OrdinalIgnoreCase);
        return untouched;
    }

    /// <summary>
    /// Writes the software of <paramref name="dat"/> as a MAME software list, the format MAME reads in its hash folder:
    /// each <c>&lt;software&gt;</c> element is copied as it is from the file the list was read from
    /// (<see cref="DatHeader.SoftwareListFile"/> of <paramref name="source"/>), with its parts, data areas and features
    /// that a DAT does not hold, under the <c>&lt;softwarelist&gt;</c> of that file. A <c>cloneof</c> naming a software
    /// left out is removed: MAME refuses a clone whose parent is not in the list.
    /// </summary>
    public static async Task WriteMameSoftwareListAsync(DatCatalog dat, DatHeader source, string path, CancellationToken cancellationToken = default)
    {
        var sourceFile = source.SoftwareListFile ?? throw new ArgumentException("The list was not read from a MAME software list.", nameof(source));
        byte[] content;
        try
        {
            content = BuildMameSoftwareList(dat, source, sourceFile, new UTF8Encoding(false, throwOnInvalidBytes: true), cancellationToken);
        }
        catch (DecoderFallbackException)
        {
            content = BuildMameSoftwareList(dat, source, sourceFile, Encoding.Latin1, cancellationToken);
        }

        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        await File.WriteAllBytesAsync(path, content, cancellationToken).ConfigureAwait(false);
    }

    private static byte[] BuildMameSoftwareList(DatCatalog dat, DatHeader source, string sourceFile, Encoding encoding, CancellationToken cancellationToken)
    {
        var kept = dat.Games.Select(g => g.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var readerSettings = new XmlReaderSettings
        {
            // Same rules as the reading of the DAT
            DtdProcessing = DtdProcessing.Parse,
            XmlResolver = null,
            MaxCharactersFromEntities = 10_000_000,
            IgnoreComments = true,
            IgnoreProcessingInstructions = true,
            IgnoreWhitespace = true
        };
        var writerSettings = new XmlWriterSettings
        {
            Encoding = new UTF8Encoding(false),
            Indent = true,
            IndentChars = "\t",
            NewLineChars = "\n"
        };

        using var stream = new FileStream(sourceFile, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, FileOptions.SequentialScan);
        using var textReader = new StreamReader(stream, encoding, detectEncodingFromByteOrderMarks: true);
        using var xml = XmlReader.Create(textReader, readerSettings);
        using var output = new MemoryStream();
        using (var writer = XmlWriter.Create(output, writerSettings))
        {
            writer.WriteStartDocument();
            writer.WriteDocType("softwarelist", null, "softwarelist.dtd", null);

            // The root of hash/<list>.xml is the list; an output of MAME holds a <softwarelist> per list
            var isListRoot = false;
            var isListStarted = false;
            string? currentList = null;
            xml.MoveToContent();
            while (!xml.EOF)
            {
                if (xml.NodeType != XmlNodeType.Element)
                {
                    xml.Read();
                    continue;
                }

                if (xml.LocalName == "software" && (isListRoot || string.Equals(currentList, source.Name, StringComparison.OrdinalIgnoreCase)))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (!kept.Contains(xml.GetAttribute("name") ?? string.Empty))
                    {
                        xml.Skip();
                        continue;
                    }

                    var software = (System.Xml.Linq.XElement)System.Xml.Linq.XNode.ReadFrom(xml);
                    var cloneOf = software.Attribute("cloneof");
                    if (cloneOf is not null && !kept.Contains(cloneOf.Value))
                    {
                        cloneOf.Remove();
                    }

                    software.WriteTo(writer);
                    continue;
                }

                if (xml.LocalName == "softwarelist" && !isListStarted)
                {
                    isListRoot = xml.Depth == 0;
                    currentList = xml.GetAttribute("name");
                    if (isListRoot || string.Equals(currentList, source.Name, StringComparison.OrdinalIgnoreCase))
                    {
                        WriteSoftwareListStart(writer, currentList ?? source.Name, xml.GetAttribute("description") ?? source.Description);
                        isListStarted = true;
                    }
                }
                else if (xml.LocalName == "softwarelist")
                {
                    currentList = xml.GetAttribute("name");
                }

                xml.Read();
            }

            if (!isListStarted)
            {
                WriteSoftwareListStart(writer, source.Name, source.Description);
            }

            writer.WriteEndElement();
            writer.WriteEndDocument();
        }

        return output.ToArray();
    }

    private static void WriteSoftwareListStart(XmlWriter writer, string name, string description)
    {
        writer.WriteStartElement("softwarelist");
        writer.WriteAttributeString("name", name);
        if (!string.IsNullOrEmpty(description))
        {
            writer.WriteAttributeString("description", description);
        }
    }

    /// <summary>
    /// Whether a DAT export writes the CLRMAMEPRO format: asked for, or, for <see cref="DatExportFormat.Auto"/>, a
    /// <c>.dat</c> file that is not a <c>.xml.dat</c>.
    /// </summary>
    public static bool IsClrMameProOutput(DatExportFormat format, string outputPath) =>
        format == DatExportFormat.ClrMamePro ||
        (format == DatExportFormat.Auto &&
         outputPath.EndsWith(".dat", StringComparison.OrdinalIgnoreCase) &&
         !outputPath.EndsWith(".xml.dat", StringComparison.OrdinalIgnoreCase));

    private static DatGame? TryGetMameSetGame(ProcessItem item, DatCatalog? datCatalog)
    {
        if (datCatalog is null ||
            string.IsNullOrEmpty(item.DatGameName) ||
            (!item.IsWholeArchive && item.Parsed?.Convention.IsMameSets() != true))
        {
            return null;
        }

        return datCatalog.GameByName.TryGetValue(item.DatGameName, out var game) ? game : null;
    }

    /// <summary>
    /// The entry of the DAT a file was identified as: same CRC32 (or SHA-1 for a disk), in the game of the
    /// identification. Its checksums describe the file without reading it. A file matched by its name only is not
    /// verified, so it has none.
    /// </summary>
    private static DatRomEntry? FindIdentifiedDatEntry(ProcessItem item, DatCatalog? datCatalog)
    {
        if (datCatalog is null || string.IsNullOrEmpty(item.DatGameName) || item.IsIdentifiedByName)
        {
            return null;
        }

        List<DatRomEntry>? candidates = null;
        if (!string.IsNullOrEmpty(item.Crc32))
        {
            datCatalog.ByCrc.TryGetValue(item.Crc32, out candidates);
        }
        else if (!string.IsNullOrEmpty(item.Sha1))
        {
            datCatalog.BySha1.TryGetValue(item.Sha1, out candidates);
        }

        if (candidates is null)
        {
            return null;
        }

        var matches = candidates
            .Where(e => string.Equals(e.GameName, item.DatGameName, StringComparison.OrdinalIgnoreCase) &&
                        (string.IsNullOrEmpty(item.Sha1) || string.IsNullOrEmpty(e.Sha1) ||
                         string.Equals(e.Sha1, item.Sha1, StringComparison.OrdinalIgnoreCase)))
            .ToList();
        return matches.Count == 0
            ? null
            : datCatalog.SelectMatch(matches, Path.GetFileName(item.ArchiveMember ?? item.SourcePath));
    }

    private async Task<(long Size, string Crc, string? Md5, string? Sha1)> ResolveItemChecksumsAsync(
        ProcessItem item,
        DatCatalog? datCatalog,
        bool computeMissing,
        CancellationToken cancellationToken)
    {
        var size = item.FileSize;
        var crc = item.Crc32?.ToLowerInvariant() ?? string.Empty;
        var md5 = item.Md5?.ToLowerInvariant();
        var sha1 = item.Sha1?.ToLowerInvariant();

        // An identified file is its entry of the DAT: size and checksums are those of the DAT (without a copier
        // header), and reading the file again would only compute what the DAT already says
        if (FindIdentifiedDatEntry(item, datCatalog) is { } datEntry)
        {
            if (!datEntry.IsDisk)
            {
                size = datEntry.Size;
            }

            if (!string.IsNullOrEmpty(datEntry.Crc))
            {
                crc = datEntry.Crc.ToLowerInvariant();
            }

            md5 = string.IsNullOrEmpty(datEntry.Md5) ? md5 : datEntry.Md5.ToLowerInvariant();
            sha1 = string.IsNullOrEmpty(datEntry.Sha1) ? sha1 : datEntry.Sha1.ToLowerInvariant();
        }

        if (!computeMissing || (!string.IsNullOrEmpty(crc) && !string.IsNullOrEmpty(md5) && !string.IsNullOrEmpty(sha1)))
        {
            return (size, crc, md5, sha1);
        }

        if (!string.IsNullOrEmpty(item.ArchivePath) &&
            !string.IsNullOrEmpty(item.ArchiveMember) &&
            File.Exists(item.ArchivePath))
        {
            try
            {
                await using var stream = _checksumService.OpenArchiveMember(item.ArchivePath, item.ArchiveMember);
                if (string.IsNullOrEmpty(crc))
                {
                    crc = stream.ExpectedCrc32;
                }

                if (string.IsNullOrEmpty(md5) || string.IsNullOrEmpty(sha1))
                {
                    using var hMd5 = IncrementalHash.CreateHash(HashAlgorithmName.MD5);
                    using var hSha1 = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);
                    var buffer = new byte[65536];
                    int read;
                    while ((read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
                    {
                        hMd5.AppendData(buffer, 0, read);
                        hSha1.AppendData(buffer, 0, read);
                    }

                    if (string.IsNullOrEmpty(md5))
                        md5 = Convert.ToHexString(hMd5.GetHashAndReset()).ToLowerInvariant();
                    if (string.IsNullOrEmpty(sha1))
                        sha1 = Convert.ToHexString(hSha1.GetHashAndReset()).ToLowerInvariant();
                }
            }
            catch
            {
                // Ignore archive errors
            }
        }
        else
        {
            var candidatePaths = new List<string>();
            if (!string.IsNullOrEmpty(item.FinalTargetPath) && File.Exists(item.FinalTargetPath))
                candidatePaths.Add(item.FinalTargetPath);
            if (!string.IsNullOrEmpty(item.SourcePath) && File.Exists(item.SourcePath))
                candidatePaths.Add(item.SourcePath);

            foreach (var p in candidatePaths)
            {
                try
                {
                    var (c, m, s) = await _checksumService.ComputeFileChecksumsAsync(
                        p,
                        computeHashes: true,
                        cancellationToken: cancellationToken).ConfigureAwait(false);
                    if (string.IsNullOrEmpty(crc)) crc = c;
                    if (string.IsNullOrEmpty(md5)) md5 = m;
                    if (string.IsNullOrEmpty(sha1)) sha1 = s;
                    break;
                }
                catch (IOException)
                {
                }
            }
        }

        return (size, crc, md5, sha1);
    }
}
