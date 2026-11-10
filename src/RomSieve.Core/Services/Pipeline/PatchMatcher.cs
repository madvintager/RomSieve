using System.Globalization;
using System.IO;
using System.IO.Hashing;
using System.Text.RegularExpressions;
using RomSieve.Core.Abstractions;
using RomSieve.Core.Models;

namespace RomSieve.Core.Services;

/// <summary>
/// Step of the pipeline that matches the patches of the patch folder (<see cref="SortConfig.PatchDir"/>) with the files
/// of the source identified in the DAT, and adds a patched version for each (<see cref="ProcessItem.Patch"/>). It runs
/// after the release selection, so that a patched version stays outside the 1G1R, the multi-disk sets and the filters,
/// and before the selection of a partial sort, which it follows like any file of its game. The rules:
/// <list type="bullet">
/// <item>The origin of a UPS or a BPS is the file of the CRC32 and the size of the source it records; that of an IPS,
/// which records nothing, the file of a CRC32 its name gives (the expression of igir, on the file name only).</item>
/// <item>Only a file identified in the DAT is an origin, whatever its status after the 1G1R (a translation often aims
/// at the Japanese release the 1G1R leaves out); never a CHD, a MAME set or a file of a game in several files.</item>
/// <item>The CRC32 of a file is looked for with and without its copier header: as identified first, then in its other
/// form, computed only when a patch is left without origin. The patch applies to the form it expects.</item>
/// <item>Several files of the same CRC32: the one the sort writes, then the first by path. Several patches of a file:
/// a patched version each.</item>
/// <item>The patched version is named after its patch, with the extension of the origin; the CRC32 in the name of an
/// IPS is removed with its brackets. It goes into the folder of the origin, just before it in the list (a move leaves
/// the source after the patched version is written).</item>
/// </list>
/// A patch that gives no patched version is reported (<see cref="Unapplied"/>, a line of the log each).
/// </summary>
public sealed partial class PatchMatcher
{
    private readonly SortConfig _config;
    private readonly IPatchService _patchService;
    private readonly IChecksumService _checksumService;
    private readonly ICrcCacheService _crcCache;
    private readonly Action<LogLevel, LocalizedMessage> _log;
    private readonly List<UnappliedPatch> _unapplied = [];

    public PatchMatcher(
        SortConfig config,
        IPatchService patchService,
        IChecksumService checksumService,
        ICrcCacheService crcCache,
        Action<LogLevel, LocalizedMessage> log)
    {
        _config = config;
        _patchService = patchService;
        _checksumService = checksumService;
        _crcCache = crcCache;
        _log = log;
    }

    /// <summary>The patches that gave no patched version, in the order of their paths.</summary>
    public IReadOnlyList<UnappliedPatch> Unapplied => _unapplied;

    /// <summary>A form of the data of a file the patch may expect: the bytes from an offset, their size and CRC32.</summary>
    private sealed record Candidate(ProcessItem Item, long Offset, long Size, uint Crc32, bool IsMultiFile);

    /// <summary>A patch read, with the CRC32 its origin must have: one for a UPS or a BPS, those of its name for an IPS.</summary>
    private sealed record ReadPatch(PatchFile File, PatchHeader Header, IReadOnlyList<uint> SourceCrcs);

    /// <summary>
    /// Reads <paramref name="patches"/>, matches them with the identified files of <paramref name="items"/>, and inserts
    /// each patched version just before its origin. Returns the number of patched versions.
    /// </summary>
    public async Task<int> MatchAsync(
        List<ProcessItem> items,
        IReadOnlyList<PatchFile> patches,
        CancellationToken cancellationToken = default,
        IProgress<SortProgress>? progress = null)
    {
        progress?.Report(new SortProgress(0, 0, 0, 0, 0.0, LocalizedMessage.Create("progress.reading_patches")));
        var read = new List<ReadPatch>();
        foreach (var patch in patches)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (await ReadAsync(patch, cancellationToken).ConfigureAwait(false) is { } readPatch)
            {
                read.Add(readPatch);
            }
        }

        var candidates = CollectCandidates(items);
        var index = candidates.ToLookup(c => c.Crc32).ToDictionary(g => g.Key, g => g.ToList());
        var matched = new List<(ReadPatch Patch, Candidate Origin)>();
        var left = new List<ReadPatch>();
        foreach (var patch in read)
        {
            var (origin, noOrigin) = Match(patch, index, final: false);
            if (origin is not null)
            {
                matched.Add((patch, origin));
            }
            else if (noOrigin)
            {
                left.Add(patch);
            }
        }

        // The other form of each file (with or without its copier header) is only read for the patches left over
        if (left.Count > 0 && _config.Filters.DetectCopierHeaders)
        {
            foreach (var other in await ReadOtherFormsAsync(candidates, left, cancellationToken).ConfigureAwait(false))
            {
                if (!index.TryGetValue(other.Crc32, out var list))
                {
                    list = [];
                    index[other.Crc32] = list;
                }

                list.Add(other);
            }
        }

        foreach (var patch in left)
        {
            if (Match(patch, index, final: true).Origin is { } origin)
            {
                matched.Add((patch, origin));
            }
        }

        // Each patched version just before its origin, in the order of the patches
        var byOrigin = new Dictionary<ProcessItem, List<ProcessItem>>(ReferenceEqualityComparer.Instance);
        foreach (var (patch, origin) in matched.OrderBy(m => m.Patch.File.SourcePath, StringComparer.Ordinal)
                     .ThenBy(m => m.Patch.File.ArchiveMember, StringComparer.Ordinal))
        {
            var patched = CreatePatchedVersion(patch, origin);
            origin.Item.PatchedVersions.Add(patched);
            if (!byOrigin.TryGetValue(origin.Item, out var list))
            {
                list = [];
                byOrigin[origin.Item] = list;
            }

            list.Add(patched);
        }

        if (byOrigin.Count > 0)
        {
            var ordered = new List<ProcessItem>(items.Count + matched.Count);
            foreach (var item in items)
            {
                if (byOrigin.TryGetValue(item, out var patchedVersions))
                {
                    ordered.AddRange(patchedVersions);
                }

                ordered.Add(item);
            }

            items.Clear();
            items.AddRange(ordered);
        }

        _unapplied.Sort((a, b) =>
        {
            var byPath = string.CompareOrdinal(a.File.SourcePath, b.File.SourcePath);
            return byPath != 0 ? byPath : string.CompareOrdinal(a.File.ArchiveMember, b.File.ArchiveMember);
        });
        foreach (var unapplied in _unapplied)
        {
            _log(LogLevel.Warn, LocalizedMessage.Create("log.patch_not_applied", ("name", unapplied.File.DisplayName), ("reason", unapplied.Reason)));
        }

        _log(LogLevel.Info, LocalizedMessage.Create("log.patches_summary", ("patched", matched.Count), ("unapplied", _unapplied.Count)));
        return matched.Count;
    }

    /// <summary>
    /// The option to write the patched versions only (<see cref="SortConfig.PatchOnly"/>): an origin the release selection
    /// kept, and that a patched version replaces, is left out (<see cref="ProcessItem.IsReplacedByPatch"/>). An origin
    /// the selection already left out keeps its reason. Called after every selection, the recalculation of a game too.
    /// </summary>
    public static void ApplyPatchOnly(IEnumerable<ProcessItem> items, bool patchOnly)
    {
        foreach (var origin in items)
        {
            if (origin.PatchedVersions.Count == 0)
            {
                continue;
            }

            origin.IsReplacedByPatch = false;
            if (!patchOnly || origin.Status != FileStatus.Valid)
            {
                continue;
            }

            origin.Status = FileStatus.Excluded;
            origin.ReleaseStatus = FileStatus.Excluded;
            origin.StatusReason = LocalizedMessage.Create(
                "reason.replaced_by_patch",
                ("patches", string.Join(", ", origin.PatchedVersions.Select(p => p.DisplayName))));
            origin.IsReplacedByPatch = true;
        }
    }

    // =========================================================================
    // Reading the patches
    // =========================================================================

    private async Task<ReadPatch?> ReadAsync(PatchFile patch, CancellationToken cancellationToken)
    {
        byte[] bytes;
        try
        {
            await using Stream stream = patch.ArchiveMember is { } member
                ? _checksumService.OpenArchiveMember(patch.SourcePath, member)
                : new FileStream(patch.SourcePath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true);
            bytes = await _patchService.ReadPatchAsync(stream, cancellationToken).ConfigureAwait(false);
        }
        catch (PatchException ex)
        {
            Refuse(patch, LocalizedMessage.Create("reason.patch_unreadable", ("error", ex.Reason)));
            return null;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            Refuse(patch, LocalizedMessage.Create("reason.patch_unreadable", ("error", LocalizedMessage.Verbatim(ex.Message))));
            return null;
        }

        if (_patchService.DetectFormat(patch.DisplayName, bytes.AsSpan(0, Math.Min(bytes.Length, PatchService.SignatureLength))) is null)
        {
            Refuse(patch, LocalizedMessage.Create("reason.patch_unreadable", ("error", LocalizedMessage.Create("patch.error_signature"))));
            return null;
        }

        PatchHeader header;
        try
        {
            header = _patchService.ReadHeader(bytes);
        }
        catch (PatchException ex)
        {
            Refuse(patch, LocalizedMessage.Create("reason.patch_unreadable", ("error", ex.Reason)));
            return null;
        }

        if (header.SourceCrc32 is { } sourceCrc)
        {
            return new ReadPatch(patch, header, [sourceCrc]);
        }

        // An IPS records no checksum: its name gives the CRC32 of its origin, as igir reads it
        var crcs = CrcTokens(BaseName(patch.DisplayName)).Select(t => t.Crc).Distinct().ToList();
        if (crcs.Count == 0)
        {
            Refuse(patch, LocalizedMessage.Create("reason.patch_no_crc"));
            return null;
        }

        return new ReadPatch(patch, header, crcs);
    }

    private void Refuse(PatchFile patch, LocalizedMessage reason) => _unapplied.Add(new UnappliedPatch(patch, reason));

    /// <summary>
    /// The CRC32 in a file name, as igir reads it in the name of a patch that records none: 8 hexadecimal digits, after an
    /// optional <c>0x</c>, neither preceded nor followed by a letter or a digit (<c>[47e70e08]</c>, <c>(0x47E70E08)</c>,
    /// <c>_47e70e08_</c>). The folders of the path are not read.
    /// </summary>
    [GeneratedRegex(@"(?<![a-z0-9])(?:0x)?([a-f0-9]{8})(?![a-z0-9])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex CrcTokenRegex();

    private static IEnumerable<(uint Crc, Match Match)> CrcTokens(string baseName) =>
        CrcTokenRegex().Matches(baseName)
            .Select(m => (uint.Parse(m.Groups[1].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture), m));

    /// <summary>The name of a patch without its folders nor its extension.</summary>
    private static string BaseName(string patchName) => Path.GetFileNameWithoutExtension(Path.GetFileName(patchName));

    // =========================================================================
    // Matching
    // =========================================================================

    /// <summary>
    /// The forms of the files the patches may apply to, as identified: every file identified in the DAT, whatever its
    /// status, but the CHDs and the MAME sets; the files of a game in several files are kept, to say why their patches
    /// are not applied.
    /// </summary>
    private static List<Candidate> CollectCandidates(IEnumerable<ProcessItem> items)
    {
        var candidates = new List<Candidate>();
        foreach (var item in items)
        {
            if (!IsOrigin(item))
            {
                continue;
            }

            var isMultiFile = item.RelatedItems.Count > 0 || item.IsMultiMedia;
            AddIdentifiedForm(candidates, item, isMultiFile);
            foreach (var related in item.RelatedItems.Where(IsOrigin))
            {
                AddIdentifiedForm(candidates, related, isMultiFile: true);
            }
        }

        return candidates;
    }

    private static bool IsOrigin(ProcessItem item) =>
        item.Patch is null
        && !string.IsNullOrEmpty(item.DatGameName)
        && item.Status is not (FileStatus.Unmatched or FileStatus.Error)
        && item.Chd is null
        && !item.IsWholeArchive
        && !item.IsIdentifiedByName;

    private static void AddIdentifiedForm(List<Candidate> candidates, ProcessItem item, bool isMultiFile)
    {
        if (uint.TryParse(item.Crc32, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var crc))
        {
            candidates.Add(new Candidate(item, item.CopierHeaderSize, item.FileSize - item.CopierHeaderSize, crc, isMultiFile));
        }
    }

    /// <summary>
    /// The origin of a patch among the candidates of its CRC32. A patch refused (ambiguous, its origin in a game of
    /// several files) is reported at once; a patch without origin (<c>NoOrigin</c>) only when <paramref name="final"/>,
    /// otherwise it is left for a second try with the other forms of the files.
    /// </summary>
    private (Candidate? Origin, bool NoOrigin) Match(ReadPatch patch, Dictionary<uint, List<Candidate>> index, bool final)
    {
        var sizeMismatch = false;
        var byCrc = new List<(uint Crc, Candidate Best)>();
        foreach (var crc in patch.SourceCrcs)
        {
            if (!index.TryGetValue(crc, out var list))
            {
                continue;
            }

            var fitting = list.Where(c => patch.Header.SourceSize is not { } size || c.Size == size).ToList();
            if (fitting.Count == 0)
            {
                sizeMismatch = true;
                continue;
            }

            byCrc.Add((crc, ChooseOrigin(fitting)));
        }

        if (byCrc.Count == 0)
        {
            if (final)
            {
                var crcs = string.Join(", ", patch.SourceCrcs.Select(FormatCrc));
                Refuse(patch.File, sizeMismatch && patch.Header.SourceSize is { } expected
                    ? LocalizedMessage.Create("reason.patch_source_size_mismatch", ("crc", crcs), ("size", expected))
                    : LocalizedMessage.Create("reason.patch_orphan", ("crc", crcs)));
            }

            return (null, true);
        }

        // An IPS whose name gives the CRC32 of two different files cannot tell which one it patches
        if (byCrc.Select(b => b.Best.Item).Distinct(ReferenceEqualityComparer.Instance).Count() > 1)
        {
            Refuse(patch.File, LocalizedMessage.Create("reason.patch_ambiguous", ("crc", string.Join(", ", byCrc.Select(b => FormatCrc(b.Crc))))));
            return (null, false);
        }

        var origin = byCrc[0].Best;
        if (origin.IsMultiFile)
        {
            Refuse(patch.File, LocalizedMessage.Create("reason.patch_multi_file", ("origin", origin.Item.DisplayName)));
            return (null, false);
        }

        return (origin, false);
    }

    /// <summary>Among the files of the same CRC32: a file of a single file, the one the sort writes, then the first by path.</summary>
    private static Candidate ChooseOrigin(List<Candidate> candidates) =>
        candidates
            .OrderBy(c => c.IsMultiFile)
            .ThenByDescending(c => SelectionFilter.IsReleaseWritten(c.Item))
            .ThenBy(c => c.Item.SourcePath, StringComparer.Ordinal)
            .ThenBy(c => c.Item.ArchiveMember, StringComparer.Ordinal)
            .ThenBy(c => c.Offset)
            .First();

    /// <summary>
    /// The other form of the data of the files the patches left over may expect: the whole file of a file identified
    /// without its copier header; the data after the copier header of a file identified whole. Read only for a size one of
    /// these patches can fit (any size for an IPS).
    /// </summary>
    private async Task<List<Candidate>> ReadOtherFormsAsync(List<Candidate> candidates, List<ReadPatch> left, CancellationToken cancellationToken)
    {
        var anySize = left.Any(p => p.Header.SourceSize is null);
        var sizes = left.Select(p => p.Header.SourceSize).OfType<long>().ToHashSet();
        bool MayFit(long size) => anySize || sizes.Contains(size);

        var others = new List<Candidate>();
        foreach (var candidate in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var item = candidate.Item;
            try
            {
                if (item.CopierHeaderSize > 0)
                {
                    if (MayFit(item.FileSize) && string.IsNullOrEmpty(item.ArchiveMember))
                    {
                        var crc = _crcCache.Get(item.SourcePath);
                        if (crc is null)
                        {
                            crc = await _checksumService.ComputeFileCrc32Async(item.SourcePath, cancellationToken: cancellationToken).ConfigureAwait(false);
                            _crcCache.Set(item.SourcePath, crc);
                        }

                        others.Add(candidate with { Offset = 0, Size = item.FileSize, Crc32 = uint.Parse(crc, NumberStyles.HexNumber, CultureInfo.InvariantCulture) });
                    }
                }
                else if (CopierHeaderSizes.Any(h => item.FileSize > h && MayFit(item.FileSize - h))
                         && await ReadWithoutHeaderAsync(item, cancellationToken).ConfigureAwait(false) is { } withoutHeader)
                {
                    others.Add(candidate with { Offset = withoutHeader.Header, Size = item.FileSize - withoutHeader.Header, Crc32 = withoutHeader.Crc });
                }
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
            {
                // A file that cannot be read again gives no other form
            }
        }

        return others;
    }

    /// <summary>Sizes of the copier headers <see cref="IChecksumService.DetectCopierHeader"/> finds: iNES, Lynx, SNES.</summary>
    private static readonly int[] CopierHeaderSizes = [16, 64, 512];

    /// <summary>The copier header of a file identified whole, and the CRC32 of its data after it; <see langword="null"/> without header.</summary>
    private async Task<(int Header, uint Crc)?> ReadWithoutHeaderAsync(ProcessItem item, CancellationToken cancellationToken)
    {
        await using Stream stream = !string.IsNullOrEmpty(item.ArchivePath) && !string.IsNullOrEmpty(item.ArchiveMember)
            ? _checksumService.OpenArchiveMember(item.ArchivePath, item.ArchiveMember)
            : new FileStream(item.SourcePath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true);
        var start = new byte[64];
        var read = await stream.ReadAtLeastAsync(start, start.Length, throwOnEndOfStream: false, cancellationToken).ConfigureAwait(false);
        var header = _checksumService.DetectCopierHeader(start.AsSpan(0, read), item.FileSize);
        if (header <= 0 || header >= item.FileSize)
        {
            return null;
        }

        var crc = new Crc32();
        if (header < read)
        {
            crc.Append(start.AsSpan(header, read - header));
        }
        else if (header > read)
        {
            await SkipAsync(stream, header - read, cancellationToken).ConfigureAwait(false);
        }

        var buffer = new byte[81920];
        int n;
        while ((n = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
        {
            crc.Append(buffer.AsSpan(0, n));
        }

        return (header, crc.GetCurrentHashAsUInt32());
    }

    private static async Task SkipAsync(Stream stream, int count, CancellationToken cancellationToken)
    {
        var buffer = new byte[Math.Min(count, 4096)];
        while (count > 0)
        {
            var n = await stream.ReadAsync(buffer.AsMemory(0, Math.Min(count, buffer.Length)), cancellationToken).ConfigureAwait(false);
            if (n == 0)
            {
                throw new EndOfStreamException();
            }

            count -= n;
        }
    }

    // =========================================================================
    // The patched version
    // =========================================================================

    private static ProcessItem CreatePatchedVersion(ReadPatch patch, Candidate origin)
    {
        var item = origin.Item;
        var version = new PatchedVersion(patch.File, patch.Header, item, origin.Offset, origin.Size, origin.Crc32);
        var originName = !string.IsNullOrEmpty(item.TargetFilename) ? item.TargetFilename : item.DisplayName;
        return new ProcessItem
        {
            SourcePath = patch.File.SourcePath,
            ArchivePath = patch.File.ArchiveMember is null ? null : patch.File.SourcePath,
            ArchiveMember = patch.File.ArchiveMember,
            FileSize = version.TargetSize,
            Parsed = item.Parsed,
            Status = FileStatus.Valid,
            ReleaseStatus = FileStatus.Valid,
            StatusReason = LocalizedMessage.Create(
                "reason.patched_from",
                ("origin", originName),
                ("patch", patch.File.DisplayName),
                ("format", patch.Header.Format.ToDisplayName())),
            TargetFilename = PatchedName(patch.File.DisplayName, originName, patch.Header.Format, origin.Crc32),
            DatName = item.DatName,
            IsMultidisk = false,
            Crc32 = patch.Header.TargetCrc32 is { } targetCrc ? FormatCrc(targetCrc) : string.Empty,
            Patch = version
        };
    }

    /// <summary>
    /// Name of a patched version (igir): the name of its patch without its extension, then the extension of the origin.
    /// The CRC32 an IPS gives in its name is removed, with the brackets or parentheses that hold only it; a name left
    /// empty keeps it.
    /// </summary>
    internal static string PatchedName(string patchName, string originName, PatchFormat format, uint originCrc)
    {
        var baseName = BaseName(patchName);
        if (format is PatchFormat.Ips or PatchFormat.Ips32)
        {
            var cleaned = baseName;
            foreach (var (_, match) in CrcTokens(baseName).Where(t => t.Crc == originCrc).Reverse())
            {
                var start = match.Index;
                var end = match.Index + match.Length;
                if (start > 0 && end < cleaned.Length && IsPair(cleaned[start - 1], cleaned[end]))
                {
                    start--;
                    end++;
                }

                cleaned = cleaned[..start] + cleaned[end..];
            }

            cleaned = string.Join(' ', cleaned.Split(' ', StringSplitOptions.RemoveEmptyEntries));
            if (cleaned.Length > 0)
            {
                baseName = cleaned;
            }
        }

        return baseName + Path.GetExtension(originName);
    }

    private static bool IsPair(char open, char close) => (open, close) is ('[', ']') or ('(', ')') or ('{', '}');

    private static string FormatCrc(uint crc) => crc.ToString("x8", CultureInfo.InvariantCulture);
}
