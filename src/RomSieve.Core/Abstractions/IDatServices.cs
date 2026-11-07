using RomSieve.Core.Models;
using RomSieve.Core.Services;

namespace RomSieve.Core.Abstractions;

/// <summary>
/// Service for computing checksums (CRC32, MD5, SHA1), detecting copier headers,
/// and inspecting ZIP archives without prior extraction.
/// </summary>
public interface IChecksumService
{
    int DetectCopierHeader(ReadOnlySpan<byte> headerBytes, long fileSize);
    Task<string> ComputeFileCrc32Async(string path, long offset = 0, int chunkSize = 65536, CancellationToken cancellationToken = default);
    Task<(string Crc32, string? Md5, string? Sha1)> ComputeFileChecksumsAsync(
        string path,
        long offset = 0,
        int chunkSize = 65536,
        bool computeHashes = true,
        CancellationToken cancellationToken = default);

    /// <summary>SHA-1 (40 lowercase hex characters) of a file, from <paramref name="offset"/> (to skip a copier header).</summary>
    Task<string> ComputeFileSha1Async(string path, long offset = 0, CancellationToken cancellationToken = default);

    /// <summary>SHA-1 of an archive member, which has to be decompressed (the archive only stores its CRC32).</summary>
    Task<string> ComputeArchiveMemberSha1Async(string archivePath, string memberName, CancellationToken cancellationToken = default);

    bool IsZipArchive(string path);
    IReadOnlyList<ArchiveMember> GetZipArchiveMembers(string zipPath);

    /// <summary>Whether the members of this file are inspected: ZIP, 7z or RAR archive.</summary>
    bool IsSupportedArchive(string path);

    /// <summary>
    /// Members of a ZIP, 7z or RAR archive with the size and CRC32 recorded in the archive (empty CRC when it has none),
    /// without extraction; empty for an unreadable archive.
    /// </summary>
    IReadOnlyList<ArchiveMember> GetArchiveMembers(string archivePath);

    /// <summary>Decompressed data of an archive member; disposing the stream closes the archive.</summary>
    ArchiveMemberStream OpenArchiveMember(string archivePath, string memberName);

    /// <summary>
    /// Members of a ZIP, 7z or RAR archive whose data does not match the CRC32 the archive records for them, or cannot be
    /// decompressed: every member is decompressed, in the order of the archive (a solid block is read once). A member
    /// without a recorded CRC32 cannot be checked and is never listed. Throws when the archive itself cannot be opened.
    /// </summary>
    IReadOnlyList<DamagedArchiveMember> FindDamagedMembers(string archivePath, CancellationToken cancellationToken = default);

    /// <summary>
    /// Header (versions 3 to 5) and track metadata of a CHD file, read without decompressing its data;
    /// throws <see cref="System.IO.InvalidDataException"/> when the file is not a readable CHD.
    /// </summary>
    ChdInfo ReadChdInfo(string path);
}

/// <summary>
/// Persistent disk cache of what the headers of archives and CHDs say (<c>header_cache.json</c>, next to
/// <c>crc_cache.json</c>): an unchanged file (same last write time and size) is not opened again. Reading these
/// headers costs little on a local disk, but several network round trips per file on a network share.
/// </summary>
public interface IHeaderCache
{
    /// <summary>
    /// Members of an archive (<see cref="IChecksumService.GetArchiveMembers"/>), read by
    /// <paramref name="checksumService"/> unless cached. An empty result (unreadable archive) is never cached.
    /// </summary>
    IReadOnlyList<ArchiveMember> GetArchiveMembers(string archivePath, IChecksumService checksumService);

    /// <summary>
    /// Header and tracks of a CHD (<see cref="IChecksumService.ReadChdInfo"/>), read by
    /// <paramref name="checksumService"/> unless cached; its exceptions are thrown, and nothing is cached then.
    /// </summary>
    ChdInfo ReadChdInfo(string path, IChecksumService checksumService);

    /// <summary>
    /// Members of an archive whose data does not match their CRC32 (<see cref="IChecksumService.FindDamagedMembers"/>),
    /// decompressed by <paramref name="checksumService"/> unless the archive was checked since it last changed; an
    /// intact archive is cached too. Its exceptions are thrown, and nothing is cached then.
    /// </summary>
    IReadOnlyList<DamagedArchiveMember> FindDamagedMembers(string archivePath, IChecksumService checksumService, CancellationToken cancellationToken = default);
}

/// <summary>
/// Persistent disk cache of computed CRC32 checksums (<c>crc_cache.json</c>), with the cache of the headers of
/// archives and CHDs next to it (<see cref="Headers"/>), loaded and saved with it.
/// </summary>
public interface ICrcCacheService
{
    string CachePath { get; }

    /// <summary>Cache of the headers of archives and CHDs, in the folder of <see cref="CachePath"/>.</summary>
    IHeaderCache Headers { get; }

    Task LoadAsync(CancellationToken cancellationToken = default);
    Task SaveAsync(CancellationToken cancellationToken = default);
    string? Get(string path);
    void Set(string path, string crc);

    /// <summary>Cached SHA-1 of a file, when it was computed and the file is unchanged since.</summary>
    string? GetSha1(string path);

    /// <summary>Caches the CRC32 and the SHA-1 of a file.</summary>
    void SetSha1(string path, string crc, string sha1);

    /// <summary>
    /// Moves the cached entry of a file that was moved or renamed (its last write time and size are unchanged).
    /// </summary>
    void Rename(string oldPath, string newPath);
}

/// <summary>
/// Parser and exporter for DAT files (CLRMAMEPRO and Logiqx XML).
/// </summary>
public interface IDatService
{
    DatCatalog ParseClrMameProText(string text);
    DatCatalog ParseLogiqxXml(string text);
    Task<DatCatalog> ParseDatFileAsync(string path, CancellationToken cancellationToken = default);

    /// <summary>
    /// Reads a DAT, or a folder of DATs as <paramref name="folderOptions"/> says: with its subfolders or not, without
    /// the DATs left out. A folder of No-Intro, Redump or TOSEC DATs gives a lot (<see cref="DatCatalog.Dats"/>).
    /// </summary>
    Task<DatCatalog> ParseDatFileAsync(string path, DatFolderOptions folderOptions, CancellationToken cancellationToken = default);

    /// <summary>
    /// The DATs of a folder of DATs (path relative to the folder, header name, convention), from their headers only,
    /// in the order a lot takes them: the list of the DATs to check, the refusals told before the DATs are read
    /// (<see cref="DatService.CheckDatFolder"/>).
    /// </summary>
    IReadOnlyList<DatFolderEntry> ReadDatFolderPreview(string folder, bool recursive = false);

    /// <summary>Header and first games of a DAT, read in milliseconds whatever its size: enough to know its convention.</summary>
    DatCatalog ReadDatPreview(string path, int maxGames = DatService.PreviewGameCount);
    Task<DatCatalog> CreateDatFromProcessItemsAsync(
        IEnumerable<ProcessItem> items,
        DatHeader? header = null,
        bool onlyValid = true,
        bool computeMissingChecksums = true,
        DatCatalog? datCatalog = null,
        IProgress<(int Current, int Total, string Filename)>? progress = null,
        CancellationToken cancellationToken = default);
    Task<DatCatalog> ExportDatFileAsync(
        IEnumerable<ProcessItem> items,
        string outputPath,
        DatExportFormat format = DatExportFormat.LogiqxXml,
        DatHeader? header = null,
        bool onlyValid = true,
        bool computeMissingChecksums = true,
        DatCatalog? datCatalog = null,
        IProgress<(int Current, int Total, string Filename)>? progress = null,
        CancellationToken cancellationToken = default);
}
