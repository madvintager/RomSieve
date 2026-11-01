using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using RomSieve.App.Abstractions;
using RomSieve.App.ViewModels;
using RomSieve.App.Views;

namespace RomSieve.App.Services;

/// <summary>
/// Avalonia UI implementation of <see cref="IDialogService"/> using <see cref="IStorageProvider"/>
/// for file/folder pickers and lightweight modal windows for confirmations and alerts. The texts of its buttons are in
/// the language of the <see cref="MainViewModel"/> of the owner window.
/// </summary>
public sealed class AvaloniaDialogService(Func<Window?> ownerProvider) : IDialogService
{
    /// <summary>Preferences window currently shown: the messages it triggers (update check) are shown above it.</summary>
    private Window? _preferencesWindow;

    public async Task<string?> PickFolderAsync(string title, string? initialDirectory = null)
    {
        var owner = ownerProvider();
        if (owner?.StorageProvider is null)
        {
            return null;
        }

        IStorageFolder? startLocation = null;
        if (!string.IsNullOrWhiteSpace(initialDirectory) && Directory.Exists(initialDirectory))
        {
            startLocation = await owner.StorageProvider.TryGetFolderFromPathAsync(initialDirectory);
        }

        var folders = await owner.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = title,
            AllowMultiple = false,
            SuggestedStartLocation = startLocation
        });

        return folders.FirstOrDefault()?.TryGetLocalPath();
    }

    public async Task<string?> PickOpenFileAsync(
        string title,
        IReadOnlyList<RomSieve.App.Abstractions.FileDialogFilter>? filters = null,
        string? initialDirectory = null)
    {
        var owner = ownerProvider();
        if (owner?.StorageProvider is null)
        {
            return null;
        }

        IStorageFolder? startLocation = null;
        if (!string.IsNullOrWhiteSpace(initialDirectory) && Directory.Exists(initialDirectory))
        {
            startLocation = await owner.StorageProvider.TryGetFolderFromPathAsync(initialDirectory);
        }

        var files = await owner.StorageProvider.OpenFilePickerAsync(CreateOpenFilePickerOptions(title, filters, startLocation));

        return files.FirstOrDefault()?.TryGetLocalPath();
    }

    internal static FilePickerOpenOptions CreateOpenFilePickerOptions(
        string title,
        IReadOnlyList<RomSieve.App.Abstractions.FileDialogFilter>? filters,
        IStorageFolder? startLocation) => new()
        {
            Title = title,
            AllowMultiple = false,
            SuggestedStartLocation = startLocation,
            FileTypeFilter = ToFileTypes(filters)
        };

    /// <summary>One file type per filter, in the same order: each filter is an entry of the list of the picker.</summary>
    internal static List<FilePickerFileType>? ToFileTypes(IReadOnlyList<RomSieve.App.Abstractions.FileDialogFilter>? filters) =>
        filters?
            .Select(f => new FilePickerFileType(f.Name)
            {
                // "*" rather than "*.*": a file without an extension is a file too
                Patterns = f.Extensions
                    .Select(ext => ext == RomSieve.App.Abstractions.FileDialogFilter.AnyExtension ? "*" : $"*.{ext.TrimStart('.')}")
                    .ToList()
            })
            .ToList();

    public async Task<string?> PickSaveFileAsync(
        string title,
        string defaultFileName,
        IReadOnlyList<RomSieve.App.Abstractions.FileDialogFilter>? filters = null,
        string? initialDirectory = null)
    {
        var owner = ownerProvider();
        if (owner?.StorageProvider is null)
        {
            return null;
        }

        IStorageFolder? startLocation = null;
        if (!string.IsNullOrWhiteSpace(initialDirectory) && Directory.Exists(initialDirectory))
        {
            startLocation = await owner.StorageProvider.TryGetFolderFromPathAsync(initialDirectory);
        }

        var options = CreateSaveFilePickerOptions(title, defaultFileName, filters, startLocation);
        var file = await owner.StorageProvider.SaveFilePickerAsync(options);

        return file?.TryGetLocalPath();
    }

    internal static FilePickerSaveOptions CreateSaveFilePickerOptions(
        string title,
        string defaultFileName,
        IReadOnlyList<RomSieve.App.Abstractions.FileDialogFilter>? filters,
        IStorageFolder? startLocation)
    {
        var fileTypes = ToFileTypes(filters);

        string? defaultExtension = null;
        var suggestedFileName = defaultFileName;

        if (filters != null && filters.Count > 0)
        {
            var matchingFilterExt = filters
                .SelectMany(f => f.Extensions)
                .Select(e => "." + e.TrimStart('.'))
                .FirstOrDefault(e => defaultFileName.EndsWith(e, StringComparison.OrdinalIgnoreCase));

            if (matchingFilterExt != null)
            {
                var stripped = defaultFileName[..^matchingFilterExt.Length];
                if (!string.IsNullOrWhiteSpace(stripped))
                {
                    suggestedFileName = stripped;
                }

                defaultExtension = matchingFilterExt.TrimStart('.');
            }
            else
            {
                defaultExtension = filters[0].Extensions.FirstOrDefault()?.TrimStart('.');
            }
        }
        else if (Path.HasExtension(defaultFileName))
        {
            defaultExtension = Path.GetExtension(defaultFileName).TrimStart('.');
        }

        return new FilePickerSaveOptions
        {
            Title = title,
            SuggestedFileName = suggestedFileName,
            DefaultExtension = defaultExtension,
            SuggestedStartLocation = startLocation,
            FileTypeChoices = fileTypes,
            ShowOverwritePrompt = true
        };
    }

    public Task ShowInfoAsync(string title, string message) =>
        ShowMessageDialogAsync(title, message, isConfirmation: false);

    public Task ShowWarningAsync(string title, string message) =>
        ShowMessageDialogAsync(title, message, isConfirmation: false);

    public Task ShowErrorAsync(string title, string message) =>
        ShowMessageDialogAsync(title, message, isConfirmation: false);

    public Task<bool> ShowConfirmationAsync(string title, string message) =>
        ShowMessageDialogAsync(title, message, isConfirmation: true);

    private async Task<bool> ShowMessageDialogAsync(string title, string message, bool isConfirmation)
    {
        var owner = _preferencesWindow ?? ownerProvider();
        var i18n = (ownerProvider()?.DataContext as MainViewModel)?.Localization;
        if (owner is null || !owner.IsVisible || i18n is null)
        {
            // Nobody can answer: a confirmation is refused, never accepted in the user's place
            return false;
        }

        var result = false;
        var dialog = new Window
        {
            Title = title,
            Width = 480,
            SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            CanResize = false
        };

        var cleanText = System.Text.RegularExpressions.Regex.Replace(message ?? string.Empty, "<br\\s*/?>", Environment.NewLine);
        cleanText = System.Text.RegularExpressions.Regex.Replace(cleanText, "<[^>]+>", string.Empty);

        var okButton = new Button
        {
            Content = isConfirmation ? i18n.Tr("dialog.btn_yes") : i18n.Tr("dialog.btn_ok"),
            MinWidth = 85,
            HorizontalContentAlignment = HorizontalAlignment.Center
        };
        okButton.Click += (_, _) =>
        {
            result = true;
            dialog.Close();
        };

        var buttonPanel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 8
        };
        buttonPanel.Children.Add(okButton);

        if (isConfirmation)
        {
            var cancelButton = new Button
            {
                Content = i18n.Tr("dialog.btn_cancel"),
                MinWidth = 85,
                HorizontalContentAlignment = HorizontalAlignment.Center
            };
            cancelButton.Click += (_, _) =>
            {
                result = false;
                dialog.Close();
            };
            buttonPanel.Children.Add(cancelButton);
        }

        dialog.Content = new StackPanel
        {
            Margin = new Thickness(16),
            Spacing = 14,
            Children =
            {
                new TextBlock
                {
                    Text = cleanText,
                    TextWrapping = TextWrapping.Wrap
                },
                buttonPanel
            }
        };

        await dialog.ShowDialog(owner);
        return result;
    }

    public async Task ShowPreferencesAsync(PreferencesViewModel preferences)
    {
        var owner = ownerProvider();
        if (owner is null || !owner.IsVisible || _preferencesWindow is not null)
        {
            return;
        }

        var window = new PreferencesWindow { DataContext = preferences };
        _preferencesWindow = window;
        try
        {
            await window.ShowDialog(owner);
        }
        finally
        {
            _preferencesWindow = null;
        }
    }

    public async Task OpenUrlAsync(string url)
    {
        var owner = ownerProvider();
        Uri.TryCreate(url, UriKind.Absolute, out var uri);
        if (owner is not null && uri is not null)
        {
            try
            {
                var topLevel = TopLevel.GetTopLevel(owner);
                if (topLevel?.Launcher is not null)
                {
                    var launched = await topLevel.Launcher.LaunchUriAsync(uri);
                    if (launched)
                    {
                        return;
                    }
                }
            }
            catch
            {
                // Fallback below
            }
        }

        // Fallback when the platform launcher is unavailable; only a web address is handed to the system
        if (uri is null || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp))
        {
            return;
        }

        try
        {
            if (OperatingSystem.IsWindows())
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
            }
            else if (OperatingSystem.IsLinux())
            {
                // The address is a single argument: it is never split or interpreted by a shell
                var startInfo = new System.Diagnostics.ProcessStartInfo("xdg-open");
                startInfo.ArgumentList.Add(uri.AbsoluteUri);
                System.Diagnostics.Process.Start(startInfo);
            }
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            // No browser could be launched: there is nothing more to do
        }
    }

    public async Task<bool> OpenFileAsync(string path)
    {
        var owner = ownerProvider();
        var launcher = owner is null ? null : TopLevel.GetTopLevel(owner)?.Launcher;
        if (launcher is null)
        {
            return false;
        }

        try
        {
            return await launcher.LaunchFileInfoAsync(new FileInfo(path));
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            // No application is associated with the file: the caller tells where it is
            return false;
        }
    }
}
