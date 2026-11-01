using RomSieve.Core.Models;
using RomSieve.Core.Services;
using RomSieve.Core.Tests.Fixtures;

namespace RomSieve.Core.Tests;

/// <summary>
/// Cabinet of the MAME machines (screen, players, controls) read from the <c>-listxml</c> extract of
/// <c>Fixtures/Mame</c>, its filters, and their options in <c>config.json</c>.
/// </summary>
public sealed class MameCabinetFilterTests
{
    private static readonly CoreTestServices Services = new();

    private static readonly DatCatalog Catalog = Services.DatService
        .ParseDatFileAsync(Path.Combine(AppContext.BaseDirectory, "Fixtures", "Mame", "listxml.dat"))
        .GetAwaiter().GetResult();

    private static readonly DatCatalog SabreTools = Services.DatService
        .ParseDatFileAsync(Path.Combine(AppContext.BaseDirectory, "Fixtures", "Mame", "sabretools_nonmerged.dat"))
        .GetAwaiter().GetResult();

    // A vector game, as Tempest in MAME 0.289 (the extract has none)
    private static readonly DatGame VectorMachine = new()
    {
        Name = "vector",
        Description = "Vector Game (rev 3)",
        Roms = [new DatRomEntry("vector.bin", 16, "12345678")],
        DriverStatus = MameDriverStatus.Good,
        Cabinet = new MameCabinet(MameOrientation.Vertical, MameScreenType.Vector, 2, MameControls.Spinner, 2)
    };

    private static (bool IsKept, string? Reason) Evaluate(string machine, Action<FilterConfig> configure) =>
        Evaluate(Catalog.GameByName[machine], configure);

    private static (bool IsKept, string? Reason) Evaluate(DatGame game, Action<FilterConfig> configure)
    {
        var filters = new FilterConfig();
        configure(filters);
        var (isKept, reason) = Services.FilterService.EvaluateFilter(new MameDescriptionParser().ParseGame(game), filters);
        return (isKept, reason is null ? null : Services.I18n.Format(reason));
    }

    [Fact]
    public void Dat_ShouldDescribeTheScreenAndTheControlsOfEachMachine()
    {
        Catalog.GameByName["sf2"].Cabinet.Should().Be(
            new MameCabinet(MameOrientation.Horizontal, MameScreenType.Raster, 2, MameControls.Joystick, 6));

        // A 4-player football game on a vertical screen, with a joystick and an analog stick for two of the players
        Catalog.GameByName["aafb"].Cabinet.Should().Be(
            new MameCabinet(MameOrientation.Vertical, MameScreenType.Raster, 4, MameControls.Joystick | MameControls.AnalogStick, 1));
        Catalog.GameByName["area51"].Cabinet.Should().Be(
            new MameCabinet(MameOrientation.Horizontal, MameScreenType.Raster, 2, MameControls.Lightgun, 1));
        Catalog.GameByName["airrace"].Cabinet!.Controls.Should().Be(MameControls.Joystick | MameControls.AnalogStick);
        Catalog.GameByName["3lilpigs"].Cabinet.Should().Be(
            new MameCabinet(MameOrientation.Horizontal, MameScreenType.Lcd, 1, MameControls.Keyboard, 9));

        // A machine whose inputs MAME does not describe yet, a device without screen, a DAT of SabreTools
        Catalog.GameByName["timecrs4"].Cabinet.Should().Be(
            new MameCabinet(MameOrientation.Horizontal, MameScreenType.Raster, 0, MameControls.None, 0));
        Catalog.GameByName["hd44780"].Cabinet.Should().BeNull();
        SabreTools.GameByName["sf2"].Cabinet.Should().BeNull();

        new MameDescriptionParser().ParseGame(Catalog.GameByName["aafb"]).Machine!.Cabinet
            .Should().Be(Catalog.GameByName["aafb"].Cabinet);
    }

    [Fact]
    public void Orientation_ShouldKeepTheMachinesOfTheChosenScreen()
    {
        Evaluate("aafb", f => f.MameOrientation = MameOrientationRequirement.Vertical).IsKept.Should().BeTrue();
        Evaluate("sf2", f => f.MameOrientation = MameOrientationRequirement.Vertical)
            .Should().Be((false, "Écran horizontal exclu"));
        Evaluate("aafb", f => f.MameOrientation = MameOrientationRequirement.Horizontal)
            .Should().Be((false, "Écran vertical exclu"));
        Evaluate("sf2", f => f.MameOrientation = MameOrientationRequirement.Horizontal).IsKept.Should().BeTrue();
    }

    [Fact]
    public void VectorAndLcdScreens_ShouldBeExcludedOnDemand()
    {
        Evaluate(VectorMachine, f => f.MameExcludeVector = true).Should().Be((false, "Écran vectoriel exclu"));
        Evaluate(VectorMachine, f => f.MameExcludeLcd = true).IsKept.Should().BeTrue();
        Evaluate("3lilpigs", f => f.MameExcludeLcd = true).Should().Be((false, "Écran LCD exclu"));
        Evaluate("sf2", f =>
        {
            f.MameExcludeVector = true;
            f.MameExcludeLcd = true;
        }).IsKept.Should().BeTrue();
    }

    [Fact]
    public void MinimumPlayers_ShouldExcludeTheMachinesForFewerPlayers()
    {
        Evaluate("sf2", f => f.MameMinimumPlayers = 3).Should().Be((false, "2 joueur(s), 3 au moins demandés"));
        Evaluate("3stooges", f => f.MameMinimumPlayers = 3).IsKept.Should().BeTrue();
        Evaluate("aafb", f => f.MameMinimumPlayers = 4).IsKept.Should().BeTrue();
    }

    [Fact]
    public void MissingControls_ShouldExcludeTheMachinesThatUseThem()
    {
        Evaluate("area51", f => f.MameExcludedControls = MameControls.Lightgun)
            .Should().Be((false, "Commande absente du panneau : Pistolet"));
        Evaluate("aafb", f => f.MameExcludedControls = MameControls.AnalogStick | MameControls.Lightgun)
            .Should().Be((false, "Commande absente du panneau : Manche analogique"));
        Evaluate("sf2", f => f.MameExcludedControls = MameControls.AnalogStick | MameControls.Lightgun | MameControls.Pedal)
            .IsKept.Should().BeTrue();
    }

    [Fact]
    public void MaximumButtons_ShouldExcludeTheMachinesThatNeedMore()
    {
        Evaluate("sf2", f => f.MameMaximumButtons = 4).Should().Be((false, "6 boutons, 4 au plus sur le panneau"));
        Evaluate("mslug", f => f.MameMaximumButtons = 4).IsKept.Should().BeTrue();
        Evaluate("sf2", f => f.MameMaximumButtons = 6).IsKept.Should().BeTrue();
    }

    [Fact]
    public void WhatTheDatDoesNotSay_ShouldNeverExcludeAMachine()
    {
        void Strictest(FilterConfig f)
        {
            f.MameOrientation = MameOrientationRequirement.Vertical;
            f.MameMinimumPlayers = 4;
            f.MameMaximumButtons = 1;
            f.MameExcludedControls = MameControls.Joystick;
        }

        // No number of players nor controls: only its horizontal screen counts
        Evaluate("timecrs4", f =>
        {
            Strictest(f);
            f.MameOrientation = MameOrientationRequirement.Any;
        }).IsKept.Should().BeTrue();

        // Nothing at all: a device, a DAT of SabreTools
        Evaluate("hd44780", Strictest).IsKept.Should().BeTrue();
        Evaluate(SabreTools.GameByName["sf2"], Strictest).IsKept.Should().BeTrue();
    }

    [Fact]
    public void OtherConventions_ShouldIgnoreTheCabinetOptions()
    {
        var parsed = Services.NoIntroParser.ParseRomFilename("Super Game (USA).sfc");
        var filters = new FilterConfig
        {
            MameOrientation = MameOrientationRequirement.Vertical,
            MameExcludeVector = true,
            MameExcludeLcd = true,
            MameMinimumPlayers = 4,
            MameMaximumButtons = 1,
            MameExcludedControls = MameControls.Joystick
        };

        Services.FilterService.EvaluateFilter(parsed, filters).IsKept.Should().BeTrue();
    }

    [Fact]
    public void Options_ShouldBeSavedReadAndPassedToTheRun()
    {
        var settings = new SortSettings
        {
            MameOrientation = MameOrientationRequirement.Vertical,
            MameExcludeVector = true,
            MameExcludeLcd = true,
            MameMinimumPlayers = 2,
            MameMaximumButtons = 6,
            MameExcludedControls = MameControls.Lightgun | MameControls.GamblingPanel
        };
        var data = new Dictionary<string, object?>();

        SortSettingsSerializer.Write(settings, data);

        data["mame_orientation"].Should().Be("vertical");
        data["mame_exclude_vector"].Should().Be(true);
        data["mame_exclude_lcd"].Should().Be(true);
        data["mame_minimum_players"].Should().Be(2);
        data["mame_maximum_buttons"].Should().Be(6);
        data["mame_excluded_controls"].Should().BeEquivalentTo(new[] { "lightgun", "gambling_panel" });

        var filters = SortSettingsSerializer.ToSortConfig(SortSettingsSerializer.Read(data)).Filters;
        filters.MameOrientation.Should().Be(MameOrientationRequirement.Vertical);
        filters.MameExcludeVector.Should().BeTrue();
        filters.MameExcludeLcd.Should().BeTrue();
        filters.MameMinimumPlayers.Should().Be(2);
        filters.MameMaximumButtons.Should().Be(6);
        filters.MameExcludedControls.Should().Be(MameControls.Lightgun | MameControls.GamblingPanel);
    }

    [Fact]
    public void EditedConfiguration_ShouldKeepEveryCabinet()
    {
        var read = SortSettingsSerializer.Read(new Dictionary<string, object?>
        {
            ["mame_orientation"] = "diagonal",
            ["mame_minimum_players"] = -2,
            ["mame_excluded_controls"] = new List<string> { "lightgun", "steering_wheel" }
        });

        read.MameOrientation.Should().Be(MameOrientationRequirement.Any, "an unknown value is the default");
        read.MameMinimumPlayers.Should().Be(0);
        read.MameMaximumButtons.Should().Be(0);
        read.MameExcludeVector.Should().BeFalse();
        read.MameExcludeLcd.Should().BeFalse();
        read.MameExcludedControls.Should().Be(MameControls.Lightgun, "an unknown kind of controls is ignored");
    }
}
