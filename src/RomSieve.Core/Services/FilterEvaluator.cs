using RomSieve.Core.Abstractions;
using RomSieve.Core.Models;

namespace RomSieve.Core.Services;

/// <summary>
/// Integrity and modification filters (TOSEC and No-Intro / Redump) deciding whether a file is kept.
/// </summary>
public sealed class FilterEvaluator : IFilterEvaluator
{
    public (bool IsKept, LocalizedMessage? Reason) EvaluateFilter(ParsedRomName item, FilterConfig config)
    {
        config.NormalizeDefaults();

        if (!item.IsTosecConform)
        {
            return (true, null);
        }

        var isNoIntro = item.Convention.UsesNoIntroSettings();
        if (isNoIntro)
        {
            // What only a MAME DAT says of a machine
            if (item.Machine is { } machine)
            {
                if (config.MameExcludeBios && machine.IsBios)
                    return (false, LocalizedMessage.Create("reason.mame_bios_excluded"));
                if (config.MameExcludeDevices && machine.IsDevice)
                    return (false, LocalizedMessage.Create("reason.mame_device_excluded"));
                if (config.MameExcludeMechanical && machine.IsMechanical)
                    return (false, LocalizedMessage.Create("reason.mame_mechanical_excluded"));
                if (config.MameExcludeNothingToDump && machine.HasNothingToDump)
                    return (false, LocalizedMessage.Create("reason.mame_nothing_to_dump_excluded"));
                if (config.MameExcludeRequiresArtwork && machine.RequiresArtwork)
                    return (false, LocalizedMessage.Create("reason.mame_artwork_required"));
                if (!config.MameDriverStatuses.Keeps(config.MameKeepPlayable, machine.DriverStatus, machine.EmulationStatus))
                    return (false, LocalizedMessage.Create("reason.mame_driver_excluded", ("status", machine.DriverStatus.ToMessage())));
                if (config.MameExcludeUnemulatedSound && machine.UnemulatedFeatures.HasFlag(MameFeatures.Sound))
                    return (false, LocalizedMessage.Create("reason.mame_sound_unemulated"));
                if (machine.Cabinet is { } cabinet && EvaluateCabinet(cabinet, config) is { } cabinetReason)
                    return (false, cabinetReason);
                if (machine.Category is { } category && EvaluateCategory(category, config) is { } categoryReason)
                    return (false, categoryReason);
                if (machine.Rating is { } rating && EvaluateRating(rating, config) is { } ratingReason)
                    return (false, ratingReason);
            }

            if (item.Convention.IsMameSets())
            {
                // A MAME set or software has no dump quality (a baddump ROM is the best known dump): only the alternate
                // sets of the machines are left out, by the option of the MAME machines
                if (item.Convention == NamingConvention.Mame && config.MameExcludeAlternateSets && item.IsAlternate)
                    return (false, LocalizedMessage.Create("reason.mame_alternate_set_excluded"));
            }
            else
            {
                if (config.NoIntroExcludeBadDumps && item.IsBadDump)
                    return (false, LocalizedMessage.Create("reason.bad_dump_excluded"));
                if (config.NoIntroExcludeAlternate && item.IsAlternate)
                    return (false, LocalizedMessage.Create("reason.alternate_dump_excluded"));
            }

            if (config.NoIntroExcludePirate && item.IsPirate)
                return (false, LocalizedMessage.Create("reason.pirate_excluded"));
            if (config.NoIntroExcludeHack && item.IsHack)
                return (false, LocalizedMessage.Create("reason.hack_excluded"));
            if (config.NoIntroExcludeTranslations && item.IsTranslation)
                return (false, LocalizedMessage.Create("reason.translation_excluded"));
            if (config.NoIntroExcludeDevVersions && !string.IsNullOrEmpty(item.DevStatus))
                return (false, LocalizedMessage.Create("reason.dev_version_excluded", ("status", item.DevStatus)));
            if (config.NoIntroExcludeUnlicensed && !string.IsNullOrEmpty(item.Copyright))
                return (false, LocalizedMessage.Create("reason.free_pd_excluded", ("copyright", item.Copyright)));
            if (config.NoIntroExcludeDemos && !string.IsNullOrEmpty(item.Demo))
                return (false, LocalizedMessage.Create("reason.demo_excluded"));
            if (config.NoIntroExcludeRereleases && item.IsRerelease)
                return (false, LocalizedMessage.Create("reason.rerelease_excluded", ("tag", item.RereleaseTag ?? "Re-release")));

            return (true, null);
        }

        // --- TOSEC (TNC) Filter Pipeline ---
        return EvaluateTosec(item, config);
    }

    /// <summary>
    /// Why the cabinet of a MAME machine does not suit the options: orientation, screen, players, controls the panel
    /// lacks, buttons. A value the DAT does not give never excludes the machine.
    /// </summary>
    private static LocalizedMessage? EvaluateCabinet(MameCabinet cabinet, FilterConfig config)
    {
        var orientationWanted = config.MameOrientation switch
        {
            MameOrientationRequirement.Horizontal => MameOrientation.Horizontal,
            MameOrientationRequirement.Vertical => MameOrientation.Vertical,
            _ => MameOrientation.Unknown
        };
        if (orientationWanted != MameOrientation.Unknown &&
            cabinet.Orientation != MameOrientation.Unknown &&
            cabinet.Orientation != orientationWanted)
        {
            return LocalizedMessage.Create("reason.mame_orientation_excluded", ("orientation", cabinet.Orientation.ToMessage()));
        }

        if ((config.MameExcludeVector && cabinet.Screen == MameScreenType.Vector) ||
            (config.MameExcludeLcd && cabinet.Screen == MameScreenType.Lcd))
        {
            return LocalizedMessage.Create("reason.mame_screen_excluded", ("screen", cabinet.Screen.ToMessage()));
        }

        if (config.MameMinimumPlayers > 0 && cabinet.Players > 0 && cabinet.Players < config.MameMinimumPlayers)
        {
            return LocalizedMessage.Create("reason.mame_players_excluded",
                ("players", cabinet.Players),
                ("minimum", config.MameMinimumPlayers));
        }

        var lacking = cabinet.Controls & config.MameExcludedControls;
        if (lacking != MameControls.None)
        {
            var kind = EnumExtensions.MameControlKinds.First(k => lacking.HasFlag(k));
            return LocalizedMessage.Create("reason.mame_control_excluded", ("control", kind.ToMessage()));
        }

        if (config.MameMaximumButtons > 0 && cabinet.Buttons > config.MameMaximumButtons)
        {
            return LocalizedMessage.Create("reason.mame_buttons_excluded",
                ("buttons", cabinet.Buttons),
                ("maximum", config.MameMaximumButtons));
        }

        return null;
    }

    /// <summary>Why the category of a MAME machine does not suit the options: for adults, not arcade, excluded genre.</summary>
    private static LocalizedMessage? EvaluateCategory(MameCategory category, FilterConfig config)
    {
        if (config.MameExcludeMature && category.IsMature)
        {
            return LocalizedMessage.Create("reason.mame_mature_excluded");
        }

        if (config.MameArcadeOnly && category.IsArcade == false)
        {
            return LocalizedMessage.Create("reason.mame_not_arcade_excluded", ("category", category.Name));
        }

        return config.MameExcludedGenres.Contains(category.Genre, StringComparer.OrdinalIgnoreCase)
            ? LocalizedMessage.Create("reason.mame_genre_excluded", ("genre", category.Genre))
            : null;
    }

    /// <summary>Why the rating of a MAME machine does not suit the options: not rated, or rated below the minimum.</summary>
    private static LocalizedMessage? EvaluateRating(MameRating rating, FilterConfig config)
    {
        if (rating.IsUnrated)
        {
            return config.MameExcludeUnrated ? LocalizedMessage.Create("reason.mame_unrated_excluded") : null;
        }

        return config.MameMinimumRating > 0 && rating.Score < config.MameMinimumRating
            ? LocalizedMessage.Create("reason.mame_rating_excluded", ("rating", rating.Name), ("minimum", config.MameMinimumRating))
            : null;
    }

    private static (bool IsKept, LocalizedMessage? Reason) EvaluateTosec(ParsedRomName item, FilterConfig config)
    {
        // --- TOSEC (TNC) Filter Pipeline ---
        // An Amiga release that needs a later chipset than the target machine has does not run on it
        var chipset = AmigaChipsets.Of(item);
        if (!AmigaChipsets.RunsOn(chipset, config.AmigaTarget))
        {
            return (false, LocalizedMessage.Create("reason.amiga_chipset_excluded",
                ("chipset", item.System ?? string.Empty),
                ("target", config.AmigaTarget.ToMessage())));
        }

        if (config.DumpsVerifiedOnly)
        {
            if (!item.IsVerified)
                return (false, LocalizedMessage.Create("reason.dump_not_verified"));
        }
        else
        {
            if (config.ExcludeBadDumps && item.IsBadDump)
                return (false, LocalizedMessage.Create("reason.bad_dump_excluded"));
            if (config.ExcludeAlternate && item.IsAlternate)
                return (false, LocalizedMessage.Create("reason.alternate_dump_excluded"));
            if (config.ExcludeOverUnder && item.IsOverUnder)
                return (false, LocalizedMessage.Create("reason.over_under_excluded"));
            if (config.ExcludeVirus && item.IsVirus)
                return (false, LocalizedMessage.Create("reason.virus_excluded"));
        }

        var skipModifications = config.OneGameOneRom && config.IgnoreModificationsInPriority;
        if (!skipModifications)
        {
            if (config.ExcludeModifications &&
                (item.IsCrack || item.IsTrainer || item.IsHack || item.IsPirate || item.IsFix || item.IsModified))
            {
                if (item.IsCrack) return (false, LocalizedMessage.Create("reason.crack_excluded"));
                if (item.IsTrainer) return (false, LocalizedMessage.Create("reason.trainer_excluded"));
                if (item.IsHack) return (false, LocalizedMessage.Create("reason.hack_excluded"));
                if (item.IsPirate) return (false, LocalizedMessage.Create("reason.pirate_excluded"));
                if (item.IsFix) return (false, LocalizedMessage.Create("reason.fix_excluded"));
                return (false, LocalizedMessage.Create("reason.modified_excluded"));
            }

            if (config.ExcludeTranslations && item.IsTranslation)
                return (false, LocalizedMessage.Create("reason.translation_excluded"));

            if (config.ExcludeDevVersions && !string.IsNullOrEmpty(item.DevStatus))
                return (false, LocalizedMessage.Create("reason.dev_version_excluded", ("status", item.DevStatus)));

            if (config.ExcludeFreePd && !string.IsNullOrEmpty(item.Copyright))
                return (false, LocalizedMessage.Create("reason.free_pd_excluded", ("copyright", item.Copyright)));
        }

        if (config.ExcludeDemos && !string.IsNullOrEmpty(item.Demo))
            return (false, LocalizedMessage.Create("reason.demo_excluded"));

        return (true, null);
    }
}
