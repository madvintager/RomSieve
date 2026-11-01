using System.Text.Json;
using RomSieve.App;
using RomSieve.App.Abstractions;
using RomSieve.App.ViewModels;
using RomSieve.Core.Models;
using RomSieve.Core.Tests.Fixtures;

namespace RomSieve.App.Tests;

/// <summary>
/// Cabinet filters of the MAME machines in the Filters page: options read from and saved to <c>config.json</c>, the
/// controls shown as what the panel has, and their translation.
/// </summary>
public sealed class MameCabinetFiltersViewModelTests
{
    private static MainViewModel CreateViewModel(TempDirectoryFixture tmp, string json = "{}") =>
        TestMainViewModel.Create(new HeadlessDialogService(), tmp.WriteText("config.json", json));

    [Fact]
    public void Defaults_ShouldKeepEveryCabinet()
    {
        using var tmp = new TempDirectoryFixture();
        var cabinet = CreateViewModel(tmp).Filters.Cabinet;

        cabinet.Orientation.Should().Be(MameOrientationRequirement.Any);
        cabinet.OrientationOptions.Select(o => o.Label).Should().Equal("Toutes orientations", "Horizontales uniquement", "Verticales uniquement");
        cabinet.SummaryText.Should().Be("Borne : toutes");
        cabinet.IsCabinetRestricted.Should().BeFalse();
        cabinet.AreControlsRestricted.Should().BeFalse();
        cabinet.KeepVector.Should().BeTrue();
        cabinet.KeepLcd.Should().BeTrue();
        cabinet.MinimumPlayers.Should().Be(0);
        cabinet.MaximumButtons.Should().Be(0);
        cabinet.Controls.Should().HaveCount(9).And.OnlyContain(c => c.IsAvailable);
        cabinet.ControlsText.Should().Be("Commandes : toutes");
        cabinet.PlayerOptions.Select(o => o.Label).Should().Equal("Tous", "2 ou plus", "3 ou plus", "4 ou plus");
        cabinet.ButtonOptions.Select(o => o.Label).Should().Equal("Sans limite", "1 au plus", "2 au plus", "3 au plus", "4 au plus", "6 au plus");
    }

    [Fact]
    public async Task Options_ShouldBeReadPresentedAndSaved()
    {
        using var tmp = new TempDirectoryFixture();
        var vm = CreateViewModel(tmp, """
            { "mame_orientation": "vertical", "mame_exclude_vector": true, "mame_minimum_players": 2,
              "mame_excluded_controls": ["lightgun"] }
            """);
        var cabinet = vm.Filters.Cabinet;

        cabinet.Orientation.Should().Be(MameOrientationRequirement.Vertical);
        cabinet.SummaryText.Should().Be("Borne : 3 restrictions");
        cabinet.SummaryTooltip.Should().EndWith("Restrictions : écran vertical, sans écran vectoriel, 2 joueurs ou plus");
        cabinet.IsCabinetRestricted.Should().BeTrue();
        cabinet.AreControlsRestricted.Should().BeTrue();
        cabinet.KeepVector.Should().BeFalse();
        cabinet.KeepLcd.Should().BeTrue();
        cabinet.MinimumPlayers.Should().Be(2);
        var lightgun = cabinet.Controls.Single(c => c.Kind == MameControls.Lightgun);
        lightgun.IsAvailable.Should().BeFalse();
        lightgun.Label.Should().Be("Pistolet");
        cabinet.ControlsText.Should().Be("Commandes : 8 sur 9");
        cabinet.ControlsTooltip.Should().EndWith("Absentes du panneau : Pistolet");

        // Each choice writes its option, as the panel says what it has
        var notified = new List<string?>();
        cabinet.PropertyChanged += (_, e) => notified.Add(e.PropertyName);
        cabinet.Controls.Single(c => c.Kind == MameControls.Pedal).IsAvailable = false;
        cabinet.Orientation = MameOrientationRequirement.Horizontal;
        cabinet.KeepLcd = false;
        cabinet.MaximumButtons = 6;

        vm.NoIntroFilters.MameExcludedControls.Should().Be(MameControls.Lightgun | MameControls.Pedal);
        vm.NoIntroFilters.MameOrientation.Should().Be(MameOrientationRequirement.Horizontal);
        cabinet.ControlsText.Should().Be("Commandes : 7 sur 9");
        notified.Should().Contain([nameof(cabinet.ControlsText), nameof(cabinet.Orientation), nameof(cabinet.KeepLcd), nameof(cabinet.SummaryText)]);

        await vm.SaveConfigurationAsync();
        using var saved = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(tmp.Path, "config.json")));
        saved.RootElement.GetProperty("mame_orientation").GetString().Should().Be("horizontal");
        saved.RootElement.GetProperty("mame_exclude_vector").GetBoolean().Should().BeTrue();
        saved.RootElement.GetProperty("mame_exclude_lcd").GetBoolean().Should().BeTrue();
        saved.RootElement.GetProperty("mame_minimum_players").GetInt32().Should().Be(2);
        saved.RootElement.GetProperty("mame_maximum_buttons").GetInt32().Should().Be(6);
        saved.RootElement.GetProperty("mame_excluded_controls").EnumerateArray().Select(e => e.GetString())
            .Should().Equal("pedal", "lightgun");
    }

    [Fact]
    public void LanguageSwitch_ShouldTranslateTheChoicesAndTheControls()
    {
        using var tmp = new TempDirectoryFixture();
        var vm = CreateViewModel(tmp, """{ "mame_excluded_controls": ["trackball"], "mame_maximum_buttons": 4 }""");
        var cabinet = vm.Filters.Cabinet;
        var trackball = cabinet.Controls.Single(c => c.Kind == MameControls.Trackball);

        vm.UiLocale = "en";

        cabinet.PlayerOptions[1].Label.Should().Be("2 or more");
        cabinet.ButtonOptions[0].Label.Should().Be("No limit");
        trackball.Label.Should().Be("Trackball or mouse");
        trackball.Tooltip.Should().Be("Trackballs (Centipede, Marble Madness) and mice.");
        cabinet.ControlsText.Should().Be("Controls: 8 out of 9");
        cabinet.OrientationOptions[2].Label.Should().Be("Vertical only");
        cabinet.SummaryText.Should().Be("Cabinet: 4 buttons at most");
    }
}
