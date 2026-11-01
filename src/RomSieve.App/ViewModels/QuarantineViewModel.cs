using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RomSieve.App.Abstractions;
using RomSieve.Core.Abstractions;
using RomSieve.Core.Models;
using RomSieve.Core.Services;

namespace RomSieve.App.ViewModels;

/// <summary>
/// Quarantine of the purges of the destination, shown in the Destination frame of the Output step: its size, measured
/// off the UI thread (<see cref="LoadAsync"/>), and a command that deletes it for good after a confirmation. A purge
/// never deletes anything, so without this the quarantine of a destination purged again and again only grows.
/// Only the quarantine of a destination marked by RomSieve is ever measured or emptied (<see cref="DestinationGuard"/>).
/// The line is shown as soon as a destination is chosen, even with nothing to empty, so that the user learns that purges
/// are set aside and can be emptied here.
/// </summary>
public sealed partial class QuarantineViewModel : ViewModelBase
{
    private readonly ILocalizationService _localization;
    private readonly IDialogService _dialogService;
    private readonly LogViewModel _log;
    private string _destination = string.Empty;
    private int _loadVersion;

    public QuarantineViewModel(ILocalizationService localization, IDialogService dialogService, LogViewModel log)
    {
        _localization = localization ?? throw new ArgumentNullException(nameof(localization));
        _dialogService = dialogService ?? throw new ArgumentNullException(nameof(dialogService));
        _log = log ?? throw new ArgumentNullException(nameof(log));
    }

    /// <summary>Quarantine of the destination, or <c>null</c> when it has none (or it is empty, or not marked).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasQuarantine))]
    [NotifyPropertyChangedFor(nameof(SummaryText))]
    [NotifyPropertyChangedFor(nameof(EmptyTooltip))]
    [NotifyCanExecuteChangedFor(nameof(EmptyCommand))]
    private QuarantineInfo? _info;

    /// <summary>Whether a destination is chosen: the quarantine line is then shown, empty or not.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SummaryText))]
    private bool _hasDestination;

    /// <summary>Set while a sort, a simulation or an undo runs: the quarantine is then neither measured again nor emptied.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(EmptyCommand))]
    private bool _isRunning;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(EmptyCommand))]
    private bool _isEmptying;

    public bool HasQuarantine => Info is not null;

    /// <summary>What the quarantine holds (purges, files and size), or that it is empty; nothing without a destination.</summary>
    public string SummaryText =>
        Info is { } info
            ? _localization.Tr(
                "quarantine.summary",
                ("purges", info.PurgeCount),
                ("files", info.FileCount),
                ("size", SizeFormatting.Format(_localization, info.TotalBytes)))
            : HasDestination ? _localization.Tr("quarantine.summary_empty") : string.Empty;

    /// <summary>Tooltip of the button: what emptying does, or why there is nothing to empty.</summary>
    public string EmptyTooltip =>
        _localization.Tr(Info is null ? "quarantine.tooltip_empty_nothing" : "quarantine.tooltip_empty");

    /// <summary>
    /// Measures the quarantine of a destination off the UI thread. A destination changed again before the end of the
    /// measure wins: the older measure is dropped.
    /// </summary>
    public async Task LoadAsync(string? destination)
    {
        var version = ++_loadVersion;
        var trimmed = destination?.Trim() ?? string.Empty;
        _destination = trimmed;
        HasDestination = trimmed.Length > 0;
        var info = trimmed.Length == 0 ? null : await Task.Run(() => DestinationGuard.GetQuarantine(trimmed));
        if (version == _loadVersion)
        {
            Info = info;
        }
    }

    /// <summary>Measures the quarantine of the current destination again, after a run that may have purged or undone.</summary>
    public Task RefreshAsync() => LoadAsync(_destination);

    public void RefreshTranslations()
    {
        OnPropertyChanged(nameof(SummaryText));
        OnPropertyChanged(nameof(EmptyTooltip));
    }

    private bool CanEmpty() => Info is not null && !IsRunning && !IsEmptying;

    /// <summary>
    /// Deletes the quarantine for good after a confirmation that says the undo of the sorts that purged it can no longer
    /// put its content back. The sorted files are never touched.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanEmpty))]
    private async Task EmptyAsync()
    {
        if (Info is not { } info)
        {
            return;
        }

        var title = _localization.Tr("quarantine.title");
        var confirmed = await _dialogService.ShowConfirmationAsync(
            title,
            _localization.Tr(
                "quarantine.confirm_html",
                ("path", info.Path),
                ("purges", info.PurgeCount),
                ("files", info.FileCount),
                ("size", SizeFormatting.Format(_localization, info.TotalBytes))));
        if (!confirmed)
        {
            return;
        }

        var destination = _destination;
        IsEmptying = true;
        try
        {
            var result = await Task.Run(() => DestinationGuard.EmptyQuarantine(destination));
            foreach (var error in result.Errors)
            {
                _log.Append(LogLevel.Error, LocalizedMessage.Create(
                    "log.labeled", ("label", LocalizedMessage.Create("quarantine.log_not_deleted")), ("detail", error)));
            }

            _log.Append(
                result.Errors.Count == 0 ? LogLevel.Ok : LogLevel.Warn,
                LocalizedMessage.Create("quarantine.log_emptied", ("files", result.DeletedFiles), ("path", info.Path)));

            var message = _localization.Tr(
                "quarantine.done",
                ("files", result.DeletedFiles),
                ("size", SizeFormatting.Format(_localization, result.DeletedBytes)));
            if (result.Errors.Count == 0)
            {
                await _dialogService.ShowInfoAsync(title, message);
            }
            else
            {
                await _dialogService.ShowWarningAsync(
                    title, message + Environment.NewLine + _localization.Tr("quarantine.not_all_deleted", ("count", result.Errors.Count)));
            }
        }
        finally
        {
            IsEmptying = false;
            await LoadAsync(destination);
        }
    }
}
