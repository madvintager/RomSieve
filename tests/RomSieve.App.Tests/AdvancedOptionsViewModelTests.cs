using RomSieve.App;
using RomSieve.App.Abstractions;
using RomSieve.App.ViewModels;
using RomSieve.Core.Models;
using RomSieve.Core.Tests.Fixtures;

namespace RomSieve.App.Tests;

/// <summary>
/// Advanced options of the Directories tab, in a drop-down panel: the button counts the options which differ from
/// their default value.
/// </summary>
public sealed class AdvancedOptionsViewModelTests
{
    [Fact]
    public void DirectoriesHeader_ShouldCountTheOptionsThatDifferFromTheirDefault()
    {
        using var tmp = new TempDirectoryFixture();
        var vm = TestMainViewModel.Create(new HeadlessDialogService(), tmp.WriteText("config.json", "{}"));
        var advanced = vm.Directories.Advanced;
        var notified = new List<string?>();
        advanced.PropertyChanged += (_, e) => notified.Add(e.PropertyName);

        advanced.ChangedCount.Should().Be(0);
        advanced.HeaderText.Should().Be("Options avancées");
        advanced.HeaderTooltip.Should().StartWith("Réglages rarement utiles");

        vm.Directories.VerifySha1 = true;
        advanced.ChangedCount.Should().Be(1);
        advanced.HeaderText.Should().Be("Options avancées (1 modifiée)");
        notified.Should().Contain(nameof(AdvancedOptionsViewModel.HeaderText));

        // Archives are inspected by default: turning it off is a change
        vm.Directories.ScanArchives = false;
        vm.Directories.MatchChdByName = true;
        advanced.ChangedCount.Should().Be(3);
        advanced.HeaderText.Should().Be("Options avancées (3 modifiées)");
        advanced.HeaderTooltip.Should().Be(
            "Options qui diffèrent de leur valeur par défaut : Inspecter les archives (ZIP, 7z, RAR), Vérifier le SHA-1, "
            + "Identifier les CHD par leur nom.");

        vm.UiLocale = "en";
        advanced.HeaderText.Should().Be("Advanced Options (3 changed)");

        // Back to the defaults
        vm.Directories.ReadFrom(new SortSettings());
        advanced.ChangedCount.Should().Be(0);
        advanced.HeaderText.Should().Be("Advanced Options");

        // An option outside the group is not counted: the verification of the copies, a visible option of the output
        // step, and the place of the playlists, which stays next to its option
        vm.Directories.VerifyCopies = true;
        vm.Directories.IsRecursive = false;
        vm.Directories.SelectedM3uPlacement = M3uPlacementMode.Subfolder;
        advanced.ChangedCount.Should().Be(0);
    }
}
