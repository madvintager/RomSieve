using RomSieve.Core.Models;
using RomSieve.Core.Services;
using RomSieve.Core.Tests.Fixtures;

namespace RomSieve.Core.Tests;

/// <summary>
/// Filters of MAME machines (BIOS, devices, mechanical machines, external artwork, driver statuses, unemulated sound,
/// alternate sets),
/// on the machines of the
/// <c>-listxml</c> extract of <c>Fixtures/Mame</c>, and their options in <c>config.json</c>.
/// </summary>
public sealed class MameFilterTests
{
    private static readonly DatCatalog Catalog = new CoreTestServices().DatService
        .ParseDatFileAsync(Path.Combine(AppContext.BaseDirectory, "Fixtures", "Mame", "listxml.dat"))
        .GetAwaiter().GetResult();

    private readonly CoreTestServices _services = new();

    // A preliminary machine whose own emulation is good: its sound is not emulated (mostly so in MAME 0.289)
    private static readonly DatGame SilentMachine = new()
    {
        Name = "silent",
        Description = "Silent Game (World)",
        Roms = [new DatRomEntry("silent.bin", 16, "12345678")],
        DriverStatus = MameDriverStatus.Preliminary,
        EmulationStatus = MameDriverStatus.Good,
        UnemulatedFeatures = MameFeatures.Sound | MameFeatures.Lan
    };

    private (bool IsKept, string? Reason) Evaluate(string machine, Action<FilterConfig> configure) =>
        Evaluate(machine == SilentMachine.Name ? SilentMachine : Catalog.GameByName[machine], configure);

    private const MameDriverStatuses GoodOrImperfect = MameDriverStatuses.Good | MameDriverStatuses.Imperfect;

    private static void Keep(FilterConfig filters, MameDriverStatuses statuses, bool keepPlayable = false) =>
        (filters.MameDriverStatuses, filters.MameKeepPlayable) = (statuses, keepPlayable);

    private (bool IsKept, string? Reason) Evaluate(DatGame game, Action<FilterConfig> configure)
    {
        var filters = new FilterConfig();
        configure(filters);
        var (isKept, reason) = _services.FilterService.EvaluateFilter(new MameDescriptionParser().ParseGame(game), filters);
        return (isKept, reason is null ? null : _services.I18n.Format(reason));
    }

    [Fact]
    public void ParseGame_ShouldCarryWhatTheDatSaysOfTheMachine()
    {
        new MameDescriptionParser().ParseGame(Catalog.GameByName["neogeo"]).Machine
            .Should().Be(new MameMachineInfo(IsBios: true, IsDevice: false, IsMechanical: false, MameDriverStatus.Good, MameDriverStatus.Good,
                Cabinet: Catalog.GameByName["neogeo"].Cabinet));
        new MameDescriptionParser().ParseGame(Catalog.GameByName["hd44780"]).Machine!.IsDevice.Should().BeTrue();
        new MameDescriptionParser().ParseGame(Catalog.GameByName["3lilpigs"]).Machine
            .Should().Be(new MameMachineInfo(false, false, IsMechanical: true, MameDriverStatus.Preliminary, MameDriverStatus.Preliminary,
                Cabinet: Catalog.GameByName["3lilpigs"].Cabinet));
        new MameDescriptionParser().ParseGame(Catalog.GameByName["timecrs4"]).Machine!.UnemulatedFeatures
            .Should().Be(MameFeatures.Sound);
    }

    [Fact]
    public void DefaultFilters_ShouldKeepEveryKindOfMachine()
    {
        foreach (var machine in new[] { "neogeo", "hd44780", "3lilpigs", "aafbb", "sf2", "timecrs4", "silent" })
        {
            Evaluate(machine, _ => { }).IsKept.Should().BeTrue(machine);
        }
    }

    [Fact]
    public void BiosDevicesAndMechanicalMachines_ShouldBeExcludedOnDemand()
    {
        Evaluate("neogeo", f => f.MameExcludeBios = true).Should().Be((false, "BIOS MAME exclu"));
        Evaluate("hd44780", f => f.MameExcludeDevices = true).Should().Be((false, "Device MAME exclu"));
        Evaluate("3lilpigs", f => f.MameExcludeMechanical = true).Should().Be((false, "Machine mécanique exclue"));

        // Each option only concerns its kind of machine
        Evaluate("sf2", f =>
        {
            f.MameExcludeBios = true;
            f.MameExcludeDevices = true;
            f.MameExcludeMechanical = true;
        }).IsKept.Should().BeTrue();
    }

    [Fact]
    public async Task MameDatSummary_ShouldTellWhetherTheDriverStatusesLeaveAMechanicalMachine()
    {
        // 3lilpigs, the only mechanical machine of the extract, is preliminary, as all of them in MAME 0.289
        var summary = MameDatSummary.FromCatalog(Catalog)!;
        summary.KeepsNoMechanicalMachine(MameDriverStatuses.All, keepPlayable: false).Should().BeFalse();
        summary.KeepsNoMechanicalMachine(GoodOrImperfect, keepPlayable: true).Should().BeTrue();
        summary.KeepsNoMechanicalMachine(GoodOrImperfect, keepPlayable: false).Should().BeTrue();
        summary.KeepsNoMechanicalMachine(MameDriverStatuses.Good, keepPlayable: false).Should().BeTrue();

        // A SabreTools DAT gives no driver status: such a machine is always kept
        var sabreTools = await _services.DatService
            .ParseDatFileAsync(Path.Combine(AppContext.BaseDirectory, "Fixtures", "Mame", "sabretools_nonmerged.dat"));
        MameDatSummary.FromCatalog(sabreTools)!.KeepsNoMechanicalMachine(MameDriverStatuses.Good, keepPlayable: false).Should().BeFalse();
    }

    [Fact]
    public void MameDatSummary_ShouldOnlyCountTheMechanicalMachinesThatHaveSomethingToDump()
    {
        var catalog = new DatCatalog();
        catalog.AddGame(new DatGame { Name = "sf2", Roms = [new DatRomEntry("sf2.11e", 16, "fe39ee33")], DriverStatus = MameDriverStatus.Good });
        MameDatSummary.FromCatalog(catalog).Should().BeNull("the DAT has no mechanical machine");

        // A machine whose ROMs were never dumped cannot be in a collection
        catalog.AddGame(new DatGame
        {
            Name = "undumped",
            IsMechanical = true,
            DriverStatus = MameDriverStatus.Good,
            Roms = [new DatRomEntry("undumped.bin", 16, string.Empty) { Status = DatRomStatus.NoDump }]
        });
        MameDatSummary.FromCatalog(catalog).Should().BeNull();

        catalog.AddGame(new DatGame
        {
            Name = "pinball",
            IsMechanical = true,
            DriverStatus = MameDriverStatus.Imperfect,
            Roms = [new DatRomEntry("pinball.bin", 16, "12345678")]
        });
        var summary = MameDatSummary.FromCatalog(catalog)!;
        summary.KeepsNoMechanicalMachine(GoodOrImperfect, keepPlayable: false).Should().BeFalse();
        summary.KeepsNoMechanicalMachine(MameDriverStatuses.Good, keepPlayable: false).Should().BeTrue();
    }

    [Fact]
    public void InsufficientEmulation_ShouldSayTheStatus()
    {
        Evaluate("aafbb", f => Keep(f, MameDriverStatuses.Good))
            .Should().Be((false, "Statut d'émulation non gardé (préliminaire)"));
    }

    [Fact]
    public void MachinesWithNothingToDump_ShouldBeExcludedByDefault()
    {
        var console = new DatGame { Name = "console", Description = "Console (USA)", DriverStatus = MameDriverStatus.Good };
        var undumped = new DatGame
        {
            Name = "undumped",
            Description = "Undumped",
            DriverStatus = MameDriverStatus.Good,
            Roms = [new DatRomEntry("undumped.bin", 16, string.Empty) { Status = DatRomStatus.NoDump }]
        };
        var diskOnly = new DatGame
        {
            Name = "disk",
            Description = "Disk Game",
            DriverStatus = MameDriverStatus.Good,
            Roms = [new DatRomEntry("disk", 0, string.Empty, Sha1: ChdTestFile.Sha1Of("disk")) { IsDisk = true }]
        };

        Evaluate(console, _ => { }).Should().Be((false, "Machine sans ROM à dumper exclue"));
        Evaluate(undumped, _ => { }).Should().Be((false, "Machine sans ROM à dumper exclue"));
        Evaluate(diskOnly, _ => { }).IsKept.Should().BeTrue("a disk is something to dump");
        Evaluate(console, f => f.MameExcludeNothingToDump = false).IsKept.Should().BeTrue();

        // Only the MAME sets are concerned
        new MameDescriptionParser().ParseGame(Catalog.GameByName["sf2"]).Machine!.HasNothingToDump.Should().BeFalse();
    }

    [Fact]
    public void NothingToDumpOption_ShouldBeSavedAndExcludeByDefault()
    {
        new SortSettings().MameExcludeNothingToDump.Should().BeTrue();
        SortSettingsSerializer.Read(new Dictionary<string, object?>()).MameExcludeNothingToDump.Should().BeTrue("a configuration without the key excludes them");

        var data = new Dictionary<string, object?>();
        SortSettingsSerializer.Write(new SortSettings { MameExcludeNothingToDump = false }, data);
        data["mame_exclude_nothing_to_dump"].Should().Be(false);
        SortSettingsSerializer.Read(data).MameExcludeNothingToDump.Should().BeFalse();
        SortSettingsSerializer.ToSortConfig(new SortSettings { MameExcludeNothingToDump = false }).Filters.MameExcludeNothingToDump.Should().BeFalse();
    }

    [Fact]
    public void MachinesNeedingArtwork_ShouldBeExcludedOnDemand()
    {
        var handheld = new DatGame
        {
            Name = "handheld",
            Description = "Handheld Game",
            Roms = [new DatRomEntry("handheld.bin", 16, "12345678")],
            DriverStatus = MameDriverStatus.Good,
            EmulationStatus = MameDriverStatus.Good,
            RequiresArtwork = true
        };

        new MameDescriptionParser().ParseGame(handheld).Machine!.RequiresArtwork.Should().BeTrue();
        Evaluate(handheld, _ => { }).IsKept.Should().BeTrue("every machine is kept by default");
        Evaluate(handheld, f => f.MameExcludeRequiresArtwork = true).Should().Be((false, "Habillage externe requis"));
        Evaluate("sf2", f => f.MameExcludeRequiresArtwork = true).IsKept.Should().BeTrue();
    }

    [Fact]
    public void AlternateSets_ShouldBeExcludedByTheirOwnOptionOnly()
    {
        var main = new DatGame { Name = "pacman", Description = "Pac-Man (Midway, set 1)", Roms = [new DatRomEntry("pacman.6e", 16, "c1e6ab10")] };
        var second = new DatGame { Name = "pacmanf", Description = "Pac-Man (Midway, set 2)", Roms = [new DatRomEntry("pacman.6e", 16, "c1e6ab10")] };
        var alternate = new DatGame { Name = "pacmana", Description = "Pac-Man (Midway, alt)", Roms = [new DatRomEntry("pacman.6e", 16, "c1e6ab10")] };

        Evaluate(second, _ => { }).IsKept.Should().BeTrue("every machine is kept by default");
        Evaluate(second, f => f.MameExcludeAlternateSets = true).Should().Be((false, "Variante MAME exclue (set 2, alt)"));
        Evaluate(alternate, f => f.MameExcludeAlternateSets = true).IsKept.Should().BeFalse();
        Evaluate(main, f => f.MameExcludeAlternateSets = true).IsKept.Should().BeTrue("the main set stays");

        // A MAME set has no dump quality: the No-Intro / Redump dump options never apply to it
        Evaluate(second, f =>
        {
            f.NoIntroExcludeAlternate = true;
            f.NoIntroExcludeBadDumps = true;
        }).IsKept.Should().BeTrue();
    }

    [Fact]
    public void AlternateSetsOption_ShouldNotChangeTheNoIntroAndRedumpFilters()
    {
        var alternate = _services.NoIntroParser.ParseRomFilename("Game (USA) (Alt 1).sfc");
        alternate.Convention.Should().Be(NamingConvention.NoIntro);
        alternate.IsAlternate.Should().BeTrue();

        var filters = new FilterConfig { MameExcludeAlternateSets = true };
        _services.FilterService.EvaluateFilter(alternate, filters).IsKept.Should().BeTrue();

        filters = new FilterConfig { NoIntroExcludeAlternate = true };
        _services.FilterService.EvaluateFilter(alternate, filters).IsKept.Should().BeFalse();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AlternateSetsOption_ShouldNotFollowTheNoIntroOptions(bool excludeAlternateSets)
    {
        var data = new Dictionary<string, object?> { ["nointro_exclude_alternate"] = true };
        SortSettingsSerializer.Read(data).MameExcludeAlternateSets.Should().BeFalse("a missing option takes its default value");

        data["mame_exclude_alternate_sets"] = excludeAlternateSets;
        data["nointro_exclude_alternate"] = !excludeAlternateSets;
        SortSettingsSerializer.Read(data).MameExcludeAlternateSets.Should().Be(excludeAlternateSets);
    }

    [Fact]
    public void Playable_ShouldFallBackOnTheDriverStatusWithoutEmulationStatus()
    {
        // A DAT without the emulation attribute: an imperfect machine is playable, a preliminary one is not
        var imperfect = new DatGame { Name = "a", Description = "A", DriverStatus = MameDriverStatus.Imperfect, Roms = [new DatRomEntry("a.bin", 16, "12345678")] };
        var preliminary = new DatGame { Name = "b", Description = "B", DriverStatus = MameDriverStatus.Preliminary, Roms = [new DatRomEntry("b.bin", 16, "12345678")] };

        Evaluate(imperfect, f => Keep(f, GoodOrImperfect, keepPlayable: true)).IsKept.Should().BeTrue();
        Evaluate(preliminary, f => Keep(f, GoodOrImperfect, keepPlayable: true))
            .Should().Be((false, "Statut d'émulation non gardé (préliminaire)"));
    }

    [Theory]
    [InlineData("timecrs4", false)]
    [InlineData("silent", false)]
    [InlineData("sf2", true)]
    [InlineData("airrace", true)]
    [InlineData("hd44780", true)]
    public void UnemulatedSound_ShouldBeExcludedOnDemand(string machine, bool isKept)
    {
        // airrace has no sound hardware: it lacks no emulated sound
        var result = Evaluate(machine, f => f.MameExcludeUnemulatedSound = true);

        result.IsKept.Should().Be(isKept);
        if (!isKept)
        {
            result.Reason.Should().Be("Son non émulé");
        }
    }

    [Fact]
    public void PlayableWithSound_ShouldCombineBothOptions()
    {
        static void Configure(FilterConfig f)
        {
            Keep(f, GoodOrImperfect, keepPlayable: true);
            f.MameExcludeUnemulatedSound = true;
        }

        Evaluate("silent", Configure).Should().Be((false, "Son non émulé"));
        Evaluate("airrace", Configure).IsKept.Should().BeTrue();
        Evaluate("aafbb", Configure).IsKept.Should().BeFalse();
    }

    [Fact]
    public void OtherConventions_ShouldIgnoreTheMameOptions()
    {
        var parsed = _services.NoIntroParser.ParseRomFilename("Super Game (USA).sfc");
        var filters = new FilterConfig
        {
            MameExcludeBios = true,
            MameExcludeDevices = true,
            MameExcludeMechanical = true,
            MameDriverStatuses = MameDriverStatuses.Good,
            MameExcludeUnemulatedSound = true,
            MameExcludeAlternateSets = true
        };

        parsed.Machine.Should().BeNull();
        _services.FilterService.EvaluateFilter(parsed, filters).IsKept.Should().BeTrue();
    }

    [Fact]
    public void Options_ShouldBeSavedReadAndPassedToTheRun()
    {
        var settings = new SortSettings
        {
            MameExcludeBios = true,
            MameExcludeDevices = true,
            MameExcludeMechanical = true,
            MameExcludeRequiresArtwork = true,
            MameDriverStatuses = MameDriverStatuses.Good | MameDriverStatuses.Imperfect,
            MameKeepPlayable = true,
            MameExcludeUnemulatedSound = true,
            MameExcludeAlternateSets = true
        };
        var data = new Dictionary<string, object?>();

        SortSettingsSerializer.Write(settings, data);

        data["mame_exclude_bios"].Should().Be(true);
        data["mame_exclude_devices"].Should().Be(true);
        data["mame_exclude_mechanical"].Should().Be(true);
        data["mame_exclude_requires_artwork"].Should().Be(true);
        data["mame_driver_statuses"].Should().BeEquivalentTo(new[] { "good", "imperfect" }, o => o.WithStrictOrdering());
        data["mame_keep_playable"].Should().Be(true);
        data["mame_exclude_unemulated_sound"].Should().Be(true);
        data["mame_exclude_alternate_sets"].Should().Be(true);

        var read = SortSettingsSerializer.Read(data);
        read.MameDriverStatuses.Should().Be(MameDriverStatuses.Good | MameDriverStatuses.Imperfect);
        read.MameKeepPlayable.Should().BeTrue();
        read.MameExcludeUnemulatedSound.Should().BeTrue();
        read.MameExcludeRequiresArtwork.Should().BeTrue();
        read.MameExcludeMechanical.Should().BeTrue();

        var filters = SortSettingsSerializer.ToSortConfig(read).Filters;
        filters.MameExcludeBios.Should().BeTrue();
        filters.MameExcludeDevices.Should().BeTrue();
        filters.MameDriverStatuses.Should().Be(MameDriverStatuses.Good | MameDriverStatuses.Imperfect);
        filters.MameKeepPlayable.Should().BeTrue();
        filters.MameExcludeUnemulatedSound.Should().BeTrue();
        filters.MameExcludeRequiresArtwork.Should().BeTrue();
        filters.MameExcludeAlternateSets.Should().BeTrue();
    }

    [Fact]
    public void ConfigurationWithoutTheMameOptions_ShouldKeepEveryMachine()
    {
        var read = SortSettingsSerializer.Read(new Dictionary<string, object?>());

        read.MameExcludeBios.Should().BeFalse();
        read.MameExcludeDevices.Should().BeFalse();
        read.MameExcludeMechanical.Should().BeFalse();
        read.MameExcludeUnemulatedSound.Should().BeFalse();
        read.MameExcludeRequiresArtwork.Should().BeFalse();
        read.MameExcludeAlternateSets.Should().BeFalse();
        read.MameDriverStatuses.Should().Be(MameDriverStatuses.All);
        read.MameKeepPlayable.Should().BeFalse();
    }

    [Theory]
    [InlineData(MameDriverStatuses.All, false, "aafbb", true)]
    [InlineData(GoodOrImperfect, true, "aafbb", false)]
    [InlineData(GoodOrImperfect, true, "silent", true)]
    [InlineData(GoodOrImperfect, true, "airrace", true)]
    [InlineData(GoodOrImperfect, true, "sf2", true)]
    [InlineData(GoodOrImperfect, false, "silent", false)]
    [InlineData(GoodOrImperfect, false, "aafbb", false)]
    [InlineData(GoodOrImperfect, false, "airrace", true)]
    [InlineData(MameDriverStatuses.Good, false, "airrace", false)]
    [InlineData(MameDriverStatuses.Good, false, "sf2", true)]
    [InlineData(MameDriverStatuses.Good, false, "hd44780", true)]
    [InlineData(MameDriverStatuses.Preliminary, false, "aafbb", true)]
    [InlineData(MameDriverStatuses.Preliminary, false, "silent", true)]
    [InlineData(MameDriverStatuses.Preliminary, false, "airrace", false)]
    [InlineData(MameDriverStatuses.Preliminary, false, "sf2", false)]
    [InlineData(MameDriverStatuses.Imperfect, false, "airrace", true)]
    [InlineData(MameDriverStatuses.Imperfect, false, "sf2", false)]
    [InlineData(MameDriverStatuses.Imperfect | MameDriverStatuses.Preliminary, false, "sf2", false)]
    [InlineData(MameDriverStatuses.Imperfect | MameDriverStatuses.Preliminary, false, "aafbb", true)]
    [InlineData(MameDriverStatuses.Good | MameDriverStatuses.Preliminary, false, "airrace", false)]
    [InlineData(MameDriverStatuses.Good, true, "airrace", true)]
    [InlineData(MameDriverStatuses.Good, true, "silent", true)]
    [InlineData(MameDriverStatuses.Good, true, "aafbb", false)]
    [InlineData(MameDriverStatuses.None, false, "sf2", false)]
    [InlineData(MameDriverStatuses.None, false, "hd44780", true)]
    [InlineData(MameDriverStatuses.None, true, "silent", true)]
    public void DriverStatuses_ShouldKeepAnyCombination(MameDriverStatuses statuses, bool keepPlayable, string machine, bool isKept)
    {
        // The machines that do not work yet, the imperfect ones only…; a device, without a status, is always kept
        Evaluate(machine, f => Keep(f, statuses, keepPlayable)).IsKept.Should().Be(isKept);
    }

    [Fact]
    public void DriverStatusesOfALaterVersion_ShouldIgnoreUnknownNames()
    {
        var read = SortSettingsSerializer.Read(new Dictionary<string, object?>
        {
            [ConfigSchema.VersionKey] = ConfigSchema.CurrentVersion,
            ["mame_driver_statuses"] = new List<string> { "Good", "broken", "preliminary" }
        });

        read.MameDriverStatuses.Should().Be(MameDriverStatuses.Good | MameDriverStatuses.Preliminary);
    }
}
