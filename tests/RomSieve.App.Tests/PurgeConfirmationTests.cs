using RomSieve.App;
using RomSieve.App.Abstractions;
using RomSieve.App.ViewModels;
using RomSieve.Core.Models;
using RomSieve.Core.Services;
using RomSieve.Core.Tests.Fixtures;

namespace RomSieve.App.Tests;

/// <summary>
/// Confirmations of a real sort into a non-empty destination: the purge sets the content aside in the quarantine, and
/// purging a folder that RomSieve did not mark asks for a second confirmation.
/// </summary>
public sealed class PurgeConfirmationTests : IDisposable
{
    private readonly TempDirectoryFixture _temp = new();
    private readonly HeadlessDialogService _dialogs = new();
    private readonly MainViewModel _vm;
    private readonly string _destination;

    public PurgeConfirmationTests()
    {
        var config = _temp.WriteText(Path.Combine("config", "config.json"), "{}");
        _temp.WriteText(Path.Combine("src", "Game A (1990)(Ocean).adf"), "A");
        _destination = _temp.CreateSubdirectory("dst");
        _vm = TestMainViewModel.Create(_dialogs, config);
        _vm.Directories.SourceDir = Path.Combine(_temp.Path, "src");
        _vm.Directories.DestinationDir = _destination;
        _vm.UiLocale = "en";
    }

    public void Dispose() => _temp.Dispose();

    private string Quarantine => Path.Combine(_destination, DestinationGuard.QuarantineDirectoryName);

    private string[] Confirmations => _dialogs.ShownMessages.Where(m => m.Level == "CONFIRM").Select(m => m.Title).ToArray();

    [Fact]
    public async Task ForeignDestination_RefusedSecondConfirmation_ShouldLeaveItUntouched()
    {
        var userFile = _temp.WriteText(Path.Combine("dst", "Documents", "letter.txt"), "letter");
        _dialogs.NextConfirmationResults.Enqueue(true);
        _dialogs.NextConfirmationResults.Enqueue(false);

        await _vm.Execution.StartSortAsync();

        Confirmations.Should().Equal("Warning: Destination Directory Not Empty", "Directory Not Created by RomSieve");
        File.ReadAllText(userFile).Should().Be("letter");
        Directory.Exists(Quarantine).Should().BeFalse();
        File.Exists(Path.Combine(_destination, DestinationGuard.MarkerFileName)).Should().BeFalse();
        _vm.Log.Text.Should().Contain("not created by RomSieve");
    }

    [Fact]
    public async Task ForeignDestination_ConfirmedTwice_ShouldSetTheContentAside()
    {
        _temp.WriteText(Path.Combine("dst", "Documents", "letter.txt"), "letter");

        await _vm.Execution.StartSortAsync();

        Confirmations.Should().HaveCount(2);
        _dialogs.ShownMessages[0].Message.Should().Contain("_purged").And.Contain("nothing is deleted");
        Directory.EnumerateFiles(Quarantine, "letter.txt", SearchOption.AllDirectories).Should().ContainSingle();
        Directory.EnumerateFiles(_destination, "Game A (1990)(Ocean).adf", SearchOption.AllDirectories)
            .Should().ContainSingle(f => !DestinationGuard.IsGuardPath(f), "the sort ran after the purge");
    }

    [Fact]
    public async Task DestinationOfAPreviousSort_ShouldAskASingleConfirmation()
    {
        DestinationGuard.WriteMarker(_destination);
        _temp.WriteText(Path.Combine("dst", "Ocean", "Old (1985)(Ocean).adf"), "old");

        await _vm.Execution.StartSortAsync();

        Confirmations.Should().Equal("Warning: Destination Directory Not Empty");
        Directory.EnumerateFiles(Quarantine, "Old (1985)(Ocean).adf", SearchOption.AllDirectories).Should().ContainSingle();
    }

    [Fact]
    public async Task DestinationHoldingOnlyTheMarkerAndTheQuarantine_ShouldBeTreatedAsEmpty()
    {
        DestinationGuard.WriteMarker(_destination);
        _temp.WriteText(Path.Combine("dst", DestinationGuard.QuarantineDirectoryName, "20260101-000000", "old.adf"), "old");

        await _vm.Execution.StartSortAsync();

        Confirmations.Should().Equal("Confirm Sorting Operation");
    }

    [Fact]
    public async Task Confirmation_ShouldNameTheConflictHandlingAsTheOutputStepDoes()
    {
        _vm.Directories.SelectedCollisionMode = CollisionMode.Skip;
        _dialogs.NextConfirmationResult = false;

        await _vm.Execution.StartSortAsync();

        // It used to show the value of config.json ("skip"), in every language
        var message = _dialogs.ShownMessages.Single(m => m.Level == "CONFIRM").Message;
        message.Should().Contain("<b>Collision Handling:</b> Skip<br>").And.NotContain("Undo Last Sort");
    }

    [Fact]
    public async Task OverwritingSort_ShouldWarnThatTheUndoCannotGiveTheReplacedFilesBack()
    {
        _vm.Directories.SelectedCollisionMode = CollisionMode.Overwrite;
        _vm.Directories.AppendToDestination = true;
        _temp.WriteText(Path.Combine("dst", "Game A (1990)(Ocean).adf"), "old");
        _dialogs.NextConfirmationResult = false;

        await _vm.Execution.StartSortAsync();

        var message = _dialogs.ShownMessages.Single(m => m.Level == "CONFIRM").Message;
        message.Should().Contain("conflict handling: <i>Overwrite</i>")
            .And.Contain("a file replaced in the destination is lost: “Undo Last Sort” will not be able to put it back");
        File.ReadAllText(Path.Combine(_destination, "Game A (1990)(Ocean).adf")).Should().Be("old", "the sort was refused");
    }

    [Fact]
    public async Task OverwritingSort_ShouldNameTheModeAndWarnInTheLanguageOfTheInterface()
    {
        _vm.Directories.SelectedCollisionMode = CollisionMode.Overwrite;
        _vm.UiLocale = "fr";
        _dialogs.NextConfirmationResult = false;

        await _vm.Execution.StartSortAsync();

        _dialogs.ShownMessages.Single(m => m.Level == "CONFIRM").Message
            .Should().Contain("<b>Gestion des collisions :</b> Écraser (Overwrite)<br>")
            .And.Contain("« Annuler le dernier tri » ne pourra pas le remettre en place");
    }
}
