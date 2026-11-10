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
/// which records nothing, the file of a CRC32 its name gives (the expression of igir, on the file name only) or, when
/// its name gives none, the text that comes with it, its readme (<see cref="NotesOf"/>, <see cref="NoteCrcs"/>). A text
/// that gives the CRC32 of a file with and without its copier header leaves the patch out: an IPS does not tell which
/// form it expects.</item>
/// <item>Only a file identified in the DAT is an origin, whatever its status after the 1G1R (a translation often aims
/// at the Japanese release the 1G1R leaves out); never a CHD, a MAME set or a file of a game in several files.</item>
/// <item>The CRC32 of a file is looked for with and without its copier header: as identified first, then in its other
/// form, computed only when a patch is left without origin. The patch applies to the form it expects.</item>
/// <item>Several files of the same CRC32: the one the sort writes, then the first by path. Several patches of a file:
/// a patched version each.</item>
/// <item>The patched version is named after its origin, with a label of its patch in brackets
/// (<see cref="PatchedName"/>). It goes into the folder of the origin, just before it in the list (a move leaves the
/// source after the patched version is written).</item>
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

    /// <summary>
    /// A patch read, with the CRC32 its origin must have: one for a UPS or a BPS, those of its name or of its texts for an
    /// IPS, where they come from and, for those of its texts, the text of each.
    /// </summary>
    private sealed record ReadPatch(
        PatchFile File,
        PatchHeader Header,
        IReadOnlyList<uint> SourceCrcs,
        PatchCrcSource CrcSource,
        IReadOnlyDictionary<uint, PatchFile>? NoteOf = null)
    {
        /// <summary>The names of the texts that gave the CRC32, for a reason.</summary>
        public string NoteNames => NoteOf is null ? string.Empty : string.Join(", ", NoteOf.Values.Distinct().Select(n => n.DisplayName));
    }

    /// <summary>
    /// Reads the patches of <paramref name="folder"/>, matches them with the identified files of
    /// <paramref name="items"/>, and inserts each patched version just before its origin. Returns the number of patched
    /// versions.
    /// </summary>
    public async Task<int> MatchAsync(
        List<ProcessItem> items,
        PatchFolderContent folder,
        CancellationToken cancellationToken = default,
        IProgress<SortProgress>? progress = null)
    {
        progress?.Report(new SortProgress(0, 0, 0, 0, 0.0, LocalizedMessage.Create("progress.reading_patches")));
        var patches = folder.Patches;
        CountPatchesByFolder(patches);
        IndexNotes(folder);
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

        if (_config.Filters.DetectCopierHeaders)
        {
            await RefuseNotesOfBothFormsAsync(matched, cancellationToken).ConfigureAwait(false);
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
            return new ReadPatch(patch, header, [sourceCrc], PatchCrcSource.Recorded);
        }

        // An IPS records no checksum: its name gives the CRC32 of its origin, as igir reads it
        var crcs = CrcTokens(BaseName(patch.DisplayName)).Select(t => t.Crc).Distinct().ToList();
        if (crcs.Count > 0)
        {
            return new ReadPatch(patch, header, crcs, PatchCrcSource.FileName);
        }

        // A name without CRC32: the texts that come with the patch may give it
        var fromNotes = await ReadNoteCrcsAsync(patch, cancellationToken).ConfigureAwait(false);
        if (fromNotes.Count > 0)
        {
            return new ReadPatch(
                patch,
                header,
                [.. fromNotes.Select(f => f.Crc)],
                PatchCrcSource.Notes,
                fromNotes.ToDictionary(f => f.Crc, f => f.Note));
        }

        Refuse(patch, LocalizedMessage.Create("reason.patch_no_crc"));
        return null;
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
    // The texts that come with a patch
    // =========================================================================

    /// <summary>The texts of the patch folder by place (<see cref="PlaceOf"/>).</summary>
    private readonly Dictionary<string, List<PatchFile>> _notesByPlace = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The patches of each place (<see cref="PlaceOf"/>), every member of an archive counted.</summary>
    private readonly Dictionary<string, int> _patchesByPlace = new(StringComparer.OrdinalIgnoreCase);

    private void IndexNotes(PatchFolderContent folder)
    {
        _notesByPlace.Clear();
        _patchesByPlace.Clear();
        foreach (var patch in folder.Patches)
        {
            var place = PlaceOf(patch);
            _patchesByPlace[place] = _patchesByPlace.GetValueOrDefault(place) + 1;
        }

        foreach (var note in folder.Notes)
        {
            var place = PlaceOf(note);
            if (!_notesByPlace.TryGetValue(place, out var list))
            {
                list = [];
                _notesByPlace[place] = list;
            }

            list.Add(note);
        }
    }

    /// <summary>
    /// Where a patch or a text lies, for the texts of a patch: its folder on the disk, or its archive and its folder in
    /// that archive. A text on the disk never comes with a patch in an archive, nor the reverse.
    /// </summary>
    private static string PlaceOf(PatchFile file)
    {
        if (file.ArchiveMember is not { } member)
        {
            return "disk\0" + (Path.GetDirectoryName(file.SourcePath) ?? string.Empty);
        }

        var segments = member.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries);
        return "archive\0" + file.SourcePath + "\0" + string.Join('/', segments.Take(segments.Length - 1));
    }

    /// <summary>
    /// The texts that come with a patch: in its place (<see cref="PlaceOf"/>), those of its name without extension
    /// (<c>Game (T-Fr).txt</c> for <c>Game (T-Fr).ips</c>, case ignored); without any, every text of a place that holds
    /// no other patch; otherwise none, as a text among several patches cannot tell which one it describes.
    /// </summary>
    private IReadOnlyList<PatchFile> NotesOf(PatchFile patch)
    {
        var place = PlaceOf(patch);
        if (!_notesByPlace.TryGetValue(place, out var notes))
        {
            return [];
        }

        var baseName = BaseName(patch.DisplayName);
        var own = notes.Where(n => string.Equals(BaseName(n.DisplayName), baseName, StringComparison.OrdinalIgnoreCase)).ToList();
        if (own.Count > 0)
        {
            return own;
        }

        return _patchesByPlace.GetValueOrDefault(place) == 1 ? notes : [];
    }

    /// <summary>The CRC32 the texts of a patch give, each with the first text that gives it, in the order of the texts.</summary>
    private async Task<List<(uint Crc, PatchFile Note)>> ReadNoteCrcsAsync(PatchFile patch, CancellationToken cancellationToken)
    {
        var found = new List<(uint Crc, PatchFile Note)>();
        foreach (var note in NotesOf(patch))
        {
            string? text;
            try
            {
                text = await ReadNoteAsync(note, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
            {
                // A text that cannot be read gives nothing: the patch is not refused for it
                continue;
            }

            foreach (var crc in NoteCrcs(text ?? string.Empty))
            {
                if (!found.Exists(f => f.Crc == crc))
                {
                    found.Add((crc, note));
                }
            }
        }

        return found;
    }

    /// <summary>
    /// A text read whole, or <see langword="null"/> beyond <see cref="PatchService.MaxNoteSize"/> (the size an archive
    /// gives may be false). Latin-1 unless it starts with a byte order mark: only the ASCII of the CRC32 matters.
    /// </summary>
    private async Task<string?> ReadNoteAsync(PatchFile note, CancellationToken cancellationToken)
    {
        await using Stream stream = note.ArchiveMember is { } member
            ? _checksumService.OpenArchiveMember(note.SourcePath, member)
            : new FileStream(note.SourcePath, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, useAsync: true);
        var buffer = new byte[PatchService.MaxNoteSize + 1];
        var length = await stream.ReadAtLeastAsync(buffer, buffer.Length, throwOnEndOfStream: false, cancellationToken).ConfigureAwait(false);
        if (length > PatchService.MaxNoteSize)
        {
            return null;
        }

        using var reader = new StreamReader(new MemoryStream(buffer, 0, length), System.Text.Encoding.Latin1, detectEncodingFromByteOrderMarks: true);
        return await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// The CRC32 a text gives: on each line where the word <c>CRC</c> appears (case ignored), the tokens of 8 hexadecimal
    /// digits after it, as <see cref="CrcTokenRegex"/> reads them (<c>File/ROM CRC32: 65D0A825</c>,
    /// <c>CRC32 (no header) : 0x65d0a825</c>). A date or a SHA-1 elsewhere is never taken for a CRC32. A CRC32 of no file of
    /// the source, like that of the patched result, is ignored when matching.
    /// </summary>
    internal static IEnumerable<uint> NoteCrcs(string text)
    {
        foreach (var line in text.Split('\n'))
        {
            var at = line.IndexOf("CRC", StringComparison.OrdinalIgnoreCase);
            if (at < 0)
            {
                continue;
            }

            foreach (var (crc, _) in CrcTokens(line[(at + 3)..]))
            {
                yield return crc;
            }
        }
    }

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
                    : patch.CrcSource == PatchCrcSource.Notes
                        ? LocalizedMessage.Create("reason.patch_orphan_note", ("note", patch.NoteNames), ("crc", crcs))
                        : LocalizedMessage.Create("reason.patch_orphan", ("crc", crcs)));
            }

            return (null, true);
        }

        // An IPS whose name or texts give the CRC32 of two different files cannot tell which one it patches
        if (byCrc.Select(b => b.Best.Item).Distinct(ReferenceEqualityComparer.Instance).Count() > 1)
        {
            var crcs = string.Join(", ", byCrc.Select(b => FormatCrc(b.Crc)));
            Refuse(patch.File, patch.CrcSource == PatchCrcSource.Notes
                ? LocalizedMessage.Create("reason.patch_ambiguous_note", ("note", patch.NoteNames), ("crc", crcs))
                : LocalizedMessage.Create("reason.patch_ambiguous", ("crc", crcs)));
            return (null, false);
        }

        // Nor one whose texts give a file with and without its copier header (the name of a patch is the choice of the user)
        if (patch.CrcSource == PatchCrcSource.Notes && byCrc.Select(b => b.Best.Offset).Distinct().Count() > 1)
        {
            RefuseBothForms(patch, byCrc[0].Best.Item);
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

    private void RefuseBothForms(ReadPatch patch, ProcessItem origin) =>
        Refuse(patch.File, LocalizedMessage.Create(
            "reason.patch_header_form_ambiguous",
            ("note", patch.NoteNames),
            ("origin", origin.DisplayName)));

    /// <summary>
    /// The patches matched by the CRC32 of their texts whose other CRC32 is that of the other form of their origin (with or
    /// without its copier header): the block of a readme often gives both, and an IPS does not tell which one it expects.
    /// They are refused, and taken out of <paramref name="matched"/>. Only the other form of these origins is read.
    /// </summary>
    private async Task RefuseNotesOfBothFormsAsync(List<(ReadPatch Patch, Candidate Origin)> matched, CancellationToken cancellationToken)
    {
        for (var i = matched.Count - 1; i >= 0; i--)
        {
            var (patch, origin) = matched[i];
            if (patch.CrcSource != PatchCrcSource.Notes || patch.SourceCrcs.All(crc => crc == origin.Crc32))
            {
                continue;
            }

            cancellationToken.ThrowIfCancellationRequested();
            if (await OtherFormAsync(origin, _ => true, cancellationToken).ConfigureAwait(false) is { } other
                && patch.SourceCrcs.Contains(other.Crc32))
            {
                RefuseBothForms(patch, origin.Item);
                matched.RemoveAt(i);
            }
        }
    }

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
            if (await OtherFormAsync(candidate, MayFit, cancellationToken).ConfigureAwait(false) is { } other)
            {
                others.Add(other);
            }
        }

        return others;
    }

    /// <summary>
    /// The other form of the data of a file (<see cref="ReadOtherFormsAsync"/>), when <paramref name="mayFit"/> accepts its
    /// size; <see langword="null"/> without one, or for a file that cannot be read again.
    /// </summary>
    private async Task<Candidate?> OtherFormAsync(Candidate candidate, Func<long, bool> mayFit, CancellationToken cancellationToken)
    {
        var item = candidate.Item;
        try
        {
            if (item.CopierHeaderSize > 0)
            {
                if (mayFit(item.FileSize) && string.IsNullOrEmpty(item.ArchiveMember))
                {
                    var crc = _crcCache.Get(item.SourcePath);
                    if (crc is null)
                    {
                        crc = await _checksumService.ComputeFileCrc32Async(item.SourcePath, cancellationToken: cancellationToken).ConfigureAwait(false);
                        _crcCache.Set(item.SourcePath, crc);
                    }

                    return candidate with { Offset = 0, Size = item.FileSize, Crc32 = uint.Parse(crc, NumberStyles.HexNumber, CultureInfo.InvariantCulture) };
                }
            }
            else if (CopierHeaderSizes.Any(h => item.FileSize > h && mayFit(item.FileSize - h))
                     && await ReadWithoutHeaderAsync(item, cancellationToken).ConfigureAwait(false) is { } withoutHeader)
            {
                return candidate with { Offset = withoutHeader.Header, Size = item.FileSize - withoutHeader.Header, Crc32 = withoutHeader.Crc };
            }
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            // A file that cannot be read again gives no other form
        }

        return null;
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

    private ProcessItem CreatePatchedVersion(ReadPatch patch, Candidate origin)
    {
        var item = origin.Item;
        var version = new PatchedVersion(patch.File, patch.Header, item, origin.Offset, origin.Size, origin.Crc32)
        {
            CrcSource = patch.CrcSource,
            Note = patch.NoteOf?.GetValueOrDefault(origin.Crc32)
        };
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
            TargetFilename = PatchedName(patch.File, originName, origin.Crc32, FolderLabel(patch.File), item.Parsed.Convention),
            DatName = item.DatName,
            IsMultidisk = false,
            Crc32 = patch.Header.TargetCrc32 is { } targetCrc ? FormatCrc(targetCrc) : string.Empty,
            Patch = version
        };
    }

    /// <summary>Label of a patched version that no name of its patch gives one.</summary>
    internal const string DefaultPatchLabel = "patched";

    /// <summary>
    /// Names that tell nothing of a patch once its origin is removed: a label made of one of them gives way to a later
    /// source, and is kept only when none gives a better one.
    /// </summary>
    private static readonly HashSet<string> GenericLabels = new(StringComparer.OrdinalIgnoreCase)
    {
        "patch", "patched", "hack", "fix", "translation", "rom", "game", "english", "eng", "update"
    };

    /// <summary>A version alone, as a label: <c>1.0</c>, <c>v2</c>, <c>v1.2.3</c>.</summary>
    [GeneratedRegex(@"^v?\d+(?:\.\d+)*$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex VersionOnlyRegex();

    /// <summary>Brackets, parentheses or braces left empty once a CRC32 is removed.</summary>
    [GeneratedRegex(@"\(\s*\)|\[\s*\]|\{\s*\}", RegexOptions.CultureInvariant)]
    private static partial Regex EmptyPairRegex();

    /// <summary>Characters trimmed from both ends of a label: spaces and the separators that joined it to the title.</summary>
    private static readonly char[] LabelTrim = [' ', '-', '_', '.', ',', '+', '~', ';', ':'];

    /// <summary>Patches of each folder of the patch folder: a file, or an archive whatever its members.</summary>
    private readonly Dictionary<string, int> _patchesByFolder = new(StringComparer.OrdinalIgnoreCase);

    private void CountPatchesByFolder(IReadOnlyList<PatchFile> patches)
    {
        _patchesByFolder.Clear();
        foreach (var path in patches.Select(p => p.SourcePath).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var folder = Path.GetDirectoryName(path) ?? string.Empty;
            _patchesByFolder[folder] = _patchesByFolder.GetValueOrDefault(folder) + 1;
        }
    }

    /// <summary>
    /// The folder of a patch as a label: the name of its folder in the patch folder, only when that folder holds no other
    /// patch (a folder of several patches sorts them, by system or by kind, and tells nothing of each one).
    /// <see langword="null"/> for a patch at the root of the patch folder.
    /// </summary>
    private string? FolderLabel(PatchFile patch)
    {
        var folder = Path.GetDirectoryName(patch.SourcePath);
        if (string.IsNullOrEmpty(folder) || string.IsNullOrEmpty(_config.PatchDir) || _patchesByFolder.GetValueOrDefault(folder) != 1)
        {
            return null;
        }

        var relative = Path.GetRelativePath(Path.GetFullPath(_config.PatchDir), Path.GetFullPath(folder));
        return relative == "." || relative.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(relative)
            ? null
            : Path.GetFileName(relative.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
    }

    /// <summary>
    /// Name of a patched version: the name of its origin, then a label of its patch in brackets, then the extension of
    /// the origin (<c>Game (Japan) [T-En].sfc</c>), in the folder the name of the origin may hold. The label comes from
    /// the first of these names: the patch, its folder in its archive, its archive, its folder in the patch folder
    /// (<paramref name="folderLabel"/>). From each, the CRC32 of the origin is removed, then the name of the origin or
    /// its title the name starts with, then the brackets and parentheses. A generic label (<see cref="GenericLabels"/>,
    /// a version alone) gives way to the next name, and is kept when none gives a better one; without any label,
    /// <see cref="DefaultPatchLabel"/>. The name never is that of its origin. TOSEC writes its flags without a space
    /// before them (<c>Game (1990)(Ocean)[h Hack].adf</c>), the other conventions with one.
    /// </summary>
    internal static string PatchedName(PatchFile patch, string originName, uint originCrc, string? folderLabel, NamingConvention convention)
    {
        var originFile = Path.GetFileName(originName);
        var originBase = Path.GetFileNameWithoutExtension(originFile);
        var originTitle = TitleOf(originBase);
        string? chosen = null;
        foreach (var source in LabelSources(patch, folderLabel))
        {
            var label = Label(source, originBase, originTitle, originCrc);
            if (label.Length == 0)
            {
                continue;
            }

            if (!IsGeneric(label))
            {
                chosen = label;
                break;
            }

            // A generic label is kept only when no later name gives a better one
            chosen ??= label;
        }

        var separator = convention == NamingConvention.Tosec ? string.Empty : " ";
        return $"{originName[..^originFile.Length]}{originBase}{separator}[{chosen ?? DefaultPatchLabel}]{Path.GetExtension(originFile)}";
    }

    /// <summary>The names a label is looked for in, in order.</summary>
    private static IEnumerable<string> LabelSources(PatchFile patch, string? folderLabel)
    {
        yield return BaseName(patch.DisplayName);
        if (patch.ArchiveMember is { } member)
        {
            var segments = member.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries);
            if (segments.Length > 1)
            {
                yield return segments[^2];
            }

            yield return Path.GetFileNameWithoutExtension(patch.SourcePath);
        }

        if (!string.IsNullOrEmpty(folderLabel))
        {
            yield return folderLabel;
        }
    }

    /// <summary>The title of a name: what comes before its first parenthesis or bracket.</summary>
    private static string TitleOf(string name)
    {
        var end = name.IndexOfAny(['(', '[']);
        return (end < 0 ? name : name[..end]).Trim();
    }

    /// <summary>The label a name gives, or an empty string (see <see cref="PatchedName"/>).</summary>
    private static string Label(string name, string originBase, string originTitle, uint originCrc)
    {
        var text = CrcTokenRegex().Replace(name, m =>
            uint.Parse(m.Groups[1].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture) == originCrc ? " " : m.Value);
        text = CollapseSpaces(EmptyPairRegex().Replace(text, " "));
        text = WithoutPrefix(text, originBase) ?? WithoutPrefix(text, originTitle) ?? text;
        text = Tidy(text.Replace('(', ' ').Replace(')', ' ').Replace('[', ' ').Replace(']', ' ').Replace('{', ' ').Replace('}', ' '));
        return text.Any(char.IsLetterOrDigit) ? text : string.Empty;
    }

    /// <summary>
    /// What follows <paramref name="prefix"/> at the start of <paramref name="text"/>, case and underscores ignored, when
    /// it ends there a word; otherwise <see langword="null"/>.
    /// </summary>
    private static string? WithoutPrefix(string text, string prefix)
    {
        if (prefix.Length == 0
            || !text.Replace('_', ' ').StartsWith(prefix.Replace('_', ' '), StringComparison.OrdinalIgnoreCase)
            || (text.Length > prefix.Length && char.IsLetterOrDigit(text[prefix.Length])))
        {
            return null;
        }

        return text[prefix.Length..];
    }

    private static string CollapseSpaces(string text) => string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    private static string Tidy(string text) => CollapseSpaces(text).Trim(LabelTrim);

    private static bool IsGeneric(string label) => GenericLabels.Contains(label) || VersionOnlyRegex().IsMatch(label);

    private static string FormatCrc(uint crc) => crc.ToString("x8", CultureInfo.InvariantCulture);
}
