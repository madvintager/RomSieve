using System.Text.Json;
using RomSieve.App;
using RomSieve.App.Abstractions;
using RomSieve.App.ViewModels;
using RomSieve.Core.Models;
using RomSieve.Core.Tests.Fixtures;

namespace RomSieve.App.Tests;

/// <summary>
/// Filters tab: one convention shown at a time, shared categories acting on the options of the shown convention,
/// dump quality level summing up the integrity options, and the saved tab numbering of the former two filter tabs.
/// </summary>
public sealed class FiltersViewModelTests
{
    private static MainViewModel CreateViewModel(TempDirectoryFixture tmp, string json = "{}") =>
        TestMainViewModel.Create(new HeadlessDialogService(), tmp.WriteText("config.json", json));

    [Fact]
    public void ShownConvention_ShouldFollowTheEffectiveConventionAndBeSwitchableByHand()
    {
        using var tmp = new TempDirectoryFixture();
        var vm = CreateViewModel(tmp);
        var filters = vm.Filters;

        filters.IsTosecShown.Should().BeTrue();

        vm.Directories.SelectedNamingConvention = NamingConvention.NoIntro;
        filters.IsNoIntroShown.Should().BeTrue();
        vm.FilterSubtabIndex.Should().Be(FiltersViewModel.NoIntroConventionIndex);

        // Shown by hand: the effective convention stays No-Intro, but the TOSEC options can be set
        filters.IsTosecShown = true;
        filters.IsTosecShown.Should().BeTrue();
        filters.IsNoIntroShown.Should().BeFalse();

        vm.Directories.SelectedNamingConvention = NamingConvention.Redump;
        filters.IsNoIntroShown.Should().BeTrue();

        // An unknown convention keeps the last shown one
        vm.Directories.SelectedNamingConvention = NamingConvention.Auto;
        filters.IsNoIntroShown.Should().BeTrue();

        vm.Directories.SelectedNamingConvention = NamingConvention.Tosec;
        filters.IsTosecShown.Should().BeTrue();

        // A radio button that gets unchecked writes false, which changes nothing; an unknown index is ignored
        filters.IsNoIntroShown = false;
        filters.IsTosecShown.Should().BeTrue();
        vm.FilterSubtabIndex = 7;
        filters.ShownConventionIndex.Should().Be(FiltersViewModel.TosecConventionIndex);
    }

    [Fact]
    public void SharedCategory_ShouldWriteTheOptionOfTheShownConventionOnly()
    {
        using var tmp = new TempDirectoryFixture();
        var vm = CreateViewModel(tmp);
        var filters = vm.Filters;
        var notified = new List<string?>();
        filters.PropertyChanged += (_, e) => notified.Add(e.PropertyName);

        // A box keeps its category: it writes the opposite of the exclude_* option
        filters.IsTosecShown = true;
        filters.KeepTranslations = false;
        filters.KeepModifications = true;
        filters.CompleteSetsOnly = true;
        vm.TosecFilters.ExcludeTranslations.Should().BeTrue();
        vm.TosecFilters.ExcludeModifications.Should().BeFalse();
        vm.TosecFilters.CompleteMultidiskSetsOnly.Should().BeTrue();
        vm.NoIntroFilters.ExcludeTranslations.Should().BeFalse();
        vm.NoIntroFilters.ExcludeHack.Should().BeTrue();
        vm.NoIntroFilters.CompleteMultimediaSetsOnly.Should().BeFalse();
        filters.TranslationsTag.Should().Be("[tr]");
        filters.ModificationsTag.Should().Be("[cr] [t] [h] [p] [f] [m]");

        filters.IsNoIntroShown = true;
        filters.KeepTranslations.Should().BeTrue("the No-Intro option is shown now");
        filters.KeepModifications.Should().BeFalse();
        filters.TranslationsTag.Should().Be("(Translated)");
        filters.ModificationsTag.Should().Be("(Hack)");
        filters.TranslationsTooltip.Should().Contain("(Translated...)");
        notified.Should().Contain(nameof(FiltersViewModel.KeepTranslations));

        filters.KeepModifications = true;
        filters.KeepDemos = false;
        filters.KeepDevVersions = false;
        vm.NoIntroFilters.ExcludeHack.Should().BeFalse();
        vm.NoIntroFilters.ExcludePirate.Should().BeTrue("pirates have their own No-Intro option");
        vm.NoIntroFilters.ExcludeDemos.Should().BeTrue();
        vm.NoIntroFilters.ExcludeDevVersions.Should().BeTrue();
        vm.TosecFilters.ExcludeModifications.Should().BeFalse("set before, while TOSEC was shown");
        vm.TosecFilters.ExcludeDemos.Should().BeFalse();
        vm.TosecFilters.ExcludeDevVersions.Should().BeFalse();

        // A change of the shown section reaches the shared category
        notified.Clear();
        vm.NoIntroFilters.ExcludeTranslations = true;
        filters.KeepTranslations.Should().BeFalse();
        notified.Should().Contain(nameof(FiltersViewModel.KeepTranslations));

        // The tags follow the language
        filters.IsTosecShown = true;
        vm.UiLocale = "en";
        filters.TranslationsTooltip.Should().Be(vm.Localization.Tr("filters.tooltip_keep_tosec_translations"));
    }

    [Fact]
    public void ConventionOptions_ShouldBeKeptByTheirBoxAndWriteTheOppositeOption()
    {
        using var tmp = new TempDirectoryFixture();
        var vm = CreateViewModel(tmp);
        var filters = vm.Filters;

        // The defaults read as what is kept: modified versions are excluded, the rest is kept
        filters.IsTosecShown = true;
        filters.KeepModifications.Should().BeFalse();
        filters.KeepTranslations.Should().BeTrue();
        filters.KeepPublicDomain.Should().Be(!vm.TosecFilters.ExcludeFreePd);

        filters.KeepPublicDomain = false;
        vm.TosecFilters.ExcludeFreePd.Should().BeTrue();

        filters.IsNoIntroShown = true;
        foreach (var (keep, exclude) in new (Action<bool> Keep, Func<bool> Exclude)[]
                 {
                     (v => filters.KeepPirate = v, () => vm.NoIntroFilters.ExcludePirate),
                     (v => filters.KeepUnlicensed = v, () => vm.NoIntroFilters.ExcludeUnlicensed),
                     (v => filters.KeepRereleases = v, () => vm.NoIntroFilters.ExcludeRereleases)
                 })
        {
            keep(false);
            exclude().Should().BeTrue();
            keep(true);
            exclude().Should().BeFalse();
        }
    }

    [Fact]
    public async Task KeptCategories_ShouldBeSavedUnderTheirExcludeKeys()
    {
        // The interface says what is kept, config.json what is excluded: a file is read and saved again as it was
        using var tmp = new TempDirectoryFixture();
        var vm = CreateViewModel(tmp, """
            { "exclude_translations": true, "exclude_modifications": false, "nointro_exclude_pirate": false }
            """);
        vm.Filters.IsTosecShown = true;
        vm.Filters.KeepTranslations.Should().BeFalse();
        vm.Filters.KeepModifications.Should().BeTrue();
        vm.Filters.IsNoIntroShown = true;
        vm.Filters.KeepPirate.Should().BeTrue();

        await vm.SaveConfigurationAsync();
        using var saved = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(tmp.Path, "config.json")));
        saved.RootElement.GetProperty("exclude_translations").GetBoolean().Should().BeTrue();
        saved.RootElement.GetProperty("exclude_modifications").GetBoolean().Should().BeFalse();
        saved.RootElement.GetProperty("nointro_exclude_pirate").GetBoolean().Should().BeFalse();
    }

    [Theory]
    [InlineData(DumpQualityLevel.All, false, false, false, false, false)]
    [InlineData(DumpQualityLevel.NoBadDumps, false, true, true, true, false)]
    public void TosecDumpLevel_ShouldRewriteTheIntegrityOptions(
        DumpQualityLevel level, bool verifiedOnly, bool bad, bool overUnder, bool virus, bool alternate)
    {
        var tosec = new TosecFiltersViewModel(new RomSieve.Core.Services.LocalizationService());
        tosec.ExcludeAlternate = true;
        tosec.DumpsVerifiedOnly = true;

        tosec.DumpLevel = level;

        tosec.DumpLevel.Should().Be(level);
        tosec.DumpsVerifiedOnly.Should().Be(verifiedOnly);
        tosec.ExcludeBadDumps.Should().Be(bad);
        tosec.ExcludeOverUnder.Should().Be(overUnder);
        tosec.ExcludeVirus.Should().Be(virus);
        tosec.ExcludeAlternate.Should().Be(alternate);
    }

    [Fact]
    public void DumpLevel_ShouldSumUpTheIntegrityOptions()
    {
        var tosec = new TosecFiltersViewModel(new RomSieve.Core.Services.LocalizationService());
        var noIntro = new NoIntroFiltersViewModel();

        tosec.DumpLevel.Should().Be(DumpQualityLevel.NoBadDumps, "the defaults are the 'no bad dumps' level");
        noIntro.DumpLevel.Should().Be(DumpQualityLevel.NoBadDumps);

        // Verified only keeps the exclusions, which it implies
        tosec.ExcludeAlternate = true;
        tosec.DumpLevel.Should().Be(DumpQualityLevel.Custom);
        tosec.DumpLevel = DumpQualityLevel.VerifiedOnly;
        tosec.DumpsVerifiedOnly.Should().BeTrue();
        tosec.ExcludeAlternate.Should().BeTrue();
        tosec.DumpLevel.Should().Be(DumpQualityLevel.VerifiedOnly);

        // Custom cannot be chosen
        tosec.DumpLevel = DumpQualityLevel.Custom;
        tosec.DumpLevel.Should().Be(DumpQualityLevel.VerifiedOnly);

        noIntro.DumpLevel = DumpQualityLevel.All;
        noIntro.ExcludeBadDumps.Should().BeFalse();
        noIntro.ExcludeAlternate.Should().BeFalse();
        noIntro.ExcludeAlternate = true;
        noIntro.DumpLevel.Should().Be(DumpQualityLevel.Custom);
        noIntro.DumpLevel = DumpQualityLevel.NoBadDumps;
        noIntro.ExcludeBadDumps.Should().BeTrue();
        noIntro.ExcludeAlternate.Should().BeFalse();

        // No-Intro and Redump names never mark a verified dump: the level does not exist for them
        noIntro.DumpLevel = DumpQualityLevel.VerifiedOnly;
        noIntro.DumpLevel.Should().Be(DumpQualityLevel.NoBadDumps);
    }

    [Fact]
    public void DumpLevelChoices_ShouldActOnTheShownConventionAndShowACustomSetting()
    {
        using var tmp = new TempDirectoryFixture();
        var vm = CreateViewModel(tmp);
        var filters = vm.Filters;

        filters.IsDumpLevelNoBadDumps.Should().BeTrue();
        filters.IsDumpLevelCustom.Should().BeFalse();
        filters.IsDumpLevelVerifiedOnlyShown.Should().BeTrue();

        filters.IsDumpLevelAll = true;
        vm.TosecFilters.ExcludeBadDumps.Should().BeFalse();
        vm.NoIntroFilters.ExcludeBadDumps.Should().BeTrue("the No-Intro options are not shown");

        // The radio button being unchecked writes false: nothing changes
        filters.IsDumpLevelAll = false;
        filters.IsDumpLevelAll.Should().BeTrue();

        vm.TosecFilters.ExcludeVirus = true;
        filters.IsDumpLevelCustom.Should().BeTrue();
        filters.IsDumpLevelAll.Should().BeFalse();
        filters.IsDumpLevelNoBadDumps.Should().BeFalse();
        filters.IsDumpLevelVerifiedOnly.Should().BeFalse();

        filters.IsNoIntroShown = true;
        filters.IsDumpLevelNoBadDumps.Should().BeTrue();
        filters.IsDumpLevelVerifiedOnlyShown.Should().BeFalse("a No-Intro name never marks a verified dump");
        filters.IsDumpLevelVerifiedOnly = true;
        filters.IsDumpLevelNoBadDumps.Should().BeTrue();
        vm.TosecFilters.DumpsVerifiedOnly.Should().BeFalse();
    }

    [Fact]
    public async Task CustomIntegrityOptions_ShouldBeReadAndSavedUnchanged()
    {
        using var tmp = new TempDirectoryFixture();
        var vm = CreateViewModel(tmp, """
            {
              "dumps_verified_only": false, "exclude_bad_dumps": true, "exclude_over_under": false,
              "exclude_virus": true, "exclude_alternate": true,
              "nointro_exclude_bad_dumps": false, "nointro_exclude_alternate": true
            }
            """);

        vm.TosecFilters.DumpLevel.Should().Be(DumpQualityLevel.Custom);
        vm.NoIntroFilters.DumpLevel.Should().Be(DumpQualityLevel.Custom);

        await vm.SaveConfigurationAsync();
        using var saved = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(tmp.Path, "config.json")));
        var root = saved.RootElement;
        root.GetProperty("dumps_verified_only").GetBoolean().Should().BeFalse();
        root.GetProperty("exclude_bad_dumps").GetBoolean().Should().BeTrue();
        root.GetProperty("exclude_over_under").GetBoolean().Should().BeFalse();
        root.GetProperty("exclude_virus").GetBoolean().Should().BeTrue();
        root.GetProperty("exclude_alternate").GetBoolean().Should().BeTrue();
        root.GetProperty("nointro_exclude_bad_dumps").GetBoolean().Should().BeFalse();
        root.GetProperty("nointro_exclude_alternate").GetBoolean().Should().BeTrue();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public async Task ShownConvention_ShouldBeSavedAndRestored(int filterConvention)
    {
        using var tmp = new TempDirectoryFixture();
        var vm = CreateViewModel(tmp, $$"""{ "filter_subtab_index": {{filterConvention}} }""");

        vm.Navigation.CurrentStep.Should().Be(WorkflowStep.Collection, "the application always opens on the collection");
        vm.FilterSubtabIndex.Should().Be(filterConvention);

        await vm.SaveConfigurationAsync();
        using var saved = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(tmp.Path, "config.json")));
        saved.RootElement.GetProperty("filter_subtab_index").GetInt32().Should().Be(filterConvention);
    }

    [Fact]
    public async Task MameOptions_ShouldBeShownForMameAndSavedAsTheOppositeOfTheirExclusions()
    {
        using var tmp = new TempDirectoryFixture();
        var vm = CreateViewModel(tmp);
        var filters = vm.Filters;

        filters.AreMameOptionsShown.Should().BeFalse();
        vm.Directories.SelectedNamingConvention = NamingConvention.Mame;
        filters.AreMameOptionsShown.Should().BeTrue();
        filters.IsNoIntroShown.Should().BeTrue();

        // Shown with the No-Intro / Redump options only
        filters.IsTosecShown = true;
        filters.AreMameOptionsShown.Should().BeFalse();
        filters.IsNoIntroShown = true;

        filters.KeepMameBios.Should().BeTrue();
        filters.KeepMameDevices = false;
        filters.KeepMameMechanical = false;
        filters.KeepMameDriverPreliminary = false;
        filters.MameKeepPlayable = true;
        filters.KeepMameUnemulatedSound.Should().BeTrue();
        filters.KeepMameUnemulatedSound = false;
        filters.KeepMameRequiresArtwork.Should().BeTrue();
        filters.KeepMameRequiresArtwork = false;
        filters.KeepMameAlternateSets.Should().BeTrue();
        filters.KeepMameAlternateSets = false;
        filters.KeepMameNothingToDump.Should().BeFalse("the machines with nothing to dump are left out by default");
        filters.KeepMameNothingToDump = true;
        vm.NoIntroFilters.ExcludeMameNothingToDump.Should().BeFalse();
        vm.NoIntroFilters.ExcludeMameAlternateSets.Should().BeTrue();
        vm.NoIntroFilters.ExcludeMameRequiresArtwork.Should().BeTrue();
        vm.NoIntroFilters.ExcludeMameDevices.Should().BeTrue();
        vm.NoIntroFilters.ExcludeMameUnemulatedSound.Should().BeTrue();
        vm.NoIntroFilters.MameDriverStatuses.Should().Be(MameDriverStatuses.Good | MameDriverStatuses.Imperfect);
        vm.NoIntroFilters.MameKeepPlayable.Should().BeTrue();

        await vm.SaveConfigurationAsync();
        using (var saved = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(tmp.Path, "config.json"))))
        {
            saved.RootElement.GetProperty("mame_exclude_bios").GetBoolean().Should().BeFalse();
            saved.RootElement.GetProperty("mame_exclude_devices").GetBoolean().Should().BeTrue();
            saved.RootElement.GetProperty("mame_exclude_mechanical").GetBoolean().Should().BeTrue();
            saved.RootElement.GetProperty("mame_driver_statuses").EnumerateArray().Select(e => e.GetString()).Should().Equal("good", "imperfect");
            saved.RootElement.GetProperty("mame_keep_playable").GetBoolean().Should().BeTrue();
            saved.RootElement.GetProperty("mame_exclude_unemulated_sound").GetBoolean().Should().BeTrue();
            saved.RootElement.GetProperty("mame_exclude_requires_artwork").GetBoolean().Should().BeTrue();
            saved.RootElement.GetProperty("mame_exclude_alternate_sets").GetBoolean().Should().BeTrue();
            saved.RootElement.GetProperty("mame_exclude_nothing_to_dump").GetBoolean().Should().BeFalse();
        }

        var reloaded = TestMainViewModel.Create(new HeadlessDialogService(), Path.Combine(tmp.Path, "config.json"));
        reloaded.Filters.KeepMameDevices.Should().BeFalse();
        reloaded.Filters.KeepMameDriverGood.Should().BeTrue();
        reloaded.Filters.KeepMameDriverPreliminary.Should().BeFalse();
        reloaded.Filters.MameKeepPlayable.Should().BeTrue();
        reloaded.Filters.KeepMameUnemulatedSound.Should().BeFalse();
        reloaded.Filters.KeepMameRequiresArtwork.Should().BeFalse();
        reloaded.Filters.KeepMameAlternateSets.Should().BeFalse();
        reloaded.BuildConfigUnchecked().Filters.MameExcludeMechanical.Should().BeTrue();
    }
}
