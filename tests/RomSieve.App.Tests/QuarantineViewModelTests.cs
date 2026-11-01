using RomSieve.App;
using RomSieve.App.Abstractions;
using RomSieve.App.ViewModels;
using RomSieve.Core.Services;
using RomSieve.Core.Tests.Fixtures;

namespace RomSieve.App.Tests;

/// <summary>
/// Quarantine of the purges of the destination in the Output step: measured once the destination is chosen, only for a
/// destination marked by RomSieve, and emptied for good after a confirmation that warns about the undo.
/// </summary>
public sealed class QuarantineViewModelTests : IDisposable
{
    private readonly TempDirectoryFixture _tmp = new();
    private readonly HeadlessDialogService _dialogs = new();

    public void Dispose() => _tmp.Dispose();

    private MainViewModel CreateViewModel() =>
        TestMainViewModel.Create(_dialogs, _tmp.WriteText("config.json", "{}"));

    /// <summary>A destination of RomSieve with two purges in its quarantine, and one sorted file.</summary>
    private string CreatePurgedDestination(bool marked = true)
    {
        var destination = _tmp.CreateSubdirectory("dst");
        _tmp.WriteText(Path.Combine("dst", "Ocean", "sorted.adf"), "sorted");
        _tmp.WriteText(Path.Combine("dst", DestinationGuard.QuarantineDirectoryName, "20260101-000000", "old.adf"), "old");
        _tmp.WriteText(Path.Combine("dst", DestinationGuard.QuarantineDirectoryName, "20260102-000000", "a", "older.adf"), "older");
        if (marked)
        {
            DestinationGuard.WriteMarker(destination);
        }

        return destination;
    }

    [Fact]
    public async Task Destination_ShouldShowWhatItsQuarantineHolds()
    {
        var vm = CreateViewModel();
        var quarantine = vm.Quarantine;
        quarantine.HasQuarantine.Should().BeFalse();
        quarantine.EmptyCommand.CanExecute(null).Should().BeFalse();

        await quarantine.LoadAsync(CreatePurgedDestination());

        quarantine.HasQuarantine.Should().BeTrue();
        quarantine.SummaryText.Should().Be("Quarantaine des purges (_purged) : 2 purge(s), 2 fichier(s), 8 o.");
        quarantine.EmptyCommand.CanExecute(null).Should().BeTrue();

        // Nothing is emptied while a run goes on
        quarantine.IsRunning = true;
        quarantine.EmptyCommand.CanExecute(null).Should().BeFalse();
        quarantine.IsRunning = false;

        vm.UiLocale = "en";
        quarantine.SummaryText.Should().Be("Quarantine of the purges (_purged): 2 purge(s), 2 file(s), 8 B.");
    }

    /// <summary>
    /// The line is shown as soon as a destination is chosen, empty or not, so that the user learns that a purge sets the
    /// content aside and that it can be emptied here; the button stays greyed with nothing to empty.
    /// </summary>
    [Fact]
    public async Task EmptyQuarantine_ShouldStillBeShownOnceADestinationIsChosen()
    {
        var vm = CreateViewModel();
        var quarantine = vm.Quarantine;
        quarantine.HasDestination.Should().BeFalse();
        quarantine.SummaryText.Should().BeEmpty();

        await quarantine.LoadAsync(_tmp.CreateSubdirectory("new"));

        quarantine.HasDestination.Should().BeTrue();
        quarantine.HasQuarantine.Should().BeFalse();
        quarantine.SummaryText.Should().Be("Quarantaine des purges (_purged) : vide.");
        quarantine.EmptyTooltip.Should().StartWith("La quarantaine est vide");
        quarantine.EmptyCommand.CanExecute(null).Should().BeFalse();

        vm.UiLocale = "en";
        quarantine.SummaryText.Should().Be("Quarantine of the purges (_purged): empty.");

        await quarantine.LoadAsync(CreatePurgedDestination());
        quarantine.EmptyTooltip.Should().Be(vm.I18n["quarantine.tooltip_empty"]);

        await quarantine.LoadAsync("  ");
        quarantine.HasDestination.Should().BeFalse("without a destination, there is no quarantine to speak of");
    }

    [Fact]
    public async Task UnmarkedDestination_ShouldHaveNoQuarantine()
    {
        var quarantine = CreateViewModel().Quarantine;

        await quarantine.LoadAsync(CreatePurgedDestination(marked: false));

        quarantine.HasQuarantine.Should().BeFalse("the _purged folder of a folder RomSieve did not mark is ordinary content");
    }

    [Fact]
    public async Task ChoosingTheDestination_ShouldMeasureItsQuarantine()
    {
        var vm = CreateViewModel();
        var destination = CreatePurgedDestination();

        vm.Directories.DestinationDir = destination;
        for (var i = 0; i < 200 && !vm.Quarantine.HasQuarantine; i++)
        {
            await Task.Delay(10);
        }

        vm.Quarantine.HasQuarantine.Should().BeTrue();
        vm.Directories.DestinationDir = string.Empty;
        for (var i = 0; i < 200 && vm.Quarantine.HasQuarantine; i++)
        {
            await Task.Delay(10);
        }

        vm.Quarantine.HasQuarantine.Should().BeFalse();
    }

    [Fact]
    public async Task Empty_ShouldDeleteTheQuarantineOnlyOnceConfirmed()
    {
        var vm = CreateViewModel();
        var destination = CreatePurgedDestination();
        await vm.Quarantine.LoadAsync(destination);

        // Refused: nothing is deleted, and the confirmation said what the undo would lose
        _dialogs.NextConfirmationResult = false;
        await vm.Quarantine.EmptyCommand.ExecuteAsync(null);
        var confirmation = _dialogs.ShownMessages.Single(m => m.Level == "CONFIRM");
        confirmation.Message.Should().Contain("Purges : 2").And.Contain("l'annulation des tris qui l'ont purgé ne pourra plus le remettre en place");
        Directory.Exists(Path.Combine(destination, DestinationGuard.QuarantineDirectoryName)).Should().BeTrue();

        _dialogs.NextConfirmationResult = true;
        await vm.Quarantine.EmptyCommand.ExecuteAsync(null);

        Directory.Exists(Path.Combine(destination, DestinationGuard.QuarantineDirectoryName)).Should().BeFalse();
        File.Exists(Path.Combine(destination, "Ocean", "sorted.adf")).Should().BeTrue("the sorted files are never touched");
        vm.Quarantine.HasQuarantine.Should().BeFalse();
        _dialogs.ShownMessages.Last().Should().Be(("INFO", "Vider la quarantaine", "Quarantaine vidée : 2 fichier(s) supprimé(s), 8 o libéré(s)."));
        vm.Log.Entries.Last().FormattedLine.Should().Contain("Quarantaine vidée : 2 fichier(s) supprimé(s)");
    }
}
