using System.Globalization;
using System.Text.RegularExpressions;
using CommunityToolkit.Mvvm.ComponentModel;
using RomSieve.Core.Abstractions;
using RomSieve.Core.Models;
using RomSieve.Core.Services;

namespace RomSieve.App.ViewModels;

/// <summary>
/// ViewModel for a row in the 11-column simulation preview table
/// (<c>Original Name</c>, <c>CRC32</c>, <c>Title</c>, <c>Media</c>, <c>Publisher</c>,
/// <c>Date</c>, <c>Languages</c>, <c>Country/Region</c>, <c>Calculated Target Path</c>, <c>Status</c>,
/// <c>Related</c>).
/// </summary>
public sealed partial class PreviewItemViewModel : PreviewRowViewModel
{
    [GeneratedRegex(@"\b(\d+)\s+of\s+(\d+)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex DiskOfTotalRegex();

    [GeneratedRegex(@"^(?:Disk|Disc|Tape|Part)\s+(\d+)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex DiskNumberOnlyRegex();

    private readonly ILocalizationService _localization;
    private readonly IRomFilterService _filterService;
    private string? _destinationRootDir;

    public PreviewItemViewModel(
        ProcessItem item,
        ILocalizationService localization,
        IRomFilterService filterService,
        string? destinationRootDir = null)
    {
        Item = item;
        _localization = localization;
        _filterService = filterService;
        _destinationRootDir = destinationRootDir;

        _statusDisplay = string.Empty;
        _statusReasonText = string.Empty;
        _sourcePathDisplay = string.Empty;
        _fullTargetPath = string.Empty;

        RefreshFromItem();
    }

    /// <summary>
    /// Item shown by the row. A differential simulation binds the same row to the new item of the same file
    /// (<see cref="Rebind"/>), so that the table keeps its rows, its selection and its scroll position.
    /// </summary>
    public ProcessItem Item { get; private set; }

    public override bool IsGroupHeader => false;

    private LocalizedMessage? _changeMessage;

    /// <summary>Translated description of <see cref="Change"/> (previous status and target), empty without change.</summary>
    [ObservableProperty]
    private string _changeDescription = string.Empty;

    private LocalizedMessage? _selectionMessage;

    /// <summary>Tooltip of the row without the line of the selection.</summary>
    private string _itemTooltip = string.Empty;

    /// <summary>
    /// Translated place of the row in a partial sort (checked, unchecked, or sorted with the sets that need it), empty
    /// while every game is checked. Shown apart from the status, which never depends on the selection.
    /// </summary>
    [ObservableProperty]
    private string _selectionText = string.Empty;

    /// <summary>Records the place of the row in a partial sort (<see cref="SelectionText"/>), <see langword="null"/> for none.</summary>
    public void SetSelection(LocalizedMessage? message)
    {
        _selectionMessage = message;
        var text = _localization.Format(message);
        if (text != SelectionText)
        {
            SelectionText = text;
            UpdateTooltip();
        }
    }

    /// <summary>Tooltip of the row: the file, then the line of the selection while some games are unchecked.</summary>
    private void UpdateTooltip() =>
        TooltipText = string.IsNullOrEmpty(SelectionText)
            ? _itemTooltip
            : _itemTooltip + Environment.NewLine + _localization.Tr("preview.tooltip_selection", ("detail", SelectionText));

    /// <summary>Shows <paramref name="item"/>, the new result for the file of this row.</summary>
    public void Rebind(ProcessItem item, string? destinationRootDir)
    {
        Item = item;
        RefreshFromItem(destinationRootDir);
    }

    /// <summary>
    /// Records how the row changed since the previous simulation: <paramref name="previous"/> holds the status, the
    /// target and the reason it had then (ignored for a new or unchanged row).
    /// </summary>
    public void SetChange(PreviewRowChange change, PreviewRowState? previous = null)
    {
        _changeMessage = change switch
        {
            PreviewRowChange.Added => LocalizedMessage.Create("preview.change_added"),

            // Same status and target: only the reason changed (another winner, another rule), which says why
            PreviewRowChange.Modified when previous is { } before
                && before.Status == Item.Status
                && string.Equals(before.Target, Item.FinalTargetPath ?? Item.TargetPath, StringComparison.Ordinal)
                => before.Reason is { } reason
                    ? LocalizedMessage.Create("preview.change_reason", ("reason", reason))
                    : LocalizedMessage.Create("preview.change_reason_added"),
            PreviewRowChange.Modified when previous is { } before => LocalizedMessage.Create(
                "preview.change_modified",
                ("status", LocalizedMessage.Create(GetStatusLabelKey(before.Status))),
                ("target", FormatTarget(before.Target))),
            _ => null
        };
        Change = change;
        RefreshFromItem();
    }

    private string? _searchTitle;
    private string? _searchSource;
    private string? _searchCategory;
    private (ParsedRomName Parsed, string Key)? _canonicalGameKey;

    /// <summary>Title normalized for the search (lower case, without accents), computed once per refresh.</summary>
    public string SearchTitle => _searchTitle ??= PreviewTableViewModel.NormalizeSearchText(TitleDisplay);

    /// <summary>Source name normalized for the search (lower case, without accents), computed once per refresh.</summary>
    public string SearchSource => _searchSource ??= PreviewTableViewModel.NormalizeSearchText(SourceName);

    /// <summary>Category of a MAME machine, normalized for the search (<c>shooter / flying vertical</c>); empty otherwise.</summary>
    public string SearchCategory => _searchCategory ??= PreviewTableViewModel.NormalizeSearchText(Item.Parsed.Machine?.Category?.Name ?? string.Empty);

    /// <summary>
    /// Canonical game key of the row, computed once for each parse result of the item
    /// (the pipeline replaces <see cref="ProcessItem.Parsed"/> instead of modifying it).
    /// </summary>
    public string CanonicalGameKey
    {
        get
        {
            var parsed = Item.Parsed;
            if (_canonicalGameKey is { } cached && ReferenceEquals(cached.Parsed, parsed))
            {
                return cached.Key;
            }

            var key = _filterService.SerializeCanonicalGameKey(parsed);
            _canonicalGameKey = (parsed, key);
            return key;
        }
    }

    /// <summary>
    /// Unit of a partial sort the row belongs to (<see cref="SelectionFilter.GetUnitKey"/>): its game, or the file
    /// itself when it is non-conforming (<see cref="SelectionFilter.BelongsToGameUnit"/>). Stable across simulations of
    /// the same source.
    /// </summary>
    public string SelectionUnitKey =>
        SelectionFilter.BelongsToGameUnit(Item) ? SelectionFilter.GetGameUnitKey(CanonicalGameKey) : SelectionFilter.GetFileUnitKey(Item);

    /// <summary>
    /// Localized status with its reason (<c>Excluded (reason)</c>): the sort key of the status column. The column shows
    /// <see cref="StatusLabel"/> as a badge, the reason being in the tooltip and the detail panel.
    /// </summary>
    [ObservableProperty]
    private string _statusDisplay;

    /// <summary>Reason of the status alone (filter, 1G1R decision, identification by name), empty without reason.</summary>
    [ObservableProperty]
    private string _statusReasonText;

    /// <summary>Full path of the source file, followed by the member name for a file read from an archive.</summary>
    [ObservableProperty]
    private string _sourcePathDisplay;

    /// <summary>Full target path (the table shows it relative to the destination).</summary>
    [ObservableProperty]
    private string _fullTargetPath;

    /// <summary>
    /// Name of the checksum shown by the detail panel (<c>SHA-1</c> for a CHD, identified by it, <c>CRC32</c>
    /// otherwise), empty when the row has none: a MAME set is a whole archive, without checksum of its own.
    /// </summary>
    [ObservableProperty]
    private string _detailChecksumLabel = string.Empty;

    /// <summary>Checksum shown by the detail panel next to <see cref="DetailChecksumLabel"/>, empty without one.</summary>
    [ObservableProperty]
    private string _detailChecksumText = string.Empty;

    /// <summary>
    /// What the MAME set of the row holds compared with its machine (complete or what it lacks, the sets it needs, the
    /// clones it holds), for the detail panel; empty for any other row.
    /// </summary>
    [ObservableProperty]
    private string _mameSetText = string.Empty;

    /// <summary>Version, media and tracks of the CHD of the row, for the detail panel; empty for any other row.</summary>
    [ObservableProperty]
    private string _detailChdText = string.Empty;

    /// <summary>Sort key for the Disc column: <c>(DiscNum, DiscTotal, RawLabel)</c>.</summary>
    public (int DiscNum, int DiscTotal, string RawLabel) DiskSortKey { get; private set; }

    /// <summary>Number of files transferred with the file of the row: the sort key of the column of related files.</summary>
    public int RelatedCount => Item.RelatedItems.Count;

    private IReadOnlyList<PreviewRelatedFile>? _relatedFiles;

    /// <summary>
    /// Files transferred with the file of the row, for the detail panel; built when first read, since only the selected
    /// row shows them.
    /// </summary>
    public IReadOnlyList<PreviewRelatedFile> RelatedFiles => _relatedFiles ??= BuildRelatedFiles();

    /// <summary>Normalized status code (<c>"valid"</c>, <c>"excluded"</c>, <c>"unmatched"</c>, <c>"duplicate"</c>, <c>"error"</c>).</summary>
    public string StatusValue => Item.Status.ToValueString();

    /// <summary>
    /// Indexed access to the 11 columns of the row (0..10).
    /// </summary>
    [System.Runtime.CompilerServices.IndexerName("ColumnValue")]
    public string this[int columnIndex] => columnIndex switch
    {
        0 => SourceName,
        1 => Crc32Text,
        2 => TitleDisplay,
        3 => DiskDisplay,
        4 => Publisher,
        5 => Date,
        6 => LanguagesDisplay,
        7 => CountriesDisplay,
        8 => TargetPathDisplay,
        9 => StatusDisplay,
        10 => RelatedCountText,
        _ => string.Empty
    };

    /// <summary>
    /// Updates all observable properties of the row from <see cref="Item"/>.
    /// </summary>
    public void RefreshFromItem(string? destinationRootDir = null)
    {
        if (destinationRootDir is not null)
        {
            _destinationRootDir = destinationRootDir;
        }

        var parsed = Item.Parsed;
        _searchTitle = null;
        _searchSource = null;
        _searchCategory = null;

        SourceName = !string.IsNullOrEmpty(Item.ArchiveMember)
            ? Item.ArchiveMember
            : Path.GetFileName(Item.SourcePath);

        Crc32Text = !string.IsNullOrEmpty(Item.Crc32) ? Item.Crc32.ToUpperInvariant() : "-";
        (DetailChecksumLabel, DetailChecksumText) = GetDetailChecksum(Item);
        MameSetText = Item.MameSet is { } mameSet ? FormatMameSet(mameSet, parsed.Machine, _localization) : string.Empty;
        DetailChdText = Item.Chd is { } chd ? FormatChd(chd, _localization) : string.Empty;

        TitleDisplay = parsed.Title ?? string.Empty;
        IsTitleBold = Item.Is1G1RWinner;
        IsForcedWinner = IsManualWinner(Item);
        IsRequiredSet = IsRequiredMameSet(Item);

        var (diskText, sortKey) = FormatItemDisk(parsed, _localization);
        DiskDisplay = diskText;
        DiskSortKey = sortKey;

        Publisher = parsed.Publisher ?? string.Empty;
        Date = parsed.Date ?? string.Empty;
        LanguagesDisplay = FormatItemLanguages(parsed);
        CountriesDisplay = FormatItemCountries(parsed);

        TargetPathDisplay = FormatTarget(Item.FinalTargetPath ?? Item.TargetPath);

        RelatedCountText = Item.RelatedItems.Count > 0 ? Item.RelatedItems.Count.ToString(CultureInfo.InvariantCulture) : string.Empty;
        RelatedFilesBadge = FormatRelatedFilesBadge(Item, _localization);
        _relatedFiles = null;
        OnPropertyChanged(nameof(RelatedFiles));

        var statusLabel = GetStatusLabel(Item.Status, _localization);
        var reason = _localization.Format(Item.StatusReason);
        if (Item.IsIdentifiedByName)
        {
            var byName = _localization.Tr("preview.identified_by_name");
            reason = string.IsNullOrEmpty(reason) ? byName : $"{reason}, {byName}";
        }
        StatusDisplay = !string.IsNullOrEmpty(reason)
            ? $"{statusLabel} ({reason})"
            : statusLabel;
        StatusLabel = statusLabel;
        StatusReasonText = reason;
        SourcePathDisplay = !string.IsNullOrEmpty(Item.ArchiveMember)
            ? $"{Item.SourcePath} › {Item.ArchiveMember}"
            : Item.SourcePath;
        FullTargetPath = Item.FinalTargetPath ?? Item.TargetPath ?? string.Empty;

        Status = Item.Status;
        ChangeDescription = _localization.Format(_changeMessage);
        var tooltip = BuildItemTooltip(Item, _localization);
        _itemTooltip = string.IsNullOrEmpty(ChangeDescription)
            ? tooltip
            : tooltip + Environment.NewLine + ChangeDescription;
        SelectionText = _localization.Format(_selectionMessage);
        UpdateTooltip();
    }

    /// <summary>
    /// Formats comma-separated language codes preserving file order of appearance.
    /// </summary>
    public static string FormatItemLanguages(ParsedRomName parsed)
    {
        var isoLangs = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var raw in parsed.LanguageCodes)
        {
            if (string.IsNullOrWhiteSpace(raw))
            {
                continue;
            }

            var trimmed = raw.Trim();
            var upper = trimmed.ToUpperInvariant();
            if (upper.StartsWith('M') && (upper.Length == 1 || upper[1..].All(char.IsDigit)))
            {
                continue;
            }

            var lower = trimmed.ToLowerInvariant();
            if (seen.Add(lower))
            {
                isoLangs.Add(lower);
            }
        }

        if (isoLangs.Count > 0)
        {
            return string.Join(", ", isoLangs);
        }

        if (!string.IsNullOrEmpty(parsed.Language))
        {
            return parsed.Language;
        }

        if (parsed.CountryCodes.Count > 0)
        {
            var implied = new List<string>();
            foreach (var c in parsed.CountryCodes)
            {
                if (!string.IsNullOrWhiteSpace(c)
                    && ReleaseScoringService.CountryDefaultLanguages.TryGetValue(c.Trim().ToUpperInvariant(), out var defaultLang)
                    && seen.Add(defaultLang))
                {
                    implied.Add(defaultLang);
                }
            }

            if (implied.Count > 0)
            {
                return string.Join(", ", implied);
            }
        }

        return string.Empty;
    }

    /// <summary>
    /// Formats comma-separated country/region codes preserving file order of appearance.
    /// </summary>
    public static string FormatItemCountries(ParsedRomName parsed)
    {
        if (parsed.CountryCodes.Count > 0)
        {
            var countries = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var c in parsed.CountryCodes)
            {
                if (!string.IsNullOrWhiteSpace(c))
                {
                    var upper = c.Trim().ToUpperInvariant();
                    if (seen.Add(upper))
                    {
                        countries.Add(upper);
                    }
                }
            }

            return string.Join(", ", countries);
        }

        return parsed.Country ?? string.Empty;
    }

    /// <summary>
    /// Formats the disc/media number (<c>1 of 2</c>) according to <c>_format_item_disk</c>.
    /// </summary>
    public static (string DisplayText, (int DiscNum, int DiscTotal, string RawLabel) SortKey) FormatItemDisk(
        ParsedRomName parsed,
        ILocalizationService localization)
    {
        if (parsed.DiscNumber.HasValue && parsed.DiscTotal.HasValue)
        {
            if (parsed.DiscTotal.Value > 1)
            {
                var txt = localization.Tr("panel4.disk_n_of_total",
                    ("current", parsed.DiscNumber.Value),
                    ("total", parsed.DiscTotal.Value));
                return (txt, (parsed.DiscNumber.Value, parsed.DiscTotal.Value, txt));
            }

            return (string.Empty, (0, 0, string.Empty));
        }

        string?[] candidates = [parsed.MediaLabel, parsed.MediaType, parsed.RawOptParens, parsed.RawFilename];
        foreach (var text in candidates)
        {
            if (string.IsNullOrEmpty(text))
            {
                continue;
            }

            var match = DiskOfTotalRegex().Match(text);
            if (match.Success
                && int.TryParse(match.Groups[1].ValueSpan, out var cur)
                && int.TryParse(match.Groups[2].ValueSpan, out var tot))
            {
                if (tot > 1)
                {
                    var display = localization.Tr("panel4.disk_n_of_total", ("current", cur), ("total", tot));
                    return (display, (cur, tot, display));
                }

                return (string.Empty, (0, 0, string.Empty));
            }
        }

        if (parsed.IsMultiMedia)
        {
            if (parsed.DiscNumber.HasValue)
            {
                var s = parsed.DiscNumber.Value.ToString();
                return (s, (parsed.DiscNumber.Value, 1, s));
            }

            // MediaType is not shown: for No-Intro / Redump it is the kind of media ("cd", "cartridge"), not a disk.
            var label = parsed.MediaLabel?.Trim() ?? string.Empty;
            if (!string.IsNullOrEmpty(label) && !label.StartsWith("track", StringComparison.OrdinalIgnoreCase))
            {
                var numMatch = DiskNumberOnlyRegex().Match(label);
                if (numMatch.Success && int.TryParse(numMatch.Groups[1].ValueSpan, out var num))
                {
                    var numStr = numMatch.Groups[1].Value;
                    return (numStr, (num, 1, numStr));
                }

                return (label, (1, 1, label));
            }
        }

        return (string.Empty, (0, 0, string.Empty));
    }

    /// <summary>
    /// Badge of the files transferred with <paramref name="item"/>, counted by kind: CHDs, tracks of a descriptor, other
    /// files of its game (<c>+ 3 tracks</c>, <c>+ 2 files, 1 CHD</c>); empty without any.
    /// </summary>
    public static string FormatRelatedFilesBadge(ProcessItem item, ILocalizationService localization)
    {
        if (item.RelatedItems.Count == 0)
        {
            return string.Empty;
        }

        var chds = item.RelatedItems.Count(IsChd);
        var others = item.RelatedItems.Count - chds;
        var parts = new List<string>(2);
        if (others > 0)
        {
            // The files of a descriptor are its tracks; the other files of a game are only files
            var kind = OpticalCompanionService.IsDescriptorFile(item.DisplayName) ? "tracks" : "files";
            parts.Add(others == 1
                ? localization.Tr($"preview.related_{kind}_one")
                : localization.Tr($"preview.related_{kind}", ("count", others)));
        }

        if (chds > 0)
        {
            parts.Add(chds == 1
                ? localization.Tr("preview.related_chd_one")
                : localization.Tr("preview.related_chd", ("count", chds)));
        }

        return localization.Tr("preview.related_badge", ("parts", string.Join(", ", parts)));
    }

    private static bool IsChd(ProcessItem item) =>
        item.Chd is not null || string.Equals(Path.GetExtension(item.DisplayName), ".chd", StringComparison.OrdinalIgnoreCase);

    private IReadOnlyList<PreviewRelatedFile> BuildRelatedFiles()
    {
        if (Item.RelatedItems.Count == 0)
        {
            return [];
        }

        var isEn = !string.Equals(_localization.CurrentLocale, "fr", StringComparison.OrdinalIgnoreCase);
        return Item.RelatedItems
            .Select(rel => new PreviewRelatedFile(
                rel.DisplayName,
                rel.FileSize > 0 ? FormatBytes(rel.FileSize, isEn) : string.Empty,
                FormatTarget(rel.FinalTargetPath ?? rel.TargetPath),
                rel.Chd is { } chd ? FormatChd(chd, _localization) : string.Empty))
            .ToList();
    }

    /// <summary>Target path shown in the table: relative to the destination when it lies inside.</summary>
    private string FormatTarget(string? target)
    {
        var rawTarget = target ?? string.Empty;
        if (!string.IsNullOrEmpty(rawTarget) && !string.IsNullOrEmpty(_destinationRootDir))
        {
            try
            {
                var rel = Path.GetRelativePath(_destinationRootDir, rawTarget);
                if (!rel.StartsWith("..", StringComparison.Ordinal))
                {
                    rawTarget = rel;
                }
            }
            catch
            {
                // Keep rawTarget
            }
        }

        return rawTarget;
    }

    private static string GetStatusLabelKey(FileStatus status) =>
        status switch
        {
            FileStatus.Valid => "status.valid",
            FileStatus.Excluded => "status.excluded",
            FileStatus.Unmatched => "status.unmatched",
            FileStatus.Duplicate => "status.duplicate",
            _ => "status.error"
        };

    /// <summary>Whether the item won 1G1R by a choice of the user (the reason set by the resolver says so).</summary>
    public static bool IsManualWinner(ProcessItem item) =>
        item.Is1G1RWinner && item.StatusReason?.Key == "reason.1g1r_winner_manual";

    /// <summary>
    /// Whether the item is a MAME set kept only because a kept set needs it (the reason set by the release selection
    /// says so), whatever excluded it before.
    /// </summary>
    public static bool IsRequiredMameSet(ProcessItem item) =>
        item.Status == FileStatus.Valid && MameSetDependencies.IsRequiredSetReason(item.StatusReason);

    public static string GetStatusLabel(FileStatus status, ILocalizationService localization) =>
        Enum.IsDefined(status) ? localization.Tr(GetStatusLabelKey(status)) : status.ToValueString();

    public static string FormatBytes(long bytes, bool isEn)
    {
        if (bytes <= 0)
        {
            return $"0 {(isEn ? "B" : "o")}";
        }
        if (bytes < 1024)
        {
            return $"{bytes} {(isEn ? "B" : "o")}";
        }
        if (bytes < 1024L * 1024L)
        {
            return $"{(bytes / 1024.0).ToString("F2", CultureInfo.InvariantCulture)} {(isEn ? "KB" : "Ko")}";
        }
        var mb = bytes / (1024.0 * 1024.0);
        if (mb >= 1024.0)
        {
            return $"{(mb / 1024.0).ToString("F2", CultureInfo.InvariantCulture)} {(isEn ? "GB" : "Go")}";
        }
        return $"{mb.ToString("F2", CultureInfo.InvariantCulture)} {(isEn ? "MB" : "Mo")}";
    }

    /// <summary>
    /// Checksum of the detail panel: the SHA-1 of a CHD (the global SHA-1 of its header, which identifies it in a DAT),
    /// otherwise the CRC32 of the file; none for a whole archive (a MAME set) or a file whose CRC was not computed.
    /// </summary>
    public static (string Label, string Text) GetDetailChecksum(ProcessItem item)
    {
        if (item.Chd is { Sha1.Length: > 0 } chd)
        {
            return ("SHA-1", chd.Sha1.ToLowerInvariant());
        }

        return !string.IsNullOrEmpty(item.Crc32)
            ? ("CRC32", item.Crc32.ToUpperInvariant())
            : (string.Empty, string.Empty);
    }

    /// <summary>
    /// Description of a MAME set for the detail panel: its machine, complete or not, then what it lacks (ROMs, CHDs,
    /// an absent BIOS, devices), the set a split clone depends on, the other sets it needs, the clones a merged set
    /// holds and the members the DAT does not know.
    /// </summary>
    public static LocalizedMessage DescribeMameSet(MameSetInfo set)
    {
        var details = new List<LocalizedMessage>();
        if (set.MissingRoms > 0)
        {
            details.Add(LocalizedMessage.Create("preview.mame_missing_roms", ("count", set.MissingRoms)));
        }
        if (set.MissingDisks > 0)
        {
            details.Add(LocalizedMessage.Create("preview.mame_missing_disks", ("count", set.MissingDisks)));
        }
        if (set.RequiresBios && set.RequiredSet is { } bios)
        {
            details.Add(LocalizedMessage.Create("preview.mame_bios_absent", ("set", bios)));
        }
        else if (set.IsSplit && set.RequiredSet is { } parent)
        {
            details.Add(LocalizedMessage.Create("preview.mame_split", ("set", parent)));
        }
        if (set.MissingDevices.Count > 0)
        {
            details.Add(LocalizedMessage.Create("preview.mame_missing_devices", ("names", MameSetDependencies.FormatNames(set.MissingDevices))));
        }
        if (set.RequiredSets.Count > 0)
        {
            details.Add(LocalizedMessage.Create("preview.mame_needs_sets", ("names", MameSetDependencies.FormatNames(set.RequiredSets))));
        }
        if (set.IsMerged)
        {
            details.Add(LocalizedMessage.Create("preview.mame_merged", ("count", set.ClonesHeld)));
        }
        if (set.UnknownMembers > 0)
        {
            details.Add(LocalizedMessage.Create("preview.mame_unknown_members", ("count", set.UnknownMembers)));
        }

        var summary = LocalizedMessage.Create(set.IsComplete ? "preview.mame_set_complete" : "preview.mame_set_incomplete", ("set", set.GameName));
        if (details.Count == 0)
        {
            return summary;
        }

        var joined = details.Skip(1).Aggregate(
            details[0],
            (acc, next) => LocalizedMessage.Create("preview.mame_detail_join", ("first", acc), ("next", next)));
        return LocalizedMessage.Create("preview.mame_set_details", ("summary", summary), ("details", joined));
    }

    /// <summary>
    /// Text of the MAME set row of the detail panel: the set, then its machine on a line of its own (category, cabinet).
    /// </summary>
    public static string FormatMameSet(MameSetInfo set, MameMachineInfo? machine, ILocalizationService localization)
    {
        var text = localization.Format(DescribeMameSet(set));
        return machine is not null && DescribeMameMachine(machine) is { } machineText
            ? text + Environment.NewLine + localization.Format(machineText)
            : text;
    }

    /// <summary>
    /// A MAME machine beyond its set: its category (category file), its rating (rating file), then its cabinet as the DAT
    /// describes it (screen, players, kinds of controls, buttons). <c>null</c> when nothing is known of them.
    /// </summary>
    public static LocalizedMessage? DescribeMameMachine(MameMachineInfo machine)
    {
        var parts = new List<LocalizedMessage>();
        if (machine.Category is { } category)
        {
            parts.Add(LocalizedMessage.Create(
                category.IsMature ? "preview.mame_category_mature" : "preview.mame_category",
                ("category", category.Name)));
        }

        if (machine.Rating is { } rating)
        {
            parts.Add(rating.IsUnrated
                ? LocalizedMessage.Create("preview.mame_unrated")
                : LocalizedMessage.Create("preview.mame_rating", ("rating", rating.Name)));
        }

        if (machine.Cabinet is { } cabinet)
        {
            AddCabinetParts(parts, cabinet);
        }

        return parts.Count == 0
            ? null
            : parts.Skip(1).Aggregate(
                parts[0],
                (acc, next) => LocalizedMessage.Create("preview.mame_detail_join", ("first", acc), ("next", next)));
    }

    private static void AddCabinetParts(List<LocalizedMessage> parts, MameCabinet cabinet)
    {
        if (cabinet.Orientation != MameOrientation.Unknown)
        {
            parts.Add(cabinet.Screen == MameScreenType.Unknown
                ? LocalizedMessage.Create("preview.mame_orientation", ("orientation", cabinet.Orientation.ToMessage()))
                : LocalizedMessage.Create("preview.mame_screen",
                    ("screen", cabinet.Screen.ToMessage()),
                    ("orientation", cabinet.Orientation.ToMessage())));
        }
        if (cabinet.Players > 0)
        {
            parts.Add(LocalizedMessage.Create("preview.mame_players", ("count", cabinet.Players)));
        }
        parts.AddRange(EnumExtensions.MameControlKinds.Where(kind => cabinet.Controls.HasFlag(kind)).Select(kind => kind.ToMessage()));
        if (cabinet.Buttons > 0)
        {
            parts.Add(LocalizedMessage.Create("preview.mame_buttons", ("count", cabinet.Buttons)));
        }
    }

    /// <summary>Tooltip line of a CHD: version, media, tracks (audio ones counted apart), and its parent when differential.</summary>
    public static string FormatChd(ChdInfo chd, ILocalizationService localization)
    {
        var media = localization.Tr($"chd.media_{chd.MediaKind.ToString().ToLowerInvariant()}");
        var audio = chd.Tracks.Count(t => string.Equals(t.Type, "AUDIO", StringComparison.OrdinalIgnoreCase));
        var line = chd.Tracks.Count > 0
            ? localization.Tr("preview.tooltip_chd_tracks", ("version", chd.Version), ("media", media), ("tracks", chd.Tracks.Count), ("audio", audio))
            : localization.Tr("preview.tooltip_chd", ("version", chd.Version), ("media", media));
        return chd.IsDifferential ? $"{line}{Environment.NewLine}{localization.Tr("preview.tooltip_chd_differential")}" : line;
    }

    public static string BuildItemTooltip(ProcessItem item, ILocalizationService localization)
    {
        // French writes sizes in octets (o, Ko, Mo, Go); the other languages in bytes (B, KB, MB, GB)
        var isEn = !string.Equals(localization.CurrentLocale, "fr", StringComparison.OrdinalIgnoreCase);
        var lines = new List<string>(8)
        {
            !string.IsNullOrEmpty(item.ArchiveMember)
                ? item.ArchiveMember
                : Path.GetFileName(item.SourcePath)
        };

        var totalBytes = item.FileSize + item.RelatedItems.Sum(r => r.FileSize);
        if (totalBytes > 0)
        {
            lines.Add(localization.Tr("preview.tooltip_size", ("size", FormatBytes(totalBytes, isEn))));
        }

        if (!string.IsNullOrEmpty(item.Crc32))
        {
            lines.Add($"CRC32 : {item.Crc32.ToUpperInvariant()}");
        }

        if (item.RelatedItems.Count > 0)
        {
            var compMsg = item.RelatedItems.Count == 1
                ? localization.Tr("preview.companion_file_single")
                : localization.Tr("preview.companion_files_count", ("count", item.RelatedItems.Count));
            lines.Add(compMsg);

            if (item.RelatedItems.Count <= 10)
            {
                foreach (var rel in item.RelatedItems)
                {
                    var relName = !string.IsNullOrEmpty(rel.ArchiveMember)
                        ? rel.ArchiveMember
                        : Path.GetFileName(rel.SourcePath);
                    var relSize = rel.FileSize > 0 ? $" ({FormatBytes(rel.FileSize, isEn)})" : string.Empty;
                    lines.Add($"  • {relName}{relSize}");
                }
            }
            else
            {
                foreach (var rel in item.RelatedItems.Take(8))
                {
                    var relName = !string.IsNullOrEmpty(rel.ArchiveMember)
                        ? rel.ArchiveMember
                        : Path.GetFileName(rel.SourcePath);
                    var relSize = rel.FileSize > 0 ? $" ({FormatBytes(rel.FileSize, isEn)})" : string.Empty;
                    lines.Add($"  • {relName}{relSize}");
                }
                lines.Add($"  ... (+ {item.RelatedItems.Count - 8})");
            }
        }

        if (!string.IsNullOrEmpty(item.WinnerReference))
        {
            // Single 1G1R line: "<winner> (<pure reason>)" : EliminationDetail contains
            // the raw reason (e.g., "clean version [!] vs modified"), WinnerReference the winner's
            // name. We assemble here to avoid any duplication in the tooltip.
            var prefix = isEn ? "1G1R: " : "1G1R : ";
            var translatedDetail = localization.Format(item.EliminationDetail);
            var line1G1R = !string.IsNullOrEmpty(translatedDetail)
                ? $"{prefix}{item.WinnerReference} ({translatedDetail})"
                : $"{prefix}{item.WinnerReference}";
            lines.Add(line1G1R);
        }
        else if (item.StatusReason is not null)
        {
            // No 1G1R context: display the raw status reason
            // (e.g., "Alternate Dump [a] excluded", "File not cataloged in DAT file").
            lines.Add(localization.Format(item.StatusReason));
        }

        if (item.Chd is { } chd)
        {
            lines.Add(FormatChd(chd, localization));
        }

        if (item.IsIdentifiedByName)
        {
            lines.Add(localization.Tr("preview.tooltip_identified_by_name", ("game", item.DatGameName ?? string.Empty)));
        }

        var target = item.FinalTargetPath ?? item.TargetPath;
        if (!string.IsNullOrEmpty(target))
        {
            lines.Add(target);
        }

        return string.Join(Environment.NewLine, lines);
    }
}
