using RomSieve.App.Abstractions;
using RomSieve.App.ViewModels;
using RomSieve.Core.Tests.Fixtures;

namespace RomSieve.App.Tests;

/// <summary>
/// A real sort that stops before its end says why at once, in a message box and not only in the log: here, a journal
/// that cannot be written, without which the sort could not be undone, so that nothing is written.
/// </summary>
public sealed class SortStopViewModelTests : IDisposable
{
    private readonly TempDirectoryFixture _temp = new();
    private readonly HeadlessDialogService _dialogs = new();
    private readonly MainViewModel _vm;
    private readonly string _source;
    private readonly string _destination;

    public SortStopViewModelTests()
    {
        var config = _temp.WriteText(Path.Combine("config", "config.json"), "{}");

        // A file where the folder of the journals should be: the journal cannot be written
        _temp.WriteText(Path.Combine("config", "journals"), "not a folder");
        _temp.WriteText(Path.Combine("src", "Game A (1990)(Ocean).adf"), "A");
        _source = Path.Combine(_temp.Path, "src");
        _destination = Path.Combine(_temp.Path, "dst");
        _vm = TestMainViewModel.Create(_dialogs, config);
        _vm.Directories.SourceDir = _source;
        _vm.Directories.DestinationDir = _destination;
        _vm.Directories.IsTransferMove = true;
    }

    public void Dispose() => _temp.Dispose();

    [Fact]
    public async Task SortWithoutJournal_ShouldStopBeforeWritingAndSaySo()
    {
        await _vm.Execution.StartSortAsync();

        var warning = _dialogs.ShownMessages.Should().ContainSingle(m => m.Level == "WARN").Subject;
        warning.Title.Should().Be("Tri arrêté");
        warning.Message.Should().Contain("le journal des opérations ne peut pas être écrit");
        Directory.Exists(_destination).Should().BeFalse();
        File.Exists(Path.Combine(_source, "Game A (1990)(Ocean).adf")).Should().BeTrue("a move without a journal could not be undone");
        _vm.Log.Text.Should().Contain("Tri arrêté avant d'écrire quoi que ce soit");
    }
}
