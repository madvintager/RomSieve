using RomSieve.App.ViewModels;
using RomSieve.Core.Abstractions;

namespace RomSieve.App.Abstractions;

/// <summary>
/// File selection filter for open or save dialog boxes: one entry of the list of file types, so its name describes
/// that entry alone (the pickers do not split a Qt-style "A (*.a);;B (*.b)" name into several entries).
/// </summary>
public sealed record FileDialogFilter(string Name, IReadOnlyList<string> Extensions)
{
    /// <summary>Extension that matches every file, with or without an extension.</summary>
    public const string AnyExtension = "*";

    /// <summary>Filter that shows every file, offered after the specific ones of an open dialog.</summary>
    public static FileDialogFilter AllFiles(ILocalizationService localization) =>
        new(localization.Tr("dialog.all_files_filter"), [AnyExtension]);
}

/// <summary>
/// Abstraction for dialog boxes and file/folder pickers to decouple
/// <c>MainViewModel</c> from the graphical framework (Avalonia, WPF, headless unit tests).
/// </summary>
public interface IDialogService
{
    Task<string?> PickFolderAsync(string title, string? initialDirectory = null);

    Task<string?> PickOpenFileAsync(
        string title,
        IReadOnlyList<FileDialogFilter>? filters = null,
        string? initialDirectory = null);

    Task<string?> PickSaveFileAsync(
        string title,
        string defaultFileName,
        IReadOnlyList<FileDialogFilter>? filters = null,
        string? initialDirectory = null);

    Task ShowInfoAsync(string title, string message);

    Task ShowWarningAsync(string title, string message);

    Task ShowErrorAsync(string title, string message);

    Task<bool> ShowConfirmationAsync(string title, string message);

    Task OpenUrlAsync(string url);

    /// <summary>Opens a local file with the application the system associates with it; false if none was launched.</summary>
    Task<bool> OpenFileAsync(string path);

    /// <summary>Shows the preferences window and returns once it is closed; its changes apply at once.</summary>
    Task ShowPreferencesAsync(PreferencesViewModel preferences);
}
