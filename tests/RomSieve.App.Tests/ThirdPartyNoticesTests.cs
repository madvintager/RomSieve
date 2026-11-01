using RomSieve.App.Abstractions;
using RomSieve.App.ViewModels;
using RomSieve.Core.Tests.Fixtures;

namespace RomSieve.App.Tests;

/// <summary>
/// Licenses of the third-party components of the interface: THIRD-PARTY-NOTICES.txt, which the build copies next to
/// the executable, names every package it embeds, and the About section of the preferences window opens it, or tells
/// where it is when it cannot.
/// </summary>
public sealed class ThirdPartyNoticesTests : IDisposable
{
    private readonly TempDirectoryFixture _tmp = new();
    private readonly HeadlessDialogService _dialogs = new();
    private readonly PreferencesViewModel _preferences;

    public ThirdPartyNoticesTests()
    {
        _preferences = TestMainViewModel.Create(_dialogs, _tmp.WriteText("config.json", "{}")).Preferences;
    }

    public void Dispose() => _tmp.Dispose();

    [Fact]
    public void EveryPackageOfTheInterface_ShouldBeNamedInTheNotices()
    {
        var packages = ShippedPackages.Read(Path.Combine(AppContext.BaseDirectory, "RomSieve.App.deps.json"));

        // The natives of the two published platforms are embedded, those of macOS never are
        packages.Should().Contain(["Avalonia", "SkiaSharp.NativeAssets.Win32", "SkiaSharp.NativeAssets.Linux", "Avalonia.Angle.Windows.Natives"])
            .And.NotContain("SkiaSharp.NativeAssets.macOS");
        ShippedPackages.MissingFrom(File.ReadAllText(_preferences.ThirdPartyNoticesPath), packages)
            .Should().Equal([], "the licenses of every component the interface embeds accompany it");
    }

    [Fact]
    public async Task OpenThirdPartyNotices_ShouldOpenTheFileNextToTheExecutable()
    {
        await _preferences.OpenThirdPartyNoticesCommand.ExecuteAsync(null);

        var path = Path.Combine(AppContext.BaseDirectory, ShippedPackages.NoticesFileName);
        _dialogs.OpenedFiles.Should().Equal(path);
        File.Exists(path).Should().BeTrue("the build copies the notices next to the executable");
        _dialogs.ShownMessages.Should().BeEmpty();
    }

    [Fact]
    public async Task OpenThirdPartyNotices_WhenTheFileIsMissing_ShouldTellWhereItShouldBe()
    {
        _preferences.ApplicationDirectory = _tmp.Path;

        await _preferences.OpenThirdPartyNoticesCommand.ExecuteAsync(null);

        _dialogs.OpenedFiles.Should().BeEmpty();
        var (level, title, message) = _dialogs.ShownMessages.Should().ContainSingle().Subject;
        level.Should().Be("WARN");
        title.Should().Be("Licences des composants tiers");
        message.Should().Be(
            $"Le fichier des licences des composants tiers est introuvable :\n{Path.Combine(_tmp.Path, ShippedPackages.NoticesFileName)}\n\n"
            + "Il est livré avec RomSieve, à côté de ses exécutables : vous le trouverez dans l'archive téléchargée.");
    }

    [Fact]
    public async Task OpenThirdPartyNotices_WhenNoApplicationOpensIt_ShouldGiveItsPath()
    {
        _dialogs.NextOpenFileResult = false;

        await _preferences.OpenThirdPartyNoticesCommand.ExecuteAsync(null);

        var path = Path.Combine(AppContext.BaseDirectory, ShippedPackages.NoticesFileName);
        _dialogs.OpenedFiles.Should().Equal(path);
        var (level, _, message) = _dialogs.ShownMessages.Should().ContainSingle().Subject;
        level.Should().Be("INFO");
        message.Should().Be(
            $"Aucune application n'a pu ouvrir le fichier des licences des composants tiers. Ouvrez-le avec un éditeur de texte :\n{path}");
    }
}
