using System.Text.Json;
using RomSieve.App;
using RomSieve.App.Abstractions;
using RomSieve.App.ViewModels;
using RomSieve.Core.Models;
using RomSieve.Core.Services;
using RomSieve.Core.Tests.Fixtures;

namespace RomSieve.App.Tests;

/// <summary>
/// Live preview of the directory pattern: computed by the code of the sort, on the first conforming files of the
/// source or on built-in examples, refreshed when the options or the source change, and warning about a pattern
/// that would not do what it says.
/// </summary>
public sealed class PatternPreviewViewModelTests : IDisposable
{
    private readonly TempDirectoryFixture _tmp = new();
    private readonly CoreTestServices _services = new();

    public void Dispose() => _tmp.Dispose();

    private DirectoriesViewModel CreateDirectories(TimeSpan? debounce = null) =>
        new(_services.I18n, new HeadlessDialogService(), _services.DatService, _services.NoIntroParser, _services.Organizer,
            _services.MameParser, debounce ?? TimeSpan.Zero);

    private string CreateSource(string name, params string[] files)
    {
        var dir = _tmp.CreateSubdirectory(name);
        foreach (var file in files)
        {
            File.WriteAllBytes(Path.Combine(dir, file), [1, 2, 3, 4]);
        }

        return dir;
    }

    public static TheoryData<NamingConvention, string, bool, TransferMode, bool, string[]> SortCases() => new()
    {
        { NamingConvention.Tosec, SortSettings.DefaultPattern, false, TransferMode.Copy, false,
            ["Lemmings (1991)(Psygnosis)(FR)[!].adf", "Another World (1991)(Delphine)(Disk 1 of 2).adf", "Another World (1991)(Delphine)(Disk 2 of 2).adf"] },
        { NamingConvention.Tosec, "{letter}/{title} ({year})/", false, TransferMode.Zip, true,
            ["Legend of Kyrandia, The (1993)(Westwood)(Disk 1 of 2).adf", "Legend of Kyrandia, The (1993)(Westwood)(Disk 2 of 2).adf", "Zool (1992)(Gremlin).adf"] },
        { NamingConvention.NoIntro, "{country}/{title}/", false, TransferMode.Copy, false,
            ["Super Mario World (USA).sfc", "Legend of Zelda, The - A Link to the Past (Europe).sfc", "F-Zero (Japan).sfc"] },
        { NamingConvention.Redump, "{letter}/{language}/", false, TransferMode.Copy, false,
            ["Tomb Raider (France).iso", "Crash Bandicoot (Europe, Australia).iso"] },
        { NamingConvention.Tosec, "{country}/{title}/", true, TransferMode.Copy, false,
            ["Lemmings (1991)(Psygnosis)(FR)[!].adf", "Zool (1992)(Gremlin).adf"] }
    };

    /// <summary>The preview shows exactly the targets of a simulation of the same source with the same options.</summary>
    [Theory]
    [MemberData(nameof(SortCases))]
    public async Task Preview_ShouldShowTheTargetsOfTheSort(
        NamingConvention convention, string pattern, bool flat, TransferMode mode, bool hiddenPlaylists, string[] files)
    {
        var source = CreateSource("src", files);
        var directories = CreateDirectories();
        directories.SelectedNamingConvention = convention;
        directories.DestinationDir = _tmp.GetFilePath("dest");
        directories.Pattern = pattern;
        directories.FlatDestination = flat;
        directories.SelectedTransferMode = mode;
        directories.GenerateM3u = hiddenPlaylists;
        directories.SelectedM3uPlacement = hiddenPlaylists ? M3uPlacementMode.ParentHidden : M3uPlacementMode.Parent;
        directories.SourceDir = source;
        await directories.Preview.WhenIdleAsync();

        var settings = new SortSettings();
        directories.WriteTo(settings);
        var result = await _services.Processor.ExecuteAsync(SortSettingsSerializer.ToSortConfig(settings), isDryRun: true);
        var sortTargets = result.Items.ToDictionary(i => Path.GetFileName(i.SourcePath), i => i.TargetPath);

        var preview = directories.Preview;
        preview.IsBuiltIn.Should().BeFalse();
        preview.HasWarning.Should().BeFalse();
        preview.Examples.Select(e => e.SourceName).Should().Equal(files.Order(StringComparer.OrdinalIgnoreCase).Take(PatternPreviewViewModel.ExampleCount));
        foreach (var example in preview.Examples)
        {
            example.TargetPath.Should().Be(sortTargets[example.SourceName], $"{example.SourceName} goes where the sort writes it");
        }
    }

    [Theory]
    [InlineData(NamingConvention.Auto)]
    [InlineData(NamingConvention.Tosec)]
    [InlineData(NamingConvention.NoIntro)]
    [InlineData(NamingConvention.Redump)]
    public void WithoutSource_ShouldShowBuiltInExamplesOfTheConvention(NamingConvention convention)
    {
        var directories = CreateDirectories();
        directories.SelectedNamingConvention = convention;
        var preview = directories.Preview;

        preview.IsBuiltIn.Should().BeTrue();
        preview.CaptionText.Should().Be(_services.I18n.Tr("tab1.preview_builtin_no_source"));
        preview.Examples.Should().HaveCount(PatternPreviewViewModel.ExampleCount);
        preview.Examples.Should().OnlyContain(e => e.TargetPath.StartsWith(_services.I18n.Tr("tab1.preview_destination"), StringComparison.Ordinal));
        preview.Examples.Should().OnlyContain(e => !e.TargetPath.Contains(PathOrganizerService.UnmatchedFolderName, StringComparison.Ordinal),
            "the built-in examples follow their convention");
    }

    [Fact]
    public void BuiltInExamples_ShouldFillTheDefaultPatternOfEachConvention()
    {
        // The default pattern needs TOSEC names: No-Intro and Redump examples are shown with theirs
        var directories = CreateDirectories();
        directories.SelectedNamingConvention = NamingConvention.NoIntro;
        directories.PresetNoIntroRegion();
        directories.Preview.Examples.Should().Contain(e => e.TargetPath.EndsWith(
            Path.Combine("EU", "The Legend of Zelda - A Link to the Past", "Legend of Zelda, The - A Link to the Past (Europe).sfc"),
            StringComparison.Ordinal));
    }

    [Fact]
    public async Task SourceWithoutConformingFile_ShouldFallBackToTheBuiltInExamples()
    {
        var directories = CreateDirectories();
        directories.SelectedNamingConvention = NamingConvention.Tosec;
        directories.SourceDir = CreateSource("src", "readme.txt", "notes");
        await directories.Preview.WhenIdleAsync();

        directories.Preview.IsBuiltIn.Should().BeTrue();
        directories.Preview.CaptionText.Should().Be(_services.I18n.Tr("tab1.preview_builtin_no_match"));
        directories.Preview.Examples.Should().HaveCount(PatternPreviewViewModel.ExampleCount);
    }

    [Fact]
    public async Task SourceChange_ShouldCancelTheListingOfThePreviousSource()
    {
        var first = CreateSource("first", "Lemmings (1991)(Psygnosis).adf");
        var second = CreateSource("second", "Zool (1992)(Gremlin).adf");
        var directories = CreateDirectories(TimeSpan.FromMilliseconds(50));
        directories.SelectedNamingConvention = NamingConvention.Tosec;

        // Typed in quick succession: only the last source is listed
        directories.SourceDir = first;
        directories.Preview.IsListingSource.Should().BeTrue();
        directories.Preview.CaptionText.Should().Be(_services.I18n.Tr("tab1.preview_listing"));
        directories.SourceDir = second;
        await directories.Preview.WhenIdleAsync();

        directories.Preview.IsListingSource.Should().BeFalse();
        directories.Preview.CaptionText.Should().Be(_services.I18n.Tr("tab1.preview_from_source"));
        directories.Preview.Examples.Select(e => e.SourceName).Should().Equal("Zool (1992)(Gremlin).adf");

        // A new file in the source shows after the source is chosen again
        File.WriteAllBytes(Path.Combine(second, "Another World (1991)(Delphine).adf"), [1]);
        directories.SourceDir = first;
        await directories.Preview.WhenIdleAsync();
        directories.Preview.Examples.Select(e => e.SourceName).Should().Equal("Lemmings (1991)(Psygnosis).adf");

        directories.SourceDir = second;
        await directories.Preview.WhenIdleAsync();
        directories.Preview.Examples.Select(e => e.SourceName).Should().Equal("Another World (1991)(Delphine).adf", "Zool (1992)(Gremlin).adf");

        // No source: back to the built-in examples at once
        directories.SourceDir = string.Empty;
        directories.Preview.IsBuiltIn.Should().BeTrue();
        directories.Preview.IsListingSource.Should().BeFalse();
    }

    [Fact]
    public async Task Pattern_ShouldBeAppliedAfterThePauseInTyping()
    {
        var directories = CreateDirectories(TimeSpan.FromMilliseconds(100));
        directories.SelectedNamingConvention = NamingConvention.Tosec;
        var before = directories.Preview.Examples.Select(e => e.TargetPath).ToList();

        directories.Pattern = "{t";
        directories.Pattern = "{title}/";
        directories.Preview.Examples.Select(e => e.TargetPath).Should().Equal(before, "the preview waits for the end of the typing");

        await directories.Preview.WhenIdleAsync();
        directories.Preview.Examples[0].TargetPath.Should().Be(
            Path.Combine(_services.I18n.Tr("tab1.preview_destination"), "Lemmings", "Lemmings (1991)(Psygnosis)(FR)[!].adf"));
    }

    [Fact]
    public void PathOptions_ShouldRefreshThePreviewAtOnce()
    {
        var directories = CreateDirectories(TimeSpan.FromHours(1));
        directories.SelectedNamingConvention = NamingConvention.Tosec;
        string First() => directories.Preview.Examples[0].TargetPath;
        var destination = _tmp.GetFilePath("out");

        directories.DestinationDir = destination;
        First().Should().Be(Path.Combine(destination, "Psygnosis", "1991", "Lemmings", "Lemmings (1991)(Psygnosis)(FR)[!].adf"));

        directories.SelectedTransferMode = TransferMode.Zip;
        First().Should().EndWith(".zip");
        directories.SelectedTransferMode = TransferMode.Copy;

        directories.FlatDestination = true;
        First().Should().Be(Path.Combine(destination, "Lemmings (1991)(Psygnosis)(FR)[!].adf"));
        directories.FlatDestination = false;

        // The multi-disk example gets its subfolder, hidden when the playlists are placed in the parent folder
        string Second() => directories.Preview.Examples[1].TargetPath;
        Second().Should().Be(Path.Combine(destination, "Delphine", "1991", "Another World", "Another World (1991)(Delphine)(Disk 1 of 2).adf"));
        directories.GenerateM3u = true;
        directories.SelectedM3uPlacement = M3uPlacementMode.ParentHidden;
        Second().Should().Be(Path.Combine(destination, "Delphine", "1991", "Another World", ".Another World", "Another World (1991)(Delphine)(Disk 1 of 2).adf"));
        directories.SubfolderMultidisk = false;
        Second().Should().Be(Path.Combine(destination, "Delphine", "1991", "Another World", "Another World (1991)(Delphine)(Disk 1 of 2).adf"));

        // The convention changes the examples and the way they are read
        directories.SelectedNamingConvention = NamingConvention.NoIntro;
        directories.Preview.Examples[0].SourceName.Should().Be("Super Mario World (USA).sfc");
    }

    [Theory]
    [InlineData("{title}/{foo}/", "tab1.preview_unknown_variables")]
    [InlineData("{title/", "tab1.preview_unbalanced_braces")]
    [InlineData("_unmatched/{title}/", "tab1.preview_unmatched_folder")]
    [InlineData("_Unmatched/", "tab1.preview_unmatched_folder")]
    public void InvalidPattern_ShouldBeReportedWithoutException(string pattern, string warningKey)
    {
        var directories = CreateDirectories();
        directories.Pattern = pattern;

        var expected = warningKey == "tab1.preview_unknown_variables"
            ? _services.I18n.Tr(warningKey, ("variables", "{foo}"))
            : warningKey == "tab1.preview_unmatched_folder"
                ? _services.I18n.Tr(warningKey, ("folder", PathOrganizerService.UnmatchedFolderName))
                : _services.I18n.Tr(warningKey);
        directories.Preview.HasWarning.Should().BeTrue();
        directories.Preview.WarningText.Should().Be(expected);
        directories.Preview.Examples.Should().HaveCount(PatternPreviewViewModel.ExampleCount);

        // The pattern is not used at the root of the destination
        directories.FlatDestination = true;
        directories.Preview.HasWarning.Should().BeFalse();
    }

    [Fact]
    public void LanguageSwitch_ShouldTranslateThePreviewAndTheTransferModeHint()
    {
        var directories = CreateDirectories();
        directories.SelectedTransferMode = TransferMode.HardLink;
        directories.TransferModeHint.Should().Be(_services.I18n.Tr("tab1.mode_hint_hardlink"));

        _services.I18n.SetLocale("en");
        try
        {
            directories.RefreshTranslations();
            directories.TransferModeHint.Should().Be("Same volume required; no extra disk space.");
            directories.Preview.CaptionText.Should().Be("Examples (no source chosen):");
            directories.Preview.Examples[0].TargetPath.Should().StartWith("[destination]");
        }
        finally
        {
            _services.I18n.SetLocale("fr");
        }

        foreach (var mode in Enum.GetValues<TransferMode>())
        {
            directories.SelectedTransferMode = mode;
            directories.TransferModeHint.Should().NotBeNullOrEmpty().And.NotStartWith("tab1.");
        }
    }

    /// <summary>
    /// "At the root" is its own option: a config.json with flat_destination and a pattern keeps both, the
    /// pattern being shown greyed and used again once the option is unchecked.
    /// </summary>
    [Fact]
    public async Task ConfigurationAtTheRoot_ShouldKeepThePatternAndBeSavedIdentically()
    {
        var path = _tmp.WriteText("config.json", """{ "flat_destination": true, "pattern": "{country}/{title}/", "destination_dir": "out" }""");
        var vm = TestMainViewModel.Create(new HeadlessDialogService(), path);
        var directories = vm.Directories;

        directories.FlatDestination.Should().BeTrue();
        directories.Pattern.Should().Be("{country}/{title}/");
        directories.IsPatternEditingEnabled.Should().BeFalse("the pattern is greyed, not cleared");
        directories.Preview.Examples[0].TargetPath.Should().Be(Path.Combine("out", "Lemmings (1991)(Psygnosis)(FR)[!].adf"));
        directories.Preview.Examples[1].TargetPath.Should().Be(
            Path.Combine("out", "Another World", "Another World (1991)(Delphine)(Disk 1 of 2).adf"),
            "a multi-disk game keeps its subfolder at the root");

        await vm.SaveConfigurationAsync();
        using (var saved = JsonDocument.Parse(await File.ReadAllTextAsync(path)))
        {
            saved.RootElement.GetProperty("flat_destination").GetBoolean().Should().BeTrue();
            saved.RootElement.GetProperty("pattern").GetString().Should().Be("{country}/{title}/");
        }

        // Unchecking "At the root" uses the kept pattern again
        directories.FlatDestination = false;
        directories.Preview.Examples[0].TargetPath.Should().Be(
            Path.Combine("out", "FR", "Lemmings", "Lemmings (1991)(Psygnosis)(FR)[!].adf"));

        // A preset leaves the root as well
        directories.FlatDestination = true;
        directories.PresetNoIntroLetterCommand.Execute(null);
        directories.FlatDestination.Should().BeFalse();
        directories.Pattern.Should().Be(DirectoriesViewModel.NoIntroLetterPattern);
    }

    [Fact]
    public async Task MameConvention_ShouldShowDescribedSetsAndWarnAboutSubfolders()
    {
        // The name of a set says nothing of its game: the source files are not used as examples
        var directories = CreateDirectories();
        directories.DestinationDir = "out";
        directories.SelectedNamingConvention = NamingConvention.Mame;
        directories.SelectedTransferMode = TransferMode.Zip;
        directories.Pattern = "{publisher}/";

        // The source last, as in the other tests: without a UI thread, the end of its listing refreshes the preview on
        // another thread, which must not overlap the options set by the test
        directories.SourceDir = CreateSource("src", "sf2.zip", "pacman.zip");
        await directories.Preview.WhenIdleAsync();

        var preview = directories.Preview;
        preview.IsBuiltIn.Should().BeTrue();
        preview.CaptionText.Should().Be(_services.I18n.Tr("tab1.preview_builtin_mame"));
        preview.Examples[0].SourceName.Should().Be("sf2.zip");
        preview.Examples[0].TargetPath.Should().Be(Path.Combine("out", "Capcom", "sf2.zip"), "a set keeps its archive in ZIP mode");
        preview.WarningText.Should().Contain(_services.I18n.Tr("tab1.preview_mame_subfolders"));

        // At the root, where MAME looks for its sets
        directories.FlatDestination = true;
        preview.Examples[0].TargetPath.Should().Be(Path.Combine("out", "sf2.zip"));
        preview.HasWarning.Should().BeFalse();

        // The examples have the categories of catlist.ini, for the variables of the category file
        directories.FlatDestination = false;
        directories.Pattern = "{genre}/{category}";
        await directories.Preview.WhenIdleAsync();
        preview.Examples.Select(e => e.TargetPath).Should().Equal(
            Path.Combine("out", "Fighter", "Fighter - Versus", "sf2.zip"),
            Path.Combine("out", "Maze", "Maze - Collect", "pacman.zip"),
            Path.Combine("out", "Platform", "Platform - Shooter Scrolling", "mslug.zip"));
        preview.WarningText.Should().NotContain(_services.I18n.Tr("tab1.preview_unknown_variables", ("variables", "{genre}")));
    }
}
