using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RomSieve.Core.Abstractions;
using RomSieve.Core.Models;
using RomSieve.Core.Services;

namespace RomSieve.App.ViewModels;

/// <summary>
/// Pages of the main window, in the order of the pipeline: what to sort (collection), what to keep (filters, then
/// languages, regions and 1G1R), where to write it (output), then the results of the last run.
/// </summary>
public enum WorkflowStep
{
    Collection,
    Filters,
    Languages,
    Output,
    Results
}

/// <summary>Tone of the line shown under a step of the navigation; the view turns it into a color of the palette.</summary>
public enum StepIndicatorTone
{
    None,
    Info,
    Warning
}

/// <summary>Entry of the navigation: a step, or the group of the selection steps, with its state line.</summary>
public sealed partial class NavigationStepViewModel : ViewModelBase
{
    private readonly ILocalizationService _localization;
    private readonly string _labelKey;
    private readonly string _tooltipKey;

    public NavigationStepViewModel(
        ILocalizationService localization,
        WorkflowStep step,
        string number,
        string labelKey,
        string tooltipKey,
        bool isSubStep,
        Action select,
        bool isGroup = false)
    {
        _localization = localization;
        Step = step;
        Number = number;
        _labelKey = labelKey;
        _tooltipKey = tooltipKey;
        IsSubStep = isSubStep;
        IsGroup = isGroup;
        SelectCommand = new RelayCommand(select);
        _label = localization.Tr(labelKey);
        _tooltip = localization.Tr(tooltipKey);
    }

    /// <summary>The step this entry shows; for the selection group, its first step.</summary>
    public WorkflowStep Step { get; }

    /// <summary>Number of the step in the pipeline; empty for a sub-step.</summary>
    public string Number { get; }

    public bool HasNumber => Number.Length > 0;

    /// <summary>Indented entry under the selection group.</summary>
    public bool IsSubStep { get; }

    /// <summary>The selection group, which always opens on its first step (the filters).</summary>
    public bool IsGroup { get; }

    public IRelayCommand SelectCommand { get; }

    [ObservableProperty]
    private string _label;

    [ObservableProperty]
    private string _tooltip;

    /// <summary>Whether the page of this entry is shown (for the group: one of its steps).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSelectedStep))]
    [NotifyPropertyChangedFor(nameof(IsSelectedGroup))]
    private bool _isCurrent;

    /// <summary>The page of this step is shown: the view marks the entry.</summary>
    public bool IsSelectedStep => IsCurrent && !IsGroup;

    /// <summary>One of the steps of this group is shown: the view only emphasizes its label.</summary>
    public bool IsSelectedGroup => IsCurrent && IsGroup;

    /// <summary>State of the step under its label (missing source, number of results…); empty when there is none.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasIndicator))]
    private string _indicatorText = string.Empty;

    [ObservableProperty]
    private StepIndicatorTone _indicatorTone;

    public bool HasIndicator => !string.IsNullOrEmpty(IndicatorText);

    /// <summary>What the state line means, when its few words need it; <c>null</c> otherwise (the entry's tooltip shows).</summary>
    [ObservableProperty]
    private string? _indicatorTooltip;

    internal void SetIndicator(StepIndicatorTone tone, string text, string? tooltip = null)
    {
        IndicatorTone = tone;
        IndicatorText = text;
        IndicatorTooltip = tooltip;
    }

    internal void RefreshTranslations()
    {
        Label = _localization.Tr(_labelKey);
        Tooltip = _localization.Tr(_tooltipKey);
    }
}

/// <summary>
/// Navigation by steps of the main window: the page shown, the last settings page (to which the results lead back),
/// and the state line of each step. Runs lead to the results through <see cref="ShowResults"/>, called by the
/// <see cref="MainViewModel"/>. The application always opens on the collection: the page shown is not saved.
/// </summary>
public sealed partial class NavigationViewModel : ViewModelBase
{
    private readonly ILocalizationService _localization;
    private readonly DirectoriesViewModel _directories;
    private readonly ExecutionViewModel _execution;
    private readonly Func<int> _resultCount;

    public NavigationViewModel(
        ILocalizationService localization,
        DirectoriesViewModel directories,
        ExecutionViewModel execution,
        Func<int> resultCount)
    {
        _localization = localization ?? throw new ArgumentNullException(nameof(localization));
        _directories = directories ?? throw new ArgumentNullException(nameof(directories));
        _execution = execution ?? throw new ArgumentNullException(nameof(execution));
        _resultCount = resultCount ?? throw new ArgumentNullException(nameof(resultCount));

        CollectionEntry = Entry(WorkflowStep.Collection, "1", "nav.collection", "nav.tooltip_collection");
        SelectionEntry = new NavigationStepViewModel(
            localization, WorkflowStep.Filters, "2", "nav.selection", "nav.tooltip_selection", isSubStep: false,
            () => CurrentStep = WorkflowStep.Filters, isGroup: true);
        FiltersEntry = Entry(WorkflowStep.Filters, string.Empty, "nav.filters", "nav.tooltip_filters", isSubStep: true);
        LanguagesEntry = Entry(WorkflowStep.Languages, string.Empty, "nav.languages", "nav.tooltip_languages", isSubStep: true);
        OutputEntry = Entry(WorkflowStep.Output, "3", "nav.output", "nav.tooltip_output");
        ResultsEntry = Entry(WorkflowStep.Results, "4", "nav.results", "nav.tooltip_results");
        Entries = [CollectionEntry, SelectionEntry, FiltersEntry, LanguagesEntry, OutputEntry, ResultsEntry];

        _directories.PropertyChanged += OnDirectoriesPropertyChanged;
        _execution.PropertyChanged += OnExecutionPropertyChanged;

        UpdateCurrentEntries();
        RefreshIndicators();
    }

    public NavigationStepViewModel CollectionEntry { get; }

    /// <summary>Group of <see cref="FiltersEntry"/> and <see cref="LanguagesEntry"/>: always shows the filters.</summary>
    public NavigationStepViewModel SelectionEntry { get; }
    public NavigationStepViewModel FiltersEntry { get; }
    public NavigationStepViewModel LanguagesEntry { get; }
    public NavigationStepViewModel OutputEntry { get; }
    public NavigationStepViewModel ResultsEntry { get; }

    /// <summary>Every entry of the navigation, from top to bottom.</summary>
    public IReadOnlyList<NavigationStepViewModel> Entries { get; }

    /// <summary>Page shown in the main window.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsCollectionShown))]
    [NotifyPropertyChangedFor(nameof(IsFiltersShown))]
    [NotifyPropertyChangedFor(nameof(IsLanguagesShown))]
    [NotifyPropertyChangedFor(nameof(IsOutputShown))]
    [NotifyPropertyChangedFor(nameof(IsResultsShown))]
    [NotifyPropertyChangedFor(nameof(IsSettingsShown))]
    private WorkflowStep _currentStep = WorkflowStep.Collection;

    /// <summary>Last settings page shown, to which the results lead back; never <see cref="WorkflowStep.Results"/>.</summary>
    [ObservableProperty]
    private WorkflowStep _lastSettingsStep = WorkflowStep.Collection;

    public bool IsCollectionShown => CurrentStep == WorkflowStep.Collection;
    public bool IsFiltersShown => CurrentStep == WorkflowStep.Filters;
    public bool IsLanguagesShown => CurrentStep == WorkflowStep.Languages;
    public bool IsOutputShown => CurrentStep == WorkflowStep.Output;
    public bool IsResultsShown => CurrentStep == WorkflowStep.Results;
    public bool IsSettingsShown => CurrentStep != WorkflowStep.Results;

    partial void OnCurrentStepChanged(WorkflowStep value)
    {
        if (value != WorkflowStep.Results)
        {
            LastSettingsStep = value;
        }

        UpdateCurrentEntries();

        // A folder can appear or disappear without its path changing (a network share mounted on demand)
        RefreshIndicators();
    }

    /// <summary>Shows the results: a run starts, or an undo writes its report in the log.</summary>
    public void ShowResults() => CurrentStep = WorkflowStep.Results;

    /// <summary>Back to the last settings page shown.</summary>
    [RelayCommand]
    public void ReturnToSettings() => CurrentStep = LastSettingsStep;

    // =========================================================================
    // State line of the steps
    // =========================================================================

    /// <summary>Updates the state lines; the <see cref="MainViewModel"/> calls it when the results change.</summary>
    public void RefreshIndicators()
    {
        RefreshCollectionIndicator();
        RefreshOutputIndicator();
        RefreshResultsIndicator();
    }

    private void OnDirectoriesPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(DirectoriesViewModel.SourceDir):
            case nameof(DirectoriesViewModel.IsRecursive):
                RefreshCollectionIndicator();
                RefreshOutputIndicator();
                break;
            case nameof(DirectoriesViewModel.DatFilePath):
                RefreshCollectionIndicator();
                break;
            case nameof(DirectoriesViewModel.DestinationDir):
            case nameof(DirectoriesViewModel.ChdDir):
            case nameof(DirectoriesViewModel.IsMameConvention):
                RefreshOutputIndicator();
                break;
        }
    }

    private void OnExecutionPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(ExecutionViewModel.IsRunning)
            or nameof(ExecutionViewModel.IsSimulationCurrent)
            or nameof(ExecutionViewModel.IsDryRunOperation))
        {
            RefreshResultsIndicator();
        }
    }

    private void RefreshCollectionIndicator()
    {
        var source = _directories.SourceDir?.Trim() ?? string.Empty;
        var dat = _directories.DatFilePath?.Trim() ?? string.Empty;
        string? key = null;
        if (source.Length == 0 && dat.Length > 0 && (File.Exists(dat) || Directory.Exists(dat)))
        {
            // A DAT alone is enough for the filtered DAT of the catalog: nothing is missing for it, only for a run
            CollectionEntry.SetIndicator(
                StepIndicatorTone.Info,
                _localization.Tr("nav.state_dat_only"),
                _localization.Tr("nav.tooltip_dat_only"));
            return;
        }

        if (source.Length == 0)
        {
            key = dat.Length > 0 ? "nav.state_dat_not_found" : "nav.state_source_missing";
        }
        else if (!Directory.Exists(source))
        {
            key = "nav.state_source_not_found";
        }
        else if (dat.Length > 0 && !File.Exists(dat) && !Directory.Exists(dat))
        {
            key = "nav.state_dat_not_found";
        }

        CollectionEntry.SetIndicator(
            key is null ? StepIndicatorTone.None : StepIndicatorTone.Warning,
            key is null ? string.Empty : _localization.Tr(key));
    }

    private void RefreshOutputIndicator()
    {
        var source = _directories.SourceDir?.Trim() ?? string.Empty;
        var destination = _directories.DestinationDir?.Trim() ?? string.Empty;
        string? key = null;
        if (destination.Length == 0)
        {
            key = "nav.state_destination_missing";
        }
        else if (source.Length > 0 && SortConfigValidator.GetPathOverlap(source, destination, _directories.IsRecursive) != PathOverlap.None)
        {
            key = "nav.state_destination_overlap";
        }
        else if (_directories.IsMameConvention && SortConfigValidator.GetChdDirErrorKey(_directories.ChdDir, destination) is not null)
        {
            key = "nav.state_destination_overlap_chd";
        }

        OutputEntry.SetIndicator(
            key is null ? StepIndicatorTone.None : StepIndicatorTone.Warning,
            key is null ? string.Empty : _localization.Tr(key));
    }

    private void RefreshResultsIndicator()
    {
        if (_execution.IsRunning)
        {
            ResultsEntry.SetIndicator(StepIndicatorTone.Info, _localization.Tr("nav.state_running"));
            return;
        }

        var count = _resultCount();
        if (count == 0)
        {
            ResultsEntry.SetIndicator(StepIndicatorTone.None, string.Empty);
            return;
        }

        // Only a simulation describes the next sort: the results of a real sort are never out of date
        var isOutdated = _execution.IsDryRunOperation && !_execution.IsSimulationCurrent;
        ResultsEntry.SetIndicator(
            isOutdated ? StepIndicatorTone.Warning : StepIndicatorTone.Info,
            _localization.Tr(isOutdated ? "nav.state_results_outdated" : "nav.state_results", ("count", count)));
    }

    private void UpdateCurrentEntries()
    {
        foreach (var entry in Entries)
        {
            entry.IsCurrent = ReferenceEquals(entry, SelectionEntry)
                ? CurrentStep is WorkflowStep.Filters or WorkflowStep.Languages
                : entry.Step == CurrentStep;
        }
    }

    public void RefreshTranslations()
    {
        foreach (var entry in Entries)
        {
            entry.RefreshTranslations();
        }

        RefreshIndicators();
    }

    private NavigationStepViewModel Entry(WorkflowStep step, string number, string labelKey, string tooltipKey, bool isSubStep = false) =>
        new(_localization, step, number, labelKey, tooltipKey, isSubStep, () => CurrentStep = step);
}
