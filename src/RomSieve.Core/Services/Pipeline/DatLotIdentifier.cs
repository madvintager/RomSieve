using System.IO;
using RomSieve.Core.Abstractions;
using RomSieve.Core.Models;

namespace RomSieve.Core.Services;

/// <summary>
/// Identification of the files of the source against a lot of DATs (<see cref="DatCatalog.Dats"/>): one sort per DAT
/// (its convention, its filters, its 1G1R), from a single scan and a single computation of the fingerprints.
/// <list type="number">
/// <item>The fingerprints are computed once, against the lot (a CRC32 only for a size that one of its DATs knows).</item>
/// <item>Each file is sorted with the first DAT of the lot that knows it, a descriptor and its tracks with the DAT of the
/// first file of the set it knows (<see cref="ProcessItem.DatName"/>); the other DATs that know it are kept
/// (<see cref="ProcessItem.OtherDats"/>) and named in the log, and their ROMs it matches are present too.</item>
/// <item>The classifier of each DAT (its catalog, its convention, its matched entries) classifies its files from the
/// fingerprints already computed; a file no DAT knows is unmatched.</item>
/// <item>The items come back in the order of the scan, on which the collisions depend.</item>
/// </list>
/// The DATs are only reached through <see cref="DatCatalog.FindMatchesByDat"/> (which DATs know a file) and
/// <see cref="DatCatalog.DatFor"/> (the catalog of a DAT), except where every DAT is needed: the classifiers and the
/// statistics (<see cref="Classifiers"/>).
/// </summary>
public sealed class DatLotIdentifier
{
    private readonly SortConfig _config;
    private readonly DatCatalog _lot;
    private readonly ICrcCacheService _crcCache;
    private readonly Action<LogLevel, LocalizedMessage> _log;
    private readonly EntryClassifier _lotClassifier;
    private readonly Dictionary<string, EntryClassifier> _classifiers = new(StringComparer.OrdinalIgnoreCase);

    public DatLotIdentifier(
        SortConfig config,
        DatCatalog lot,
        ICrcCacheService crcCache,
        IChecksumService checksumService,
        INoIntroRedumpParser romParser,
        IRomFilterService filterService,
        ILocalizationService i18n,
        IOpticalCompanionService opticalService,
        Action<LogLevel, LocalizedMessage> log)
    {
        if (lot.Dats is not { } dats)
        {
            throw new ArgumentException("The catalog is not a lot of DATs.", nameof(lot));
        }

        _config = config;
        _lot = lot;
        _crcCache = crcCache;
        _log = log;

        // The catalog of the lot knows the sizes and checksums of every DAT, which is all the fingerprints need
        _lotClassifier = new EntryClassifier(config, lot, crcCache, checksumService, romParser, filterService, i18n, opticalService);
        foreach (var dat in dats)
        {
            _classifiers[dat.Name] = new EntryClassifier(config, dat.Catalog, crcCache, checksumService, romParser, filterService, i18n, opticalService);
        }
    }

    /// <summary>The DATs of the lot, in its order, each with the classifier of its files (matched entries, games found).</summary>
    public IReadOnlyList<(DatLotMember Dat, EntryClassifier Classifier)> Classifiers =>
        _lot.Dats!.Select(dat => (dat, _classifiers[dat.Name])).ToList();

    /// <summary>Classifier of the DAT named <paramref name="name"/>; <c>null</c> when the lot has no such DAT.</summary>
    public EntryClassifier? ClassifierFor(string? name) =>
        name is not null && _classifiers.TryGetValue(name.Trim(), out var classifier) ? classifier : null;

    /// <summary>Items of the scanned entries, in the order of the scan, each sorted with its DAT of the lot.</summary>
    public async Task<List<ProcessItem>> IdentifyAllAsync(
        IReadOnlyList<ScanEntry> scanEntries,
        CancellationToken cancellationToken = default,
        IProgress<SortProgress>? progress = null,
        long totalBytes = 0)
    {
        WarnAboutForcedConvention();

        if (totalBytes <= 0 && scanEntries.Count > 0)
        {
            totalBytes = scanEntries.Sum(e => e.FileSize + e.RelatedEntries.Sum(r => r.FileSize));
        }

        var degree = EntryClassifier.ResolveHashingParallelism(_config.HashingParallelism);
        var fingerprints = await _lotClassifier.ComputeFingerprintsAsync(scanEntries, degree, totalBytes, cancellationToken, progress).ConfigureAwait(false);

        var items = new List<ProcessItem>(scanEntries.Count);
        var itemsByDat = new Dictionary<string, List<ProcessItem>>(StringComparer.OrdinalIgnoreCase);
        var tagsDats = _lot.Dats!.Count >= 2;
        for (var index = 0; index < scanEntries.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var item = Identify(scanEntries[index], fingerprints[index], tagsDats);
            items.Add(item);
            if (item.DatName is { } datName)
            {
                if (!itemsByDat.TryGetValue(datName, out var datItems))
                {
                    datItems = [];
                    itemsByDat[datName] = datItems;
                }

                datItems.Add(item);
            }
        }

        // The files of a game the scan did not link are gathered DAT by DAT: a game is only a game of its DAT
        var kept = new HashSet<ProcessItem>(ReferenceEqualityComparer.Instance);
        foreach (var (datName, datItems) in itemsByDat)
        {
            _classifiers[datName].GroupUnlinkedDatCompanionItems(datItems);
            kept.UnionWith(datItems);
        }

        items.RemoveAll(it => it.DatName is not null && !kept.Contains(it));

        progress?.Report(new SortProgress(0, 0, 0, 0, 0.0, LocalizedMessage.Create("progress.saving_cache")));
        await _crcCache.SaveAsync(cancellationToken).ConfigureAwait(false);
        return items;
    }

    /// <summary>
    /// Item of a scanned entry and of its companion files: classified by the DAT of the first file of the set that a DAT
    /// knows, the ROMs of the other DATs that know a file of the set marked present.
    /// </summary>
    private ProcessItem Identify(ScanEntry entry, EntryClassifier.EntryFingerprints fingerprints, bool tagsDats)
    {
        var files = new List<(ScanEntry Entry, EntryClassifier.Fingerprint Fingerprint)>(entry.RelatedEntries.Count + 1)
        {
            (entry, fingerprints.Main)
        };
        for (var i = 0; i < entry.RelatedEntries.Count; i++)
        {
            files.Add((entry.RelatedEntries[i], fingerprints.Related[i]));
        }

        var knownBy = files.Select(file => KnownBy(file.Entry, file.Fingerprint)).ToList();
        var dat = knownBy.Select(known => known.Verified.FirstOrDefault() ?? known.Unverified).FirstOrDefault(name => name is not null)
                  ?? files.Select(file => DatNamingChd(file.Entry, file.Fingerprint)).FirstOrDefault(name => name is not null);

        var classifier = dat is null ? _lotClassifier : _classifiers[dat];
        var item = classifier.Classify(entry, fingerprints.Main);
        for (var i = 0; i < entry.RelatedEntries.Count; i++)
        {
            EntryClassifier.AddRelatedItem(item, classifier.Classify(entry.RelatedEntries[i], fingerprints.Related[i]));
        }

        if (dat is null)
        {
            return item;
        }

        // The ROMs of the other DATs that know a file of the set are present: the file is in the source
        var others = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < files.Count; i++)
        {
            foreach (var other in knownBy[i].Verified.Where(name => !string.Equals(name, dat, StringComparison.OrdinalIgnoreCase)))
            {
                _classifiers[other].MarkPresent(files[i].Entry, files[i].Fingerprint);
                others.Add(other);
            }
        }

        var otherDats = _lot.Dats!.Select(d => d.Name).Where(others.Contains).ToList();
        AssignDat(item, dat, otherDats, tagsDats);

        if (otherDats.Count > 0)
        {
            _log(LogLevel.Info, LocalizedMessage.Create(
                "log.dat_lot_other_dats",
                ("name", item.DisplayName),
                ("dat", dat),
                ("others", string.Join(", ", otherDats))));
        }

        return item;
    }

    /// <summary>
    /// Sorts an item and its companion files with the DAT <paramref name="dat"/> of a lot: its name, the other DATs that
    /// know it, and, when the lot has several DATs (<paramref name="tagsDats"/>), the DAT in its game key, the same title
    /// in two DATs making two games (<see cref="ParsedRomName.DatKey"/>).
    /// </summary>
    internal static void AssignDat(ProcessItem item, string dat, IReadOnlyList<string> otherDats, bool tagsDats)
    {
        foreach (var it in item.RelatedItems.Prepend(item))
        {
            it.DatName = dat;
            it.OtherDats = otherDats;
            if (tagsDats && it.Parsed.IsConform)
            {
                it.Parsed = it.Parsed with { DatKey = dat };
            }
        }
    }

    /// <summary>
    /// The DATs of the lot that know a file, in the order of the lot: those whose entries it matches
    /// (<see cref="EntryClassifier.Knows"/>, with the SHA-1 check), and the first one that matches it by size and CRC32
    /// alone, whose SHA-1 differs (<paramref name="Unverified"/>: the file is then unmatched with this DAT's reason).
    /// </summary>
    private (List<string> Verified, string? Unverified) KnownBy(ScanEntry entry, EntryClassifier.Fingerprint fingerprint)
    {
        var verified = new List<string>();
        string? unverified = null;
        if (fingerprint.ArchiveDamage is not null || fingerprint.ChdError is not null)
        {
            return (verified, unverified);
        }

        var byDat = !string.IsNullOrEmpty(fingerprint.Crc)
            ? _lot.FindMatchesByDat(fingerprint.EffectiveSize(entry), fingerprint.Crc)
            : [];

        // A CHD matches the disks of a DAT by the SHA-1 of its header
        if (byDat.Count == 0 && fingerprint.Chd is { } chd)
        {
            byDat = _lot.FindMatchesByDat(0, sha1: chd.Sha1).Where(found => found.Matches.Any(m => m.IsDisk)).ToList();
        }

        foreach (var (dat, _) in byDat)
        {
            if (_classifiers[dat.Name].Knows(entry, fingerprint))
            {
                verified.Add(dat.Name);
            }
            else
            {
                unverified ??= dat.Name;
            }
        }

        return (verified, unverified);
    }

    /// <summary>
    /// With <see cref="SortConfig.MatchChdByName"/>, the first DAT of the lot with a game named as a CHD no DAT knows by
    /// its checksums; <c>null</c> otherwise.
    /// </summary>
    private string? DatNamingChd(ScanEntry entry, EntryClassifier.Fingerprint fingerprint)
    {
        if (!_config.MatchChdByName || fingerprint.Chd is null)
        {
            return null;
        }

        var gameName = Path.GetFileNameWithoutExtension(string.IsNullOrEmpty(entry.ArchiveMember) ? entry.SourcePath : entry.ArchiveMember);
        return _lot.Dats!.FirstOrDefault(dat => dat.Catalog.GameByName.ContainsKey(gameName))?.Name;
    }

    /// <summary>
    /// The convention chosen by hand only applies to the DATs whose convention is not recognized: those whose detected
    /// convention differs from it are named once.
    /// </summary>
    private void WarnAboutForcedConvention()
    {
        var chosen = _config.NamingConvention;
        if (chosen == NamingConvention.Auto)
        {
            return;
        }

        var overriding = Classifiers
            .Where(pair => pair.Classifier.DatConvention is var detected && detected != NamingConvention.Auto && detected != chosen)
            .Select(pair => pair.Dat.Name)
            .ToList();
        if (overriding.Count > 0)
        {
            _log(LogLevel.Warn, LocalizedMessage.Create(
                "log.dat_lot_convention_overridden",
                ("convention", ConventionName(chosen)),
                ("count", overriding.Count),
                ("dats", string.Join(", ", overriding.Take(5)) + (overriding.Count > 5 ? ", …" : string.Empty))));
        }
    }

    /// <summary>Name of a convention, as the interface shows it.</summary>
    internal static LocalizedMessage ConventionName(NamingConvention convention) => convention switch
    {
        NamingConvention.Tosec => LocalizedMessage.Create("tab1.badge_tosec"),
        NamingConvention.NoIntro => LocalizedMessage.Create("tab1.badge_nointro"),
        NamingConvention.Redump => LocalizedMessage.Create("tab1.badge_redump"),
        NamingConvention.Mame => LocalizedMessage.Create("tab1.badge_mame"),
        NamingConvention.MameSoftwareList => LocalizedMessage.Create("tab1.badge_softlist"),
        _ => LocalizedMessage.Create("tab1.badge_auto")
    };
}
