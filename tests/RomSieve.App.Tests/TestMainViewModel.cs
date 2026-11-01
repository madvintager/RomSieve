using RomSieve.App.Abstractions;
using RomSieve.App.ViewModels;
using RomSieve.Core.Abstractions;
using RomSieve.Core.Services;

namespace RomSieve.App.Tests;

/// <summary>
/// Builds the <see cref="MainViewModel"/> as in production, but in French: the assertions of the tests check the
/// French texts, while the application starts in English. A <c>ui_locale</c> of the configuration still applies.
/// Without a dialog service, a <see cref="HeadlessDialogService"/> answers the dialogs.
/// </summary>
internal static class TestMainViewModel
{
    public const string Locale = "fr";

    public static MainViewModel Create(
        IDialogService? dialogService = null,
        string? configFilePath = null,
        IUpdateCheckService? updateCheckService = null) =>
        AppServiceCollectionExtensions.CreateMainViewModel(
            dialogService ?? new HeadlessDialogService(), configFilePath, updateCheckService, new LocalizationService(Locale));
}
