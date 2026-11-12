using RomSieve.Core.Models;
using static RomSieve.Core.Services.ConfigDictionaryReader;

namespace RomSieve.Core.Services;

/// <summary>
/// Conversion between the <c>config.json</c> dictionary (or a profile), the typed <see cref="SortSettings"/> and the
/// <see cref="SortConfig"/> of a run. The keys and the serialized values are those of the saved configuration files
/// and must never change: a change of format goes through <see cref="ConfigSchema"/>.
/// </summary>
public static class SortSettingsSerializer
{
    /// <summary>
    /// Reads the options from a configuration dictionary, first brought to the current format by
    /// <see cref="ConfigSchema.Migrate"/>. A missing or unreadable key keeps the value of <paramref name="baseline"/>
    /// (the defaults without it).
    /// </summary>
    public static SortSettings Read(IReadOnlyDictionary<string, object?> data, SortSettings? baseline = null)
    {
        data = ConfigSchema.Migrate(data);
        var s = baseline?.Clone() ?? new SortSettings();
        ReadDirectories(data, s);
        ReadTosecFilters(data, s);
        ReadNoIntroFilters(data, s);
        ReadPriorities(data, s);
        return s;
    }

    /// <summary>
    /// Header names of DATs of a lot, trimmed, without empty names and duplicates (case ignored), in the order given: the
    /// DATs left out of a sort (<c>excluded_dats</c>).
    /// </summary>
    public static List<string> NormalizeDatNames(IEnumerable<string> names) =>
        names.Select(name => name.Trim()).Where(name => name.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    /// <summary>Writes the options under their <c>config.json</c> keys.</summary>
    public static void Write(SortSettings s, IDictionary<string, object?> data)
    {
        data[ConfigSchema.VersionKey] = ConfigSchema.CurrentVersion;

        // Directories
        data["source_dir"] = s.SourceDir.Trim();
        data["destination_dir"] = s.DestinationDir.Trim();
        data["dat_file"] = s.DatFile.Trim();
        data["dat_recursive"] = s.DatRecursive;
        data["excluded_dats"] = NormalizeDatNames(s.ExcludedDats);
        data["folder_per_dat"] = s.FolderPerDat;
        data["dat_folder_mirror"] = s.DatFolderMirror;
        data["chd_dir"] = s.ChdDir.Trim();
        data["mame_category_file"] = s.MameCategoryFile.Trim();
        data["mame_rating_file"] = s.MameRatingFile.Trim();
        data["naming_convention"] = s.NamingConvention.ToConfigString();
        data["recursive"] = s.Recursive;
        data["scan_archives"] = s.ScanArchives;
        data["append_to_destination"] = s.AppendToDestination;
        data["transfer_mode"] = s.TransferMode.ToConfigString();
        data["collision_mode"] = s.CollisionMode.ToConfigString();
        data["verify_copies"] = s.VerifyCopies;
        data["preserve_archives"] = s.PreserveArchives;
        data["isolate_unmatched"] = s.IsolateUnmatched;
        data["hashing_parallelism"] = s.HashingParallelism;
        data["verify_sha1"] = s.VerifySha1;
        data["verify_archive_contents"] = s.VerifyArchiveContents;
        data["match_chd_by_name"] = s.MatchChdByName;
        data["flat_destination"] = s.FlatDestination;
        data["pattern"] = string.IsNullOrWhiteSpace(s.Pattern) ? SortSettings.DefaultPattern : s.Pattern.Trim();
        data["restore_articles"] = s.RestoreArticles;
        data["subfolder_multidisk"] = s.SubfolderMultidisk;
        data["generate_m3u"] = s.GenerateM3u;
        data["m3u_placement"] = s.M3uPlacement.ToConfigString();

        // Filters of every convention
        data[ConfigSchema.FilterNotInDatKey] = s.FilterNotInDat;

        // TOSEC criteria
        data["dumps_verified_only"] = s.DumpsVerifiedOnly;
        data["exclude_bad_dumps"] = s.ExcludeBadDumps;
        data["exclude_over_under"] = s.ExcludeOverUnder;
        data["exclude_virus"] = s.ExcludeVirus;
        data["exclude_alternate"] = s.ExcludeAlternate;
        data["exclude_modifications"] = s.ExcludeModifications;
        data["exclude_translations"] = s.ExcludeTranslations;
        data["exclude_dev_versions"] = s.ExcludeDevVersions;
        data["exclude_free_pd"] = s.ExcludeFreePd;
        data["exclude_demos"] = s.ExcludeDemos;
        data["complete_multidisk_sets_only"] = s.CompleteMultidiskSetsOnly;
        data["smart_multidisk_grouping"] = s.SmartMultidiskGrouping;
        data["ignore_modifications_in_priority"] = s.IgnoreModificationsInPriority;
        data["preferred_media_type"] = s.PreferredMediaType.ToConfigString();
        data["amiga_target"] = s.AmigaTarget.ToConfigString();

        // No-Intro and Redump criteria
        data["nointro_exclude_bad_dumps"] = s.NoIntroExcludeBadDumps;
        data["nointro_exclude_alternate"] = s.NoIntroExcludeAlternate;
        data["nointro_complete_multimedia_sets_only"] = s.NoIntroCompleteMultimediaSetsOnly;
        data["nointro_exclude_pirate"] = s.NoIntroExcludePirate;
        data["nointro_exclude_hack"] = s.NoIntroExcludeHack;
        data["nointro_exclude_translations"] = s.NoIntroExcludeTranslations;
        data["nointro_exclude_dev_versions"] = s.NoIntroExcludeDevVersions;
        data["nointro_exclude_unlicensed"] = s.NoIntroExcludeUnlicensed;
        data["nointro_exclude_demos"] = s.NoIntroExcludeDemos;
        data["nointro_exclude_rereleases"] = s.NoIntroExcludeRereleases;

        // MAME machines
        data["mame_exclude_bios"] = s.MameExcludeBios;
        data["mame_exclude_devices"] = s.MameExcludeDevices;
        data["mame_exclude_mechanical"] = s.MameExcludeMechanical;
        data["mame_exclude_requires_artwork"] = s.MameExcludeRequiresArtwork;
        data["mame_exclude_nothing_to_dump"] = s.MameExcludeNothingToDump;
        data["mame_driver_statuses"] = s.MameDriverStatuses.ToConfigStrings();
        data["mame_keep_playable"] = s.MameKeepPlayable;
        data["mame_exclude_unemulated_sound"] = s.MameExcludeUnemulatedSound;
        data["mame_exclude_alternate_sets"] = s.MameExcludeAlternateSets;
        data["mame_orientation"] = s.MameOrientation.ToConfigString();
        data["mame_exclude_vector"] = s.MameExcludeVector;
        data["mame_exclude_lcd"] = s.MameExcludeLcd;
        data["mame_minimum_players"] = s.MameMinimumPlayers;
        data["mame_maximum_buttons"] = s.MameMaximumButtons;
        data["mame_excluded_controls"] = s.MameExcludedControls.ToConfigStrings();
        data["mame_exclude_mature"] = s.MameExcludeMature;
        data["mame_arcade_only"] = s.MameArcadeOnly;
        data["mame_excluded_genres"] = s.MameExcludedGenres.ToList();
        data["mame_minimum_rating"] = s.MameMinimumRating;
        data["mame_exclude_unrated"] = s.MameExcludeUnrated;
        data["mame_size_budget_gb"] = s.MameSizeBudgetGb;

        // Languages, countries and 1G1R
        data["filter_languages"] = s.FilterLanguages;
        data["fallback_unmatched_languages"] = s.FallbackUnmatchedLanguages;
        data["filter_countries"] = s.FilterCountries;
        data["fallback_unmatched_countries"] = s.FallbackUnmatchedCountries;
        data["priority_countries"] = s.PriorityCountries.ToList();
        data["region_priority_mode"] = s.RegionPriorityMode.ToConfigString();
        data["tiebreak_order_tosec"] = s.TosecTiebreakOrder.Select(c => c.ToConfigString()).ToList();
        data["tiebreak_order_nointro"] = s.NoIntroTiebreakOrder.Select(c => c.ToConfigString()).ToList();
        data["tiebreak_order_redump"] = s.RedumpTiebreakOrder.Select(c => c.ToConfigString()).ToList();
        data["mame_tiebreak_order"] = s.MameTiebreakOrder.Select(c => c.ToConfigString()).ToList();
        data["tiebreak_order_softlist"] = s.SoftwareListTiebreakOrder.Select(c => c.ToConfigString()).ToList();
        data["one_game_one_rom"] = s.OneGameOneRom;
        data["preferred_video"] = s.PreferredVideo.ToConfigString();
        data["prefer_monolingual"] = s.PreferMonolingual;
        data["priority_languages"] = s.PriorityLanguages.ToList();
    }

    /// <summary>
    /// Builds the configuration of a run. An empty source or destination becomes the current directory, a flat
    /// destination an empty pattern, and an empty DAT path no DAT.
    /// </summary>
    public static SortConfig ToSortConfig(SortSettings s, IReadOnlyDictionary<string, string>? manual1G1RWinners = null)
    {
        var config = new SortConfig
        {
            SourceDir = string.IsNullOrWhiteSpace(s.SourceDir) ? "." : s.SourceDir.Trim(),
            DestinationDir = string.IsNullOrWhiteSpace(s.DestinationDir) ? "." : s.DestinationDir.Trim(),
            Recursive = s.Recursive,
            TransferMode = s.TransferMode,
            CollisionMode = s.CollisionMode,
            VerifyCopies = s.VerifyCopies,
            PreserveArchives = s.PreserveArchives,
            IsolateUnmatched = s.IsolateUnmatched,
            HashingParallelism = s.HashingParallelism,
            VerifySha1 = s.VerifySha1,
            VerifyArchiveContents = s.VerifyArchiveContents,
            MatchChdByName = s.MatchChdByName,
            Pattern = s.FlatDestination ? string.Empty : s.Pattern.Trim(),
            RestoreArticles = s.RestoreArticles,
            SubfolderMultidisk = s.SubfolderMultidisk,
            GenerateM3u = s.GenerateM3u,
            M3uPlacement = s.M3uPlacement,
            AppendToDestination = s.AppendToDestination,
            DatFile = string.IsNullOrWhiteSpace(s.DatFile) ? null : s.DatFile.Trim(),
            DatRecursive = s.DatRecursive,
            ExcludedDats = NormalizeDatNames(s.ExcludedDats),
            FolderPerDat = s.FolderPerDat,
            DatFolderMirror = s.DatFolderMirror,
            ChdDir = string.IsNullOrWhiteSpace(s.ChdDir) ? null : s.ChdDir.Trim(),
            MameCategoryFile = string.IsNullOrWhiteSpace(s.MameCategoryFile) ? null : s.MameCategoryFile.Trim(),
            MameRatingFile = string.IsNullOrWhiteSpace(s.MameRatingFile) ? null : s.MameRatingFile.Trim(),
            NamingConvention = s.NamingConvention,
            Filters = new FilterConfig
            {
                ScanArchives = s.ScanArchives,
                Manual1G1RWinners = manual1G1RWinners is null
                    ? new Dictionary<string, string>(StringComparer.Ordinal)
                    : new Dictionary<string, string>(manual1G1RWinners, StringComparer.Ordinal),
                FilterNotInDat = s.FilterNotInDat,

                DumpsVerifiedOnly = s.DumpsVerifiedOnly,
                ExcludeBadDumps = s.ExcludeBadDumps,
                ExcludeAlternate = s.ExcludeAlternate,
                ExcludeOverUnder = s.ExcludeOverUnder,
                ExcludeVirus = s.ExcludeVirus,
                ExcludeModifications = s.ExcludeModifications,
                ExcludeTranslations = s.ExcludeTranslations,
                ExcludeDevVersions = s.ExcludeDevVersions,
                ExcludeFreePd = s.ExcludeFreePd,
                ExcludeDemos = s.ExcludeDemos,
                CompleteMultidiskSetsOnly = s.CompleteMultidiskSetsOnly,
                SmartMultidiskGrouping = s.SmartMultidiskGrouping,
                IgnoreModificationsInPriority = s.IgnoreModificationsInPriority,
                PreferredMediaType = s.PreferredMediaType,
                AmigaTarget = s.AmigaTarget,

                NoIntroExcludeBadDumps = s.NoIntroExcludeBadDumps,
                NoIntroExcludeAlternate = s.NoIntroExcludeAlternate,
                NoIntroCompleteMultimediaSetsOnly = s.NoIntroCompleteMultimediaSetsOnly,
                NoIntroExcludePirate = s.NoIntroExcludePirate,
                NoIntroExcludeHack = s.NoIntroExcludeHack,
                NoIntroExcludeTranslations = s.NoIntroExcludeTranslations,
                NoIntroExcludeDevVersions = s.NoIntroExcludeDevVersions,
                NoIntroExcludeUnlicensed = s.NoIntroExcludeUnlicensed,
                NoIntroExcludeDemos = s.NoIntroExcludeDemos,
                NoIntroExcludeRereleases = s.NoIntroExcludeRereleases,

                MameExcludeBios = s.MameExcludeBios,
                MameExcludeDevices = s.MameExcludeDevices,
                MameExcludeMechanical = s.MameExcludeMechanical,
                MameExcludeRequiresArtwork = s.MameExcludeRequiresArtwork,
                MameExcludeNothingToDump = s.MameExcludeNothingToDump,
                MameDriverStatuses = s.MameDriverStatuses,
                MameKeepPlayable = s.MameKeepPlayable,
                MameExcludeUnemulatedSound = s.MameExcludeUnemulatedSound,
                MameExcludeAlternateSets = s.MameExcludeAlternateSets,
                MameOrientation = s.MameOrientation,
                MameExcludeVector = s.MameExcludeVector,
                MameExcludeLcd = s.MameExcludeLcd,
                MameMinimumPlayers = s.MameMinimumPlayers,
                MameMaximumButtons = s.MameMaximumButtons,
                MameExcludedControls = s.MameExcludedControls,
                MameExcludeMature = s.MameExcludeMature,
                MameArcadeOnly = s.MameArcadeOnly,
                MameExcludedGenres = s.MameExcludedGenres.ToList(),
                MameMinimumRating = s.MameMinimumRating,
                MameExcludeUnrated = s.MameExcludeUnrated,
                MameSizeBudgetBytes = (long)Math.Round(s.MameSizeBudgetGb * 1_000_000_000d),

                FilterLanguages = s.FilterLanguages,
                PriorityLanguages = s.PriorityLanguages.ToList(),
                FallbackUnmatchedLanguages = s.FallbackUnmatchedLanguages,
                FilterCountries = s.FilterCountries,
                PriorityCountries = s.PriorityCountries.ToList(),
                FallbackUnmatchedCountries = s.FallbackUnmatchedCountries,
                RegionPriorityMode = s.RegionPriorityMode,
                TosecTiebreakOrder = s.TosecTiebreakOrder.ToList(),
                NoIntroTiebreakOrder = s.NoIntroTiebreakOrder.ToList(),
                RedumpTiebreakOrder = s.RedumpTiebreakOrder.ToList(),
                MameTiebreakOrder = s.MameTiebreakOrder.ToList(),
                SoftwareListTiebreakOrder = s.SoftwareListTiebreakOrder.ToList(),
                OneGameOneRom = s.OneGameOneRom,
                PreferredVideo = s.PreferredVideo,
                PreferMonolingual = s.PreferMonolingual
            }
        };

        config.Filters.NormalizeDefaults();
        return config;
    }

    private static void ReadDirectories(IReadOnlyDictionary<string, object?> data, SortSettings s)
    {
        if (TryGetString(data, "source_dir", out var src)) s.SourceDir = src;
        if (TryGetString(data, "destination_dir", out var dst)) s.DestinationDir = dst;
        if (TryGetString(data, "dat_file", out var dat)) s.DatFile = dat;
        if (TryGetBool(data, "dat_recursive", out var datRecursive)) s.DatRecursive = datRecursive;
        if (TryGetStringList(data, "excluded_dats", out var excludedDats)) s.ExcludedDats = NormalizeDatNames(excludedDats);
        if (TryGetBool(data, "folder_per_dat", out var folderPerDat)) s.FolderPerDat = folderPerDat;
        if (TryGetBool(data, "dat_folder_mirror", out var datFolderMirror)) s.DatFolderMirror = datFolderMirror;
        if (TryGetString(data, "chd_dir", out var chdDir)) s.ChdDir = chdDir;
        if (TryGetString(data, "mame_category_file", out var categoryFile)) s.MameCategoryFile = categoryFile;
        if (TryGetString(data, "mame_rating_file", out var ratingFile)) s.MameRatingFile = ratingFile;
        if (TryGetString(data, "naming_convention", out var convention)) s.NamingConvention = EnumExtensions.ParseNamingConvention(convention);
        if (TryGetBool(data, "recursive", out var recursive)) s.Recursive = recursive;
        if (TryGetBool(data, "scan_archives", out var scanArchives)) s.ScanArchives = scanArchives;
        if (TryGetBool(data, "append_to_destination", out var append)) s.AppendToDestination = append;
        if (TryGetString(data, "transfer_mode", out var transfer)) s.TransferMode = EnumExtensions.ParseTransferMode(transfer);
        if (TryGetString(data, "collision_mode", out var collision)) s.CollisionMode = EnumExtensions.ParseCollisionMode(collision);
        if (TryGetBool(data, "verify_copies", out var verifyCopies)) s.VerifyCopies = verifyCopies;
        if (TryGetBool(data, "preserve_archives", out var preserveArchives)) s.PreserveArchives = preserveArchives;
        if (TryGetBool(data, "isolate_unmatched", out var isolateUnmatched)) s.IsolateUnmatched = isolateUnmatched;
        if (TryGetBool(data, "verify_sha1", out var verifySha1)) s.VerifySha1 = verifySha1;
        if (TryGetBool(data, "verify_archive_contents", out var verifyArchives)) s.VerifyArchiveContents = verifyArchives;
        if (TryGetBool(data, "match_chd_by_name", out var matchChd)) s.MatchChdByName = matchChd;

        // Only the values offered in the interface are accepted, any other one keeps the sequential default
        if (TryGetInt(data, "hashing_parallelism", out var parallelism))
        {
            s.HashingParallelism = SortSettings.HashingParallelismValues.Contains(parallelism) ? parallelism : 1;
        }

        if (TryGetString(data, "pattern", out var pattern) && !string.IsNullOrWhiteSpace(pattern)) s.Pattern = pattern;
        if (TryGetBool(data, "flat_destination", out var flat)) s.FlatDestination = flat;
        if (TryGetBool(data, "restore_articles", out var restoreArticles)) s.RestoreArticles = restoreArticles;
        if (TryGetBool(data, "subfolder_multidisk", out var subfolder)) s.SubfolderMultidisk = subfolder;
        if (TryGetBool(data, "generate_m3u", out var m3u)) s.GenerateM3u = m3u;
        if (TryGetString(data, "m3u_placement", out var placement)) s.M3uPlacement = EnumExtensions.ParseM3uPlacementMode(placement);
    }

    private static void ReadTosecFilters(IReadOnlyDictionary<string, object?> data, SortSettings s)
    {
        // Filters of every convention, read with the first ones
        if (TryGetBool(data, ConfigSchema.FilterNotInDatKey, out var notInDat)) s.FilterNotInDat = notInDat;

        if (TryGetBool(data, "dumps_verified_only", out var dvo)) s.DumpsVerifiedOnly = dvo;
        if (TryGetBool(data, "exclude_bad_dumps", out var ebd)) s.ExcludeBadDumps = ebd;
        if (TryGetBool(data, "exclude_over_under", out var eou)) s.ExcludeOverUnder = eou;
        if (TryGetBool(data, "exclude_virus", out var ev)) s.ExcludeVirus = ev;
        if (TryGetBool(data, "exclude_alternate", out var ea)) s.ExcludeAlternate = ea;
        if (TryGetBool(data, "exclude_modifications", out var em)) s.ExcludeModifications = em;
        if (TryGetBool(data, "exclude_translations", out var et)) s.ExcludeTranslations = et;
        if (TryGetBool(data, "exclude_dev_versions", out var edv)) s.ExcludeDevVersions = edv;
        if (TryGetBool(data, "exclude_free_pd", out var efpd)) s.ExcludeFreePd = efpd;
        if (TryGetBool(data, "exclude_demos", out var ed)) s.ExcludeDemos = ed;
        if (TryGetBool(data, "complete_multidisk_sets_only", out var cmso)) s.CompleteMultidiskSetsOnly = cmso;
        if (TryGetBool(data, "smart_multidisk_grouping", out var smg)) s.SmartMultidiskGrouping = smg;
        if (TryGetBool(data, "ignore_modifications_in_priority", out var imp)) s.IgnoreModificationsInPriority = imp;
        if (TryGetString(data, "preferred_media_type", out var pmt) && !string.IsNullOrWhiteSpace(pmt))
        {
            s.PreferredMediaType = EnumExtensions.ParseMediaPreference(pmt);
        }

        if (TryGetString(data, "amiga_target", out var at) && !string.IsNullOrWhiteSpace(at))
        {
            s.AmigaTarget = EnumExtensions.ParseAmigaTarget(at);
        }
    }

    private static void ReadNoIntroFilters(IReadOnlyDictionary<string, object?> data, SortSettings s)
    {
        if (TryGetBool(data, "nointro_exclude_bad_dumps", out var ebd)) s.NoIntroExcludeBadDumps = ebd;
        if (TryGetBool(data, "nointro_exclude_alternate", out var ea)) s.NoIntroExcludeAlternate = ea;
        if (TryGetBool(data, "nointro_complete_multimedia_sets_only", out var cmso)) s.NoIntroCompleteMultimediaSetsOnly = cmso;
        if (TryGetBool(data, "nointro_exclude_pirate", out var ep)) s.NoIntroExcludePirate = ep;
        if (TryGetBool(data, "nointro_exclude_hack", out var eh)) s.NoIntroExcludeHack = eh;
        if (TryGetBool(data, "nointro_exclude_translations", out var et)) s.NoIntroExcludeTranslations = et;
        if (TryGetBool(data, "nointro_exclude_dev_versions", out var edv)) s.NoIntroExcludeDevVersions = edv;
        if (TryGetBool(data, "nointro_exclude_unlicensed", out var eu)) s.NoIntroExcludeUnlicensed = eu;
        if (TryGetBool(data, "nointro_exclude_demos", out var ed)) s.NoIntroExcludeDemos = ed;
        if (TryGetBool(data, "nointro_exclude_rereleases", out var er)) s.NoIntroExcludeRereleases = er;
        if (TryGetBool(data, "mame_exclude_bios", out var mb)) s.MameExcludeBios = mb;
        if (TryGetBool(data, "mame_exclude_devices", out var md)) s.MameExcludeDevices = md;
        if (TryGetBool(data, "mame_exclude_mechanical", out var mm)) s.MameExcludeMechanical = mm;
        if (TryGetBool(data, "mame_exclude_requires_artwork", out var ma)) s.MameExcludeRequiresArtwork = ma;
        if (TryGetBool(data, "mame_exclude_nothing_to_dump", out var mn)) s.MameExcludeNothingToDump = mn;
        if (TryGetStringList(data, "mame_driver_statuses", out var mds)) s.MameDriverStatuses = EnumExtensions.ParseMameDriverStatuses(mds);
        if (TryGetBool(data, "mame_keep_playable", out var mkp)) s.MameKeepPlayable = mkp;
        if (TryGetBool(data, "mame_exclude_unemulated_sound", out var mus)) s.MameExcludeUnemulatedSound = mus;
        if (TryGetBool(data, "mame_exclude_alternate_sets", out var mas)) s.MameExcludeAlternateSets = mas;
        if (TryGetString(data, "mame_orientation", out var mo)) s.MameOrientation = EnumExtensions.ParseMameOrientationRequirement(mo);
        if (TryGetBool(data, "mame_exclude_vector", out var mv)) s.MameExcludeVector = mv;
        if (TryGetBool(data, "mame_exclude_lcd", out var ml)) s.MameExcludeLcd = ml;
        if (TryGetInt(data, "mame_minimum_players", out var mp)) s.MameMinimumPlayers = Math.Max(mp, 0);
        if (TryGetInt(data, "mame_maximum_buttons", out var mbt)) s.MameMaximumButtons = Math.Max(mbt, 0);
        if (TryGetStringList(data, "mame_excluded_controls", out var mec)) s.MameExcludedControls = EnumExtensions.ParseMameControls(mec);
        if (TryGetBool(data, "mame_exclude_mature", out var mmt)) s.MameExcludeMature = mmt;
        if (TryGetBool(data, "mame_arcade_only", out var mao)) s.MameArcadeOnly = mao;
        if (TryGetStringList(data, "mame_excluded_genres", out var meg))
        {
            s.MameExcludedGenres = meg.Select(g => g.Trim()).Where(g => g.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }
        if (TryGetInt(data, "mame_minimum_rating", out var mmr)) s.MameMinimumRating = Math.Clamp(mmr, 0, 100);
        if (TryGetBool(data, "mame_exclude_unrated", out var meu)) s.MameExcludeUnrated = meu;
        if (TryGetDouble(data, "mame_size_budget_gb", out var budget)) s.MameSizeBudgetGb = double.IsFinite(budget) ? Math.Max(budget, 0) : 0;
    }

    private static void ReadPriorities(IReadOnlyDictionary<string, object?> data, SortSettings s)
    {
        if (TryGetStringList(data, "priority_languages", out var languages)) s.PriorityLanguages = SortSettings.NormalizeLanguageCodes(languages);
        if (TryGetStringList(data, "priority_countries", out var countries)) s.PriorityCountries = SortSettings.NormalizeCountryCodes(countries);

        // The 1G1R deduplication relies on the language filter, which turning it on turns on
        var was1G1R = s.OneGameOneRom;
        if (TryGetBool(data, "one_game_one_rom", out var ogor)) s.OneGameOneRom = ogor;
        if (TryGetBool(data, "filter_languages", out var fl)) s.FilterLanguages = fl;
        if (s.OneGameOneRom && !was1G1R) s.FilterLanguages = true;

        if (TryGetBool(data, "fallback_unmatched_languages", out var ful)) s.FallbackUnmatchedLanguages = ful;
        if (TryGetBool(data, "filter_countries", out var fc)) s.FilterCountries = fc;
        if (TryGetBool(data, "fallback_unmatched_countries", out var fuc)) s.FallbackUnmatchedCountries = fuc;

        if (TryGetString(data, "region_priority_mode", out var rpm) && !string.IsNullOrWhiteSpace(rpm))
        {
            s.RegionPriorityMode = EnumExtensions.ParseRegionPriorityMode(rpm);
        }

        // Without a saved order, the default order of the region mode applies; a saved order is repaired (unknown
        // criteria and criteria of other conventions ignored, missing ones put back), and that of TOSEC sets the
        // region mode, which the orders of No-Intro and Redump follow
        var tosecOrder = ReadTiebreakOrder(data, "tiebreak_order_tosec", NamingConvention.Tosec, s.RegionPriorityMode);
        if (tosecOrder is not null)
        {
            s.RegionPriorityMode = Models.TiebreakOrder.ImpliedRegionMode(tosecOrder);
        }

        s.TosecTiebreakOrder = (tosecOrder ?? Models.TiebreakOrder.Default(NamingConvention.Tosec, s.RegionPriorityMode)).ToList();
        s.NoIntroTiebreakOrder = (ReadTiebreakOrder(data, "tiebreak_order_nointro", NamingConvention.NoIntro, s.RegionPriorityMode)
            ?? Models.TiebreakOrder.Default(NamingConvention.NoIntro, s.RegionPriorityMode)).ToList();
        s.RedumpTiebreakOrder = (ReadTiebreakOrder(data, "tiebreak_order_redump", NamingConvention.Redump, s.RegionPriorityMode)
            ?? Models.TiebreakOrder.Default(NamingConvention.Redump, s.RegionPriorityMode)).ToList();

        // The software of the MAME lists follows the region mode too
        s.SoftwareListTiebreakOrder = (ReadTiebreakOrder(data, "tiebreak_order_softlist", NamingConvention.MameSoftwareList, s.RegionPriorityMode)
            ?? Models.TiebreakOrder.Default(NamingConvention.MameSoftwareList, s.RegionPriorityMode)).ToList();

        // The MAME sets have their own order, without effect on the region mode
        s.MameTiebreakOrder = (ReadTiebreakOrder(data, "mame_tiebreak_order", NamingConvention.Mame, s.RegionPriorityMode)
            ?? Models.TiebreakOrder.Default(NamingConvention.Mame)).ToList();

        if (TryGetString(data, "preferred_video", out var pv) && !string.IsNullOrWhiteSpace(pv))
        {
            s.PreferredVideo = EnumExtensions.ParseVideoPreference(pv);
        }

        if (TryGetBool(data, "prefer_monolingual", out var pm)) s.PreferMonolingual = pm;
    }

    /// <summary>Saved order of <paramref name="convention"/>, repaired, or <see langword="null"/> without one.</summary>
    private static IReadOnlyList<TiebreakCriterion>? ReadTiebreakOrder(
        IReadOnlyDictionary<string, object?> data,
        string key,
        NamingConvention convention,
        RegionPriorityMode mode) =>
        TryGetStringList(data, key, out var names)
            ? Models.TiebreakOrder.Normalize(EnumExtensions.ParseTiebreakOrder(names).ToList(), convention, mode)
            : null;
}
