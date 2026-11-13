using RomSieve.App.ViewModels;
using RomSieve.Core.Models;
using RomSieve.Core.Services;
using RomSieve.Core.Tests.Fixtures;
using static RomSieve.Core.Tests.Fixtures.PatchTestEncoder;

namespace RomSieve.App.Tests;

/// <summary>
/// The pattern of the patched versions in the interface: its field of the Output step, greyed out without a folder of
/// the patches, its variables and presets; the line of the live preview, which shows where the sort writes the patched
/// version of the first example, and its warnings; the state line of a refused pattern; the simulation it makes out of
/// date; the target of a patched version in the table.
/// </summary>
public sealed class PatchedPatternViewModelTests : IDisposable
{
    private readonly TempDirectoryFixture _temp = new();
    private readonly CoreTestServices _services = new("fr");

    private static readonly byte[] Japan = Data(1, 4096);
    private static readonly byte[] Usa = Data(2, 4096);

    public void Dispose() => _temp.Dispose();

    private static byte[] Data(int seed, int size)
    {
        var data = new byte[size];
        new Random(seed).NextBytes(data);
        return data;
    }

    private DirectoriesViewModel CreateDirectories() =>
        new(_services.I18n, new HeadlessDialogService(), _services.DatService, _services.NoIntroParser, _services.Organizer,
            _services.MameParser, _services.Frontends, TimeSpan.Zero);

    /// <summary>A Japanese and an American release of a game, a DAT of both, and a translation of the Japanese one.</summary>
    private (string Source, string Dat, string Patches) WriteCollection()
    {
        _temp.WriteBytes("src/Game (Japan).sfc", Japan);
        _temp.WriteBytes("src/Game (USA).sfc", Usa);
        var dat = _temp.WriteText("dat/snes.dat", $"""
            <?xml version="1.0"?><datafile><header><name>Nintendo - SNES</name><homepage>No-Intro</homepage></header>
            <game name="Game (Japan)"><description>Game (Japan)</description><rom name="Game (Japan).sfc" size="{Japan.Length}" crc="{Crc(Japan):x8}"/></game>
            <game name="Game (USA)"><description>Game (USA)</description><rom name="Game (USA).sfc" size="{Usa.Length}" crc="{Crc(Usa):x8}"/></game></datafile>
            """);
        _temp.WriteBytes("patches/Game (Japan) (T-En).bps", BpsLinear(Japan, [.. Japan, .. Ascii("English")]));
        return (_temp.GetFilePath("src"), dat, _temp.GetFilePath("patches"));
    }

    // =========================================================================
    // The field of the Output step
    // =========================================================================

    [Fact]
    public void TheField_ShouldBeGreyedOutWithoutPatchFolder_AndKeepItsValue()
    {
        var directories = CreateDirectories();
        directories.PatchedPattern = "Patchs/{title}";

        directories.IsPatchedPatternEnabled.Should().BeFalse("nothing patched is written without a folder of the patches");
        directories.InsertPatchedVariableAtCaret("{dat}");
        directories.PatchedPattern.Should().Be("Patchs/{title}", "a greyed out field takes no variable");

        directories.PatchDir = _temp.CreateSubdirectory("patches");
        directories.IsPatchedPatternEnabled.Should().BeTrue();
        directories.PatchDir = string.Empty;
        directories.IsPatchedPatternEnabled.Should().BeFalse();
        directories.PatchedPattern.Should().Be("Patchs/{title}", "the value is kept, and saved");

        // Nor while a run goes on
        directories.PatchDir = _temp.GetFilePath("patches");
        directories.IsRunning = true;
        directories.IsPatchedPatternEnabled.Should().BeFalse();
        directories.IsRunning = false;
        directories.IsPatchedPatternEnabled.Should().BeTrue();
    }

    [Fact]
    public void ThePresets_ShouldReplaceThePattern_WithTheFolderNameOfTheLanguage()
    {
        var directories = CreateDirectories();
        directories.PatchDir = _temp.CreateSubdirectory("patches");

        directories.PresetPatchedSubfolderCommand.Execute(null);
        directories.PatchedPattern.Should().Be("{origin_folder}/Patchs");
        directories.PresetPatchedRootCommand.Execute(null);
        directories.PatchedPattern.Should().Be("Patchs/{dat}/{title}");
        directories.PresetPatchedNextToOriginCommand.Execute(null);
        directories.PatchedPattern.Should().BeEmpty();

        var english = new DirectoriesViewModel(new LocalizationService("en"), new HeadlessDialogService(), _services.DatService,
            _services.NoIntroParser, _services.Organizer, _services.MameParser, _services.Frontends, TimeSpan.Zero);
        english.PresetPatchedSubfolderCommand.Execute(null);
        english.PatchedPattern.Should().Be("{origin_folder}/Patches");

        // Every preset is accepted by the sort
        foreach (var pattern in new[] { "{origin_folder}/Patchs", "Patchs/{dat}/{title}", "{origin_folder}/Patches" })
        {
            PathOrganizerService.CheckPatchedPattern(pattern).Should().Be(PatchedPatternProblem.None, pattern);
        }
    }

    [Fact]
    public void AVariable_ShouldBeInsertedAtTheCaretOfItsOwnField()
    {
        var directories = CreateDirectories();
        directories.PatchDir = _temp.CreateSubdirectory("patches");
        directories.Pattern = "{title}/";

        // Without a caret, a new folder at the end
        directories.PatchedPatternVariables.Single(v => v.Tag == "{origin_folder}").InsertCommand.Execute(null);
        directories.PatchedPattern.Should().Be("{origin_folder}/");
        directories.PatchedPatternCaretIndex.Should().Be("{origin_folder}".Length);

        // At the caret placed by the user, the main pattern untouched
        directories.PatchedPattern = "Patchs/";
        directories.PatchedPatternCaretIndex = 0;
        directories.PatchedPatternVariables.Single(v => v.Tag == "{dat}").InsertCommand.Execute(null);
        directories.PatchedPattern.Should().Be("{dat}Patchs/");
        directories.PatchedPatternCaretIndex.Should().Be("{dat}".Length);
        directories.Pattern.Should().Be("{title}/");

        // The menu of the main pattern does not write into this one
        directories.PatternVariables.Single(v => v.Tag == "{country}").InsertCommand.Execute(null);
        directories.PatchedPattern.Should().Be("{dat}Patchs/");
        directories.Pattern.Should().Be("{title}/{country}/");
    }

    [Fact]
    public void TheVariables_ShouldBeThoseOfThePattern_ThenTheFoldersOfTheOriginalRom()
    {
        var directories = CreateDirectories();

        directories.PatchedPatternVariables.Select(v => v.Tag).Should().Equal(
            [.. directories.PatternVariables.Select(v => v.Tag), "{origin_folder}", "{dat_folder}"]);
        directories.PatternVariables.Select(v => v.Tag).Should().NotContain("{origin_folder}", "the main pattern does not know them");
        directories.PatchedPatternVariables.Single(v => v.Tag == "{origin_folder}").IsApplicable.Should().BeTrue();

        // {dat_folder} is worth a folder in a lot with a folder per DAT only: greyed out otherwise, never hidden
        var datFolder = directories.PatchedPatternVariables.Single(v => v.Tag == "{dat_folder}");
        datFolder.IsApplicable.Should().BeFalse();
        datFolder.Tooltip.Should().Be($"{_services.I18n.Tr("var.tooltip_dat_folder")}\n{_services.I18n.Tr("var.hint_dat_folder_only")}");
        directories.DatFilePath = _temp.CreateSubdirectory("dats");
        datFolder.IsApplicable.Should().BeTrue();
        datFolder.Tooltip.Should().Be(_services.I18n.Tr("var.tooltip_dat_folder"));
        directories.FolderPerDat = false;
        datFolder.IsApplicable.Should().BeFalse();

        // The variables of TOSEC names follow the convention, as in the menu of the main pattern
        directories.SelectedNamingConvention = NamingConvention.NoIntro;
        directories.PatchedPatternVariables.Single(v => v.Tag == "{publisher}").IsApplicable.Should().BeFalse();

        // Translated with the interface
        directories.PatchedPatternVariables[^2].Label.Should().Be("Dossier de la ROM d'origine");
    }

    // =========================================================================
    // The live preview
    // =========================================================================

    /// <summary>The line of the patched version shows where a simulation of the same options writes it.</summary>
    [Theory]
    [InlineData("", false)]
    [InlineData("Patchs/{title}", false)]
    [InlineData("Patchs/{dat}/{title}", true)]
    [InlineData("{origin_folder}/Patchs", false)]
    [InlineData("{origin_folder}/Patchs", true)]
    [InlineData("{dat_folder}/Hacks/{title}", false)]
    public async Task ThePatchedExample_ShouldBeWhereTheSortWritesThePatchedVersion(string pattern, bool flat)
    {
        var (source, dat, patches) = WriteCollection();
        var directories = CreateDirectories();
        directories.SelectedNamingConvention = NamingConvention.NoIntro;
        directories.DestinationDir = _temp.GetFilePath("dst");
        directories.Pattern = "{country}/{title}/";
        directories.FlatDestination = flat;
        directories.DatFilePath = dat;
        await directories.DatLoadTask;
        directories.PatchDir = patches;
        directories.PatchedPattern = pattern;
        directories.SourceDir = source;
        await directories.Preview.WhenIdleAsync();

        var settings = new SortSettings();
        directories.WriteTo(settings);
        var result = await _services.Processor.ExecuteAsync(SortSettingsSerializer.ToSortConfig(settings), isDryRun: true);
        var patched = result.Items.Single(i => i.Patch is not null);

        var preview = directories.Preview;
        preview.Examples[0].SourceName.Should().Be("Game (Japan).sfc", "the patched version is that of the first example");
        patched.TargetFilename.Should().Be("Game (Japan) [T-En].sfc", "the example is named as the sort names it");
        preview.HasPatchedExample.Should().BeTrue();
        preview.PatchedExampleText.Should().Be(_services.I18n.Tr("tab1.preview_patched_example", ("path", patched.TargetPath)));
        preview.PatchedExampleTooltip.Should().Be(
            _services.I18n.Tr("tab1.preview_example_tooltip", ("source", "Game (Japan) [T-En].sfc"), ("target", patched.TargetPath)));
        preview.HasWarning.Should().BeFalse();
    }

    [Fact]
    public void ThePatchedExample_ShouldOnlyBeShownWithAPatchFolder_OutOfMame()
    {
        var directories = CreateDirectories();
        directories.SelectedNamingConvention = NamingConvention.NoIntro;
        directories.PatchedPattern = "Patchs/{title}";
        directories.Preview.HasPatchedExample.Should().BeFalse("nothing patched is written without a folder of the patches");
        directories.Preview.PatchedExampleText.Should().BeEmpty();

        directories.Preview.Examples.Should().HaveCount(PatternPreviewViewModel.ExampleCount);

        // At once, as the other options of the paths, in place of the last example: the preview keeps its height
        directories.PatchDir = _temp.CreateSubdirectory("patches");
        directories.Preview.PatchedExampleText.Should().Be(_services.I18n.Tr("tab1.preview_patched_example", ("path",
            Path.Combine(_services.I18n.Tr("tab1.preview_destination"), "Patchs", "Super Mario World", "Super Mario World (USA) [T-En].sfc"))));
        directories.Preview.Examples.Should().HaveCount(PatternPreviewViewModel.ExampleCount - 1);
        directories.Preview.Examples[0].SourceName.Should().Be("Super Mario World (USA).sfc", "the patched version is that of the first example");

        // A TOSEC name takes its label without a space, also when the convention is detected
        foreach (var convention in new[] { NamingConvention.Tosec, NamingConvention.Auto })
        {
            directories.SelectedNamingConvention = convention;
            directories.Preview.PatchedExampleText.Should().EndWith(
                Path.Combine("Patchs", "Lemmings", "Lemmings (1991)(Psygnosis)(FR)[!][T-En].adf"), convention.ToString());
        }

        // A MAME set or a software is never patched
        directories.SelectedNamingConvention = NamingConvention.Mame;
        directories.Preview.HasPatchedExample.Should().BeFalse();
        directories.SelectedNamingConvention = NamingConvention.MameSoftwareList;
        directories.Preview.HasPatchedExample.Should().BeFalse();
    }

    [Fact]
    public async Task ThePatchedPattern_ShouldRefreshThePreviewAfterThePauseInTyping()
    {
        var directories = new DirectoriesViewModel(_services.I18n, new HeadlessDialogService(), _services.DatService,
            _services.NoIntroParser, _services.Organizer, _services.MameParser, _services.Frontends, TimeSpan.FromMilliseconds(100));
        directories.SelectedNamingConvention = NamingConvention.NoIntro;
        directories.PatchDir = _temp.CreateSubdirectory("patches");
        var before = directories.Preview.PatchedExampleText;

        directories.PatchedPattern = "Pat";
        directories.PatchedPattern = "Patchs";
        directories.Preview.PatchedExampleText.Should().Be(before, "the preview waits for the end of the typing");

        await directories.Preview.WhenIdleAsync();
        directories.Preview.PatchedExampleText.Should().Contain(Path.Combine("Patchs", "Super Mario World (USA) [T-En].sfc"));
    }

    [Theory]
    [InlineData("/Patchs", "tab1.preview_patched_rooted")]
    [InlineData("\\Patchs", "tab1.preview_patched_rooted")]
    [InlineData("Patchs/C:/x", "tab1.preview_patched_drive")]
    [InlineData("Patchs/../x", "tab1.preview_patched_parent_segment")]
    [InlineData("Pat?chs", "tab1.preview_patched_forbidden_character")]
    [InlineData("_purged/{title}", "tab1.preview_patched_reserved_folder")]
    [InlineData("{dat_folder}\\.romsieve", "tab1.preview_patched_reserved_folder")]
    [InlineData("{origin_folder}-patchs", "tab1.preview_patched_folder_variable_not_alone")]
    public void ARefusedPattern_ShouldBeSaid_WithoutAPatchedExample(string pattern, string warningKey)
    {
        var directories = CreateDirectories();
        directories.PatchedPattern = pattern;
        directories.Preview.HasWarning.Should().BeFalse("without a folder of the patches, the pattern is greyed out and refuses nothing");

        directories.PatchDir = _temp.CreateSubdirectory("patches");

        directories.Preview.WarningText.Should().Be(_services.I18n.Tr(warningKey));
        directories.Preview.HasPatchedExample.Should().BeFalse("the sort would write nothing");
        directories.Preview.Examples.Should().HaveCount(PatternPreviewViewModel.ExampleCount, "the examples of the main pattern stay");
    }

    [Theory]
    [InlineData("Patchs/{foo}/", "tab1.preview_patched_unknown_variables")]
    [InlineData("Patchs/{title/", "tab1.preview_patched_unbalanced_braces")]
    [InlineData("_unmatched/{title}", "tab1.preview_patched_unmatched_folder")]
    public void AnAcceptedPatternThatWouldNotDoWhatItSays_ShouldBeWarned_WithItsExample(string pattern, string warningKey)
    {
        var directories = CreateDirectories();
        directories.PatchDir = _temp.CreateSubdirectory("patches");

        directories.PatchedPattern = pattern;

        var expected = warningKey switch
        {
            "tab1.preview_patched_unknown_variables" => _services.I18n.Tr(warningKey, ("variables", "{foo}")),
            "tab1.preview_patched_unmatched_folder" => _services.I18n.Tr(warningKey, ("folder", PathOrganizerService.UnmatchedFolderName)),
            _ => _services.I18n.Tr(warningKey)
        };
        directories.Preview.WarningText.Should().Be(expected);
        directories.Preview.HasPatchedExample.Should().BeTrue("the sort accepts the pattern");
    }

    [Fact]
    public void TheFolderVariables_ShouldBeKnownToThePatchedPatternOnly()
    {
        var directories = CreateDirectories();
        directories.PatchDir = _temp.CreateSubdirectory("patches");

        directories.PatchedPattern = "{dat_folder}/{origin_folder}/x";
        directories.Preview.HasWarning.Should().BeFalse();

        directories.Pattern = "{origin_folder}/";
        directories.Preview.WarningText.Should().Be(_services.I18n.Tr("tab1.preview_unknown_variables", ("variables", "{origin_folder}")));
    }

    // =========================================================================
    // The main window: state line, simulation, table
    // =========================================================================

    private MainViewModel CreateMainViewModel(HeadlessDialogService? dialogs = null)
    {
        var (source, dat, patches) = WriteCollection();
        var vm = TestMainViewModel.Create(dialogs ?? new HeadlessDialogService(), _temp.WriteText("config/config.json", "{}"));
        vm.Directories.SelectedNamingConvention = NamingConvention.NoIntro;
        vm.Directories.SourceDir = source;
        vm.Directories.DatFilePath = dat;
        vm.Directories.DestinationDir = _temp.GetFilePath("dst");
        vm.Directories.FlatDestination = false;
        vm.Directories.Pattern = "{title}/";
        vm.Directories.PatchDir = patches;
        return vm;
    }

    [Fact]
    public async Task ARefusedPattern_ShouldBeSaidUnderTheOutputStep_WithItsReason()
    {
        var vm = CreateMainViewModel();
        await vm.Directories.DatLoadTask;
        vm.Navigation.OutputEntry.IndicatorTone.Should().Be(StepIndicatorTone.None);

        vm.Directories.PatchedPattern = "../Patchs";

        vm.Navigation.OutputEntry.IndicatorTone.Should().Be(StepIndicatorTone.Warning);
        vm.Navigation.OutputEntry.IndicatorText.Should().Be("Patchées : dossier refusé");
        vm.Navigation.OutputEntry.IndicatorTooltip.Should().Be(_services.I18n.Tr("dialog.error_patched_pattern_invalid"));

        // A destination that overlaps the patches is said first, as the run refuses it first
        vm.Directories.DestinationDir = Path.Combine(_temp.Path, "patches", "sorted");
        vm.Navigation.OutputEntry.IndicatorText.Should().Be("Chevauche les patchs");
        vm.Directories.DestinationDir = _temp.GetFilePath("dst");

        // Without a folder of the patches, the pattern refuses nothing
        vm.Directories.PatchDir = string.Empty;
        vm.Navigation.OutputEntry.IndicatorTone.Should().Be(StepIndicatorTone.None);
        vm.Navigation.OutputEntry.IndicatorTooltip.Should().BeNull();
    }

    [Fact]
    public async Task ChangingThePattern_ShouldMakeTheSimulationOutOfDate()
    {
        var vm = CreateMainViewModel();
        await vm.Directories.DatLoadTask;
        await vm.Execution.SimulateAsync();
        vm.Execution.IsSimulationCurrent.Should().BeTrue();

        vm.Directories.PatchedPattern = "Patchs/{title}";
        vm.Execution.IsSimulationCurrent.Should().BeFalse("the patched versions would go elsewhere");

        vm.Directories.PatchedPattern = string.Empty;
        vm.Execution.IsSimulationCurrent.Should().BeTrue("the options are those of the simulation again");
    }

    [Fact]
    public async Task TheTable_ShouldShowThePatchedVersionAtItsNewPlace_AndTheOriginalAtItsOwn()
    {
        var vm = CreateMainViewModel();
        await vm.Directories.DatLoadTask;
        vm.Directories.PatchedPattern = "Patchs/{title}";

        await vm.Execution.SimulateAsync();

        var destination = _temp.GetFilePath("dst");
        var patched = vm.AllPreviewItems.Single(r => r.IsPatched);
        patched.FullTargetPath.Should().Be(Path.Combine(destination, "Patchs", "Game", "Game (Japan) [T-En].sfc"));
        patched[8].Should().Contain(Path.Combine("Patchs", "Game", "Game (Japan) [T-En].sfc"), "the column of the target shows it");
        vm.AllPreviewItems.Single(r => r.SourceName == "Game (USA).sfc").FullTargetPath
            .Should().Be(Path.Combine(destination, "Game", "Game (USA).sfc"));

        // And the preview showed it before the simulation
        await vm.Directories.Preview.WhenIdleAsync();
        vm.Directories.Preview.PatchedExampleText.Should().Be(_services.I18n.Tr("tab1.preview_patched_example", ("path", patched.FullTargetPath)));
    }
}
