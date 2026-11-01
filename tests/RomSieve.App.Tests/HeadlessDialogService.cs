using RomSieve.App.Abstractions;
using RomSieve.App.ViewModels;

namespace RomSieve.App.Tests;

/// <summary>
/// Dialog service of the tests: it shows nothing, keeps what would have been shown, and answers the confirmations
/// (yes by default). It stays out of the application, where it would accept a purge in the user's place.
/// </summary>
public sealed class HeadlessDialogService : IDialogService
{
    public string? NextPickedFolder { get; set; }
    public string? NextPickedOpenFile { get; set; }
    public string? NextPickedSaveFile { get; set; }
    public bool NextConfirmationResult { get; set; } = true;

    /// <summary>Answers of the next confirmations, in order; once empty, <see cref="NextConfirmationResult"/> answers.</summary>
    public Queue<bool> NextConfirmationResults { get; } = new();

    public List<(string Level, string Title, string Message)> ShownMessages { get; } = [];

    public List<string> OpenedUrls { get; } = [];

    /// <summary>Local files opened, in order.</summary>
    public List<string> OpenedFiles { get; } = [];

    /// <summary>Whether the system finds an application to open the next files.</summary>
    public bool NextOpenFileResult { get; set; } = true;

    /// <summary>Preferences windows shown, in order.</summary>
    public List<PreferencesViewModel> ShownPreferences { get; } = [];

    /// <summary>What the user does in the next preferences windows, before closing them.</summary>
    public Func<PreferencesViewModel, Task>? OnShowPreferences { get; set; }

    public Task<string?> PickFolderAsync(string title, string? initialDirectory = null) =>
        Task.FromResult(NextPickedFolder);

    public Task<string?> PickOpenFileAsync(
        string title,
        IReadOnlyList<FileDialogFilter>? filters = null,
        string? initialDirectory = null) =>
        Task.FromResult(NextPickedOpenFile);

    public Task<string?> PickSaveFileAsync(
        string title,
        string defaultFileName,
        IReadOnlyList<FileDialogFilter>? filters = null,
        string? initialDirectory = null) =>
        Task.FromResult(NextPickedSaveFile);

    public Task ShowInfoAsync(string title, string message)
    {
        ShownMessages.Add(("INFO", title, message));
        return Task.CompletedTask;
    }

    public Task ShowWarningAsync(string title, string message)
    {
        ShownMessages.Add(("WARN", title, message));
        return Task.CompletedTask;
    }

    public Task ShowErrorAsync(string title, string message)
    {
        ShownMessages.Add(("ERROR", title, message));
        return Task.CompletedTask;
    }

    public Task<bool> ShowConfirmationAsync(string title, string message)
    {
        ShownMessages.Add(("CONFIRM", title, message));
        return Task.FromResult(NextConfirmationResults.TryDequeue(out var answer) ? answer : NextConfirmationResult);
    }

    public Task OpenUrlAsync(string url)
    {
        OpenedUrls.Add(url);
        return Task.CompletedTask;
    }

    public Task<bool> OpenFileAsync(string path)
    {
        OpenedFiles.Add(path);
        return Task.FromResult(NextOpenFileResult);
    }

    public async Task ShowPreferencesAsync(PreferencesViewModel preferences)
    {
        ShownPreferences.Add(preferences);
        if (OnShowPreferences is not null)
        {
            await OnShowPreferences(preferences);
        }
    }
}
