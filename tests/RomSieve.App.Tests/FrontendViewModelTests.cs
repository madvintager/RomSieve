using System.IO.Hashing;
using System.Text;
using RomSieve.App.ViewModels;
using RomSieve.Core.Models;
using RomSieve.Core.Services;
using RomSieve.Core.Tests.Fixtures;

namespace RomSieve.App.Tests;

/// <summary>
/// The frontend in the interface: the list of the output step, saved as any option; choosing a frontend offers its
/// recommended output settings, listing those that change, applied when accepted, left as they are when refused, never
/// offered for a configuration read; the preview of the pattern shows the folder of the platform, as the sort writes it;
/// the placement of the playlists read as a game and the folder per disc game are offered.
/// </summary>
public sealed class FrontendViewModelTests : IDisposable
{
    private static readonly byte[] Tetris = Encoding.ASCII.GetBytes("TETRIS GB WORLD");
    private static readonly byte[] Alleyway = Encoding.ASCII.GetBytes("ALLEYWAY GB WORLD");

    private readonly TempDirectoryFixture _tmp = new();
    private readonly CoreTestServices _services = new();
    private readonly HeadlessDialogService _dialogs = new();

    public void Dispose() => _tmp.Dispose();

    private DirectoriesViewModel CreateDirectories() =>
        new(_services.I18n, _dialogs, _services.DatService, _services.NoIntroParser, _services.Organizer,
            _services.MameParser, _services.Frontends, TimeSpan.Zero);

    private static string Rom(string name, byte[] data) =>
        $"""<rom name="{name}" size="{data.Length}" crc="{Crc32.HashToUInt32(data):x8}"/>""";

    private string WriteGameBoyDat() => _tmp.WriteText("gb.dat", $"""
        <?xml version="1.0"?>
        <datafile>
        	<header><name>Nintendo - Game Boy</name><description>Nintendo - Game Boy</description></header>
        	<game name="Tetris (World)"><description>Tetris (World)</description>{Rom("Tetris (World).gb", Tetris)}</game>
        	<game name="Alleyway (World)"><description>Alleyway (World)</description>{Rom("Alleyway (World).gb", Alleyway)}</game>
        </datafile>
        """);

    private (string Title, string Message) LastConfirmation() =>
        _dialogs.ShownMessages.Last(m => m.Level == "CONFIRM") is var (_, title, message) ? (title, message) : default;

    private int ConfirmationCount => _dialogs.ShownMessages.Count(m => m.Level == "CONFIRM");

    [Fact]
    public void Lists_ShouldOfferEveryFrontend_AndThePlaylistReadAsAGame()
    {
        var directories = CreateDirectories();

        directories.FrontendOptions.Select(o => o.Value).Should().Equal(
            FrontendTarget.None, FrontendTarget.Batocera, FrontendTarget.RetroPie, FrontendTarget.EsDe, FrontendTarget.Recalbox, FrontendTarget.MiSTer);
        directories.FrontendOptions.Select(o => o.Label).Should().Equal("Aucun", "Batocera", "RetroPie", "ES-DE", "Recalbox", "MiSTer");
        directories.M3uPlacementOptions.Select(o => o.Value).Should().Contain(M3uPlacementMode.GameFolder);
        directories.Frontend.Should().Be(FrontendTarget.None);
        directories.FolderPerDiscGame.Should().BeFalse();
    }

    [Fact]
    public async Task ChosenFrontend_ShouldOfferItsSettings_ListingTheChanges_AndApplyThemWhenAccepted()
    {
        var directories = CreateDirectories();
        directories.Pattern = "{title}";

        directories.Frontend = FrontendTarget.EsDe;
        await directories.RecommendationTask;

        var (title, message) = LastConfirmation();
        title.Should().Be("Réglages recommandés pour ES-DE");
        message.Should().Contain("• Organisation : Tout à la racine (aucun sous-dossier)")
            .And.Contain("• Une playlist .m3u par jeu : oui")
            .And.Contain("• Emplacement : Dans un dossier à son nom (ES-DE)")
            .And.NotContain("Un sous-dossier par jeu", "the subfolder is already on");
        directories.FlatDestination.Should().BeTrue();
        directories.GenerateM3u.Should().BeTrue();
        directories.SubfolderMultidisk.Should().BeTrue();
        directories.SelectedM3uPlacement.Should().Be(M3uPlacementMode.GameFolder);
        directories.FolderPerDiscGame.Should().BeFalse();
        directories.Pattern.Should().Be("{title}", "the pattern is never touched");
        directories.SelectedTransferMode.Should().Be(TransferMode.Copy, "the transfer mode is never touched");
    }

    [Theory]
    [InlineData(FrontendTarget.Batocera, true, false, true, M3uPlacementMode.Parent, false)]
    [InlineData(FrontendTarget.Recalbox, true, false, true, M3uPlacementMode.Parent, false)]
    [InlineData(FrontendTarget.RetroPie, true, true, true, M3uPlacementMode.ParentHidden, false)]
    [InlineData(FrontendTarget.MiSTer, false, true, false, M3uPlacementMode.Parent, true)]
    public async Task AcceptedSettings_ShouldBeThoseOfTheFrontend(
        FrontendTarget frontend, bool flat, bool subfolder, bool m3u, M3uPlacementMode placement, bool folderPerDiscGame)
    {
        var directories = CreateDirectories();

        directories.Frontend = frontend;
        await directories.RecommendationTask;

        (directories.FlatDestination, directories.SubfolderMultidisk, directories.GenerateM3u, directories.SelectedM3uPlacement, directories.FolderPerDiscGame)
            .Should().Be((flat, subfolder, m3u, placement, folderPerDiscGame));
    }

    [Fact]
    public async Task RefusedSettings_ShouldLeaveTheSettings_AndKeepTheFrontend()
    {
        var directories = CreateDirectories();
        _dialogs.NextConfirmationResult = false;

        directories.Frontend = FrontendTarget.MiSTer;
        await directories.RecommendationTask;

        ConfirmationCount.Should().Be(1);
        LastConfirmation().Message.Should().Contain("• Un dossier par jeu sur disque : oui");
        directories.Frontend.Should().Be(FrontendTarget.MiSTer);
        directories.FolderPerDiscGame.Should().BeFalse();
        directories.SubfolderMultidisk.Should().BeTrue();
    }

    [Fact]
    public async Task SettingsAlreadyRecommended_ShouldAskNothing_NorNoFrontend()
    {
        var directories = CreateDirectories();
        directories.Frontend = FrontendTarget.Batocera;
        await directories.RecommendationTask;
        ConfirmationCount.Should().Be(1);

        directories.Frontend = FrontendTarget.Recalbox;
        await directories.RecommendationTask;
        directories.Frontend = FrontendTarget.None;
        await directories.RecommendationTask;

        ConfirmationCount.Should().Be(1, "Recalbox wants the settings of Batocera, and no frontend wants none");
    }

    [Fact]
    public async Task ConfigurationRead_ShouldKeepItsSettings_WithoutAsking()
    {
        var vm = TestMainViewModel.Create(_dialogs, _tmp.WriteText("config.json",
            """{ "frontend": "es-de", "flat_destination": false, "generate_m3u": false, "folder_per_disc_game": true }"""));
        await vm.Directories.RecommendationTask;

        vm.Directories.Frontend.Should().Be(FrontendTarget.EsDe);
        vm.Directories.FlatDestination.Should().BeFalse();
        vm.Directories.GenerateM3u.Should().BeFalse();
        vm.Directories.FolderPerDiscGame.Should().BeTrue();
        ConfirmationCount.Should().Be(0);

        var settings = new SortSettings();
        vm.Directories.WriteTo(settings);
        settings.Frontend.Should().Be(FrontendTarget.EsDe);
        settings.FolderPerDiscGame.Should().BeTrue();
    }

    [Theory]
    [InlineData(FrontendTarget.Batocera, "gb")]
    [InlineData(FrontendTarget.MiSTer, "GAMEBOY")]
    [InlineData(FrontendTarget.None, null)]
    public async Task Preview_ShouldShowTheFolderOfThePlatform_AsTheSortWritesIt(FrontendTarget frontend, string? folder)
    {
        var source = _tmp.CreateSubdirectory("src");
        File.WriteAllBytes(Path.Combine(source, "Tetris (World).gb"), Tetris);
        File.WriteAllBytes(Path.Combine(source, "Alleyway (World).gb"), Alleyway);
        var directories = CreateDirectories();
        directories.DestinationDir = _tmp.GetFilePath("dest");
        directories.Pattern = "{title}";
        directories.DatFilePath = WriteGameBoyDat();
        await directories.DatLoadTask;
        directories.SourceDir = source;
        directories.Frontend = frontend;
        await directories.RecommendationTask;
        await directories.Preview.WhenIdleAsync();

        var settings = new SortSettings();
        directories.WriteTo(settings);
        var result = await _services.Processor.ExecuteAsync(SortSettingsSerializer.ToSortConfig(settings), isDryRun: true);
        var sortTargets = result.Items.ToDictionary(i => Path.GetFileName(i.SourcePath), i => i.TargetPath);

        directories.Preview.Examples.Should().HaveCount(2);
        foreach (var example in directories.Preview.Examples)
        {
            example.TargetPath.Should().Be(sortTargets[example.SourceName]);
            var first = Path.GetRelativePath(directories.DestinationDir, example.TargetPath).Split(Path.DirectorySeparatorChar)[0];
            if (folder is not null)
            {
                first.Should().Be(folder);
            }
            else
            {
                first.Should().NotBe("gb");
            }
        }
    }
}
