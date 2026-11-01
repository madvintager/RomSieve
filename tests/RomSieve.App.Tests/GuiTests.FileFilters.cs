using RomSieve.App.Services;
using AppFileDialogFilter = RomSieve.App.Abstractions.FileDialogFilter;

namespace RomSieve.App.Tests;

/// <summary>
/// The open dialogs offer "All files" as an entry of their list of file types: its name used to be glued to the first
/// filter in a Qt-style "Category files (*.ini);;All files (*)" name, which Avalonia shows as a single entry.
/// </summary>
public sealed partial class GuiTests
{
    public static TheoryData<string> BrowseFileCommands => ["dat", "categories", "ratings"];

    [Theory]
    [MemberData(nameof(BrowseFileCommands))]
    public async Task BrowseFile_ShouldOfferItsFileTypeThenAllFiles(string command)
    {
        IReadOnlyList<AppFileDialogFilter>? offered = null;
        var dialog = new ConfigurableDialogService
        {
            OnPickOpenFile = (_, filters, _) =>
            {
                offered = filters;
                return null;
            }
        };
        var vm = CreateViewModel(dialog);

        await (command switch
        {
            "dat" => vm.Directories.BrowseDatAsync(),
            "categories" => vm.Directories.BrowseMameCategoryFileAsync(),
            _ => vm.Directories.BrowseMameRatingFileAsync()
        });

        offered.Should().NotBeNull().And.HaveCount(2);
        offered![0].Extensions.Should().NotContain(AppFileDialogFilter.AnyExtension);
        offered[1].Name.Should().Be("Tous les fichiers (*)");
        offered[1].Extensions.Should().Equal(AppFileDialogFilter.AnyExtension);
        offered.Should().OnlyContain(f => !f.Name.Contains(";;"));
    }

    [Fact]
    public void AvaloniaDialogService_OpenFilePickerOptions_ShouldGiveOneFileTypePerFilter()
    {
        AppFileDialogFilter[] filters =
        [
            new("Category files (*.ini)", ["ini"]),
            new("All files (*)", [AppFileDialogFilter.AnyExtension])
        ];

        var options = AvaloniaDialogService.CreateOpenFilePickerOptions("Select", filters, null);

        options.AllowMultiple.Should().BeFalse();
        options.FileTypeFilter.Should().HaveCount(2);
        options.FileTypeFilter![0].Name.Should().Be("Category files (*.ini)");
        options.FileTypeFilter[0].Patterns.Should().Equal("*.ini");
        options.FileTypeFilter[1].Name.Should().Be("All files (*)");
        // "*" and not "*.*", which would hide the files without an extension
        options.FileTypeFilter[1].Patterns.Should().Equal("*");
    }
}
