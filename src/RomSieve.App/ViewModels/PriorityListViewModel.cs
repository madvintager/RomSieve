using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RomSieve.Core.Abstractions;

namespace RomSieve.App.ViewModels;

/// <summary>
/// Single item (language or country/region): a chip of the ordered priority list, or a choice of the list of
/// available items. Its commands act on the list that owns it.
/// </summary>
public sealed partial class PriorityItemViewModel : ViewModelBase
{
    public PriorityItemViewModel(string code, string label, int rankIndex = 0, PriorityListViewModel? owner = null)
    {
        Code = code;
        _label = label;
        _rankIndex = rankIndex;
        Owner = owner;
    }

    public string Code { get; }

    /// <summary>List the item belongs to, whose commands it calls.</summary>
    public PriorityListViewModel? Owner { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RankedDisplayText))]
    private string _label;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RankedDisplayText), nameof(CanMoveUp))]
    private int _rankIndex;

    [ObservableProperty]
    private bool _isSelected;

    public string RankedDisplayText => RankIndex > 0
        ? $"{RankIndex}. {Code} - {Label}"
        : $"{Code} - {Label}";

    /// <summary>The chip is not the first one of the priority list.</summary>
    public bool CanMoveUp => RankIndex > 1;

    /// <summary>The chip is not the last one of the priority list.</summary>
    public bool CanMoveDown => Owner is not null && RankIndex > 0 && RankIndex < Owner.PriorityItems.Count;

    public override string ToString() => RankedDisplayText;

    /// <summary>Adds the available item at the end of the priority list.</summary>
    [RelayCommand]
    private void Add() => Owner?.AddSelected(this);

    /// <summary>Removes the chip from the priority list: the item becomes available again.</summary>
    [RelayCommand]
    private void Remove() => Owner?.RemoveSelected(this);

    [RelayCommand]
    private void MoveToTop() => Owner?.MoveToTop(this);

    [RelayCommand]
    private void MoveUp() => Owner?.MoveUp(this);

    [RelayCommand]
    private void MoveDown() => Owner?.MoveDown(this);

    /// <summary>The place of the chip may change without a change of its rank (when the last one is removed).</summary>
    internal void RefreshPosition()
    {
        OnPropertyChanged(nameof(CanMoveUp));
        OnPropertyChanged(nameof(CanMoveDown));
    }
}

/// <summary>
/// Ordered priority list shown as chips, the available items being added through a search, with reordering (to the
/// top, up, down, drag-and-drop) and reset.
/// </summary>
public partial class PriorityListViewModel : ViewModelBase
{
    private readonly Func<IReadOnlyList<(string Code, string Label)>> _catalogProvider;
    private readonly IReadOnlyList<string> _defaultPriorityCodes;
    private Dictionary<string, string> _codeToLabel = new(StringComparer.OrdinalIgnoreCase);
    private List<string> _catalogOrder = [];
    private bool _suppressNotifications;

    public PriorityListViewModel(
        Func<IReadOnlyList<(string Code, string Label)>> catalogProvider,
        IReadOnlyList<string>? initialPriorityCodes = null)
    {
        _catalogProvider = catalogProvider;
        _defaultPriorityCodes = initialPriorityCodes ?? [];
        ReloadCatalogAndReset(_defaultPriorityCodes, notify: false);
    }

    /// <summary>
    /// Event triggered when the ordered list of priority codes changes.
    /// </summary>
    public event EventHandler<IReadOnlyList<string>>? PriorityChanged;

    /// <summary>Set of available items (not selected in the right-hand list).</summary>
    public ObservableCollection<PriorityItemViewModel> AvailableItems { get; } = [];

    /// <summary>Filtered subset of available items matching <see cref="SearchFilterText"/>.</summary>
    public ObservableCollection<PriorityItemViewModel> FilteredAvailableItems { get; } = [];

    /// <summary>Ordered list of selected priority items (right-hand column).</summary>
    public ObservableCollection<PriorityItemViewModel> PriorityItems { get; } = [];

    [ObservableProperty]
    private string _searchFilterText = string.Empty;

    [ObservableProperty]
    private PriorityItemViewModel? _selectedAvailableItem;

    [ObservableProperty]
    private PriorityItemViewModel? _selectedPriorityItem;

    [ObservableProperty]
    private bool _isEnabled = true;

    /// <summary>The priority list has at least one chip; otherwise the view says how to add one.</summary>
    [ObservableProperty]
    private bool _hasPriorityItems;

    partial void OnSearchFilterTextChanged(string value)
    {
        RefreshFilteredAvailableItems();
    }

    /// <summary>
    /// Returns the ordered list of currently selected codes.
    /// </summary>
    public IReadOnlyList<string> GetPriorityCodes() =>
        PriorityItems.Select(i => NormalizeOutputCode(i.Code)).ToList();

    /// <summary>
    /// Sets the ordered list of priority codes (e.g. from a preset or CSV text field).
    /// </summary>
    public void SetPriorityCodes(IEnumerable<string> codes, bool notify = true)
    {
        var normalizedCodes = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var raw in codes)
        {
            var cleaned = NormalizeCode(raw);
            if (!string.IsNullOrEmpty(cleaned) && seen.Add(cleaned))
            {
                normalizedCodes.Add(cleaned);
            }
        }

        var currentCodes = GetPriorityCodes();
        if (currentCodes.SequenceEqual(normalizedCodes, StringComparer.OrdinalIgnoreCase))
        {
            return;
        }

        RebuildLists(normalizedCodes, notify);
    }

    /// <summary>
    /// Reloads translated labels when the UI language changes
    /// while preserving selected code order and the search filter.
    /// </summary>
    public void RefreshTranslations()
    {
        var currentCodes = GetPriorityCodes();
        ReloadCatalog();
        RebuildLists(currentCodes, notify: false);
    }

    /// <summary>
    /// Adds selected available items (or the item passed as parameter) to the priority list.
    /// </summary>
    [RelayCommand]
    public void AddSelected(PriorityItemViewModel? explicitItem = null)
    {
        if (!IsEnabled)
        {
            return;
        }

        var toMove = new List<PriorityItemViewModel>();
        if (explicitItem is not null && AvailableItems.Contains(explicitItem))
        {
            toMove.Add(explicitItem);
        }
        else
        {
            toMove.AddRange(AvailableItems.Where(i => i.IsSelected));
            if (toMove.Count == 0 && SelectedAvailableItem is not null && AvailableItems.Contains(SelectedAvailableItem))
            {
                toMove.Add(SelectedAvailableItem);
            }
        }

        if (toMove.Count == 0)
        {
            return;
        }

        foreach (var item in toMove)
        {
            item.IsSelected = false;
            AvailableItems.Remove(item);
            PriorityItems.Add(item);
        }

        SelectedAvailableItem = null;
        UpdateRanksAndNotify();
        RefreshFilteredAvailableItems();
    }

    /// <summary>
    /// Removes selected priority items (or the item passed as parameter) and returns them to available items.
    /// </summary>
    [RelayCommand]
    public void RemoveSelected(PriorityItemViewModel? explicitItem = null)
    {
        if (!IsEnabled)
        {
            return;
        }

        var toRemove = new List<PriorityItemViewModel>();
        if (explicitItem is not null && PriorityItems.Contains(explicitItem))
        {
            toRemove.Add(explicitItem);
        }
        else
        {
            toRemove.AddRange(PriorityItems.Where(i => i.IsSelected));
            if (toRemove.Count == 0 && SelectedPriorityItem is not null && PriorityItems.Contains(SelectedPriorityItem))
            {
                toRemove.Add(SelectedPriorityItem);
            }
        }

        if (toRemove.Count == 0)
        {
            return;
        }

        foreach (var item in toRemove)
        {
            item.IsSelected = false;
            item.RankIndex = 0;
            PriorityItems.Remove(item);
            if (_codeToLabel.ContainsKey(item.Code))
            {
                AvailableItems.Add(item);
            }
        }

        SortAvailableByCatalogOrder();
        SelectedPriorityItem = null;
        UpdateRanksAndNotify();
        RefreshFilteredAvailableItems();
    }

    /// <summary>
    /// Moves the selected priority item (or the item passed as parameter) to the very top of the list (rank 1).
    /// </summary>
    [RelayCommand]
    public void MoveToTop(PriorityItemViewModel? explicitItem = null)
    {
        if (!IsEnabled)
        {
            return;
        }

        var target = GetTargetPriorityItem(explicitItem);
        if (target is null)
        {
            return;
        }

        var index = PriorityItems.IndexOf(target);
        if (index > 0)
        {
            PriorityItems.Move(index, 0);
            SelectedPriorityItem = target;
            UpdateRanksAndNotify();
        }
    }

    /// <summary>
    /// Moves the selected priority item (or the item passed as parameter) up by one position.
    /// </summary>
    [RelayCommand]
    public void MoveUp(PriorityItemViewModel? explicitItem = null)
    {
        if (!IsEnabled)
        {
            return;
        }

        var target = GetTargetPriorityItem(explicitItem);
        if (target is null)
        {
            return;
        }

        var index = PriorityItems.IndexOf(target);
        if (index > 0)
        {
            PriorityItems.Move(index, index - 1);
            SelectedPriorityItem = target;
            UpdateRanksAndNotify();
        }
    }

    /// <summary>
    /// Moves the selected priority item (or the item passed as parameter) down by one position.
    /// </summary>
    [RelayCommand]
    public void MoveDown(PriorityItemViewModel? explicitItem = null)
    {
        if (!IsEnabled)
        {
            return;
        }

        var target = GetTargetPriorityItem(explicitItem);
        if (target is null)
        {
            return;
        }

        var index = PriorityItems.IndexOf(target);
        if (index >= 0 && index < PriorityItems.Count - 1)
        {
            PriorityItems.Move(index, index + 1);
            SelectedPriorityItem = target;
            UpdateRanksAndNotify();
        }
    }

    /// <summary>
    /// Adds the first available item matching the search (Enter in the search field), then clears the search.
    /// </summary>
    [RelayCommand]
    public void AddFirstMatch()
    {
        if (!IsEnabled || FilteredAvailableItems.Count == 0 || string.IsNullOrWhiteSpace(SearchFilterText))
        {
            return;
        }

        AddSelected(FilteredAvailableItems[0]);
        SearchFilterText = string.Empty;
    }

    /// <summary>
    /// Resets the priority list to default values.
    /// </summary>
    [RelayCommand]
    public void ResetDefaults()
    {
        if (!IsEnabled)
        {
            return;
        }

        SearchFilterText = string.Empty;
        RebuildLists(_defaultPriorityCodes, notify: true);
    }

    /// <summary>
    /// Moves an item in the priority list via drag-and-drop.
    /// </summary>
    public void MovePriorityItem(int fromIndex, int toIndex)
    {
        if (!IsEnabled || fromIndex < 0 || fromIndex >= PriorityItems.Count)
        {
            return;
        }

        var clampedTarget = Math.Clamp(toIndex, 0, PriorityItems.Count - 1);
        if (fromIndex == clampedTarget)
        {
            return;
        }

        PriorityItems.Move(fromIndex, clampedTarget);
        UpdateRanksAndNotify();
    }

    protected virtual string NormalizeCode(string rawCode) =>
        rawCode.Trim();

    protected virtual string NormalizeOutputCode(string code) =>
        code;

    private PriorityItemViewModel? GetTargetPriorityItem(PriorityItemViewModel? explicitItem)
    {
        if (explicitItem is not null)
        {
            return PriorityItems.Contains(explicitItem) ? explicitItem : null;
        }

        if (SelectedPriorityItem is not null && PriorityItems.Contains(SelectedPriorityItem))
        {
            return SelectedPriorityItem;
        }

        return PriorityItems.FirstOrDefault(i => i.IsSelected);
    }

    private void ReloadCatalog()
    {
        var catalog = _catalogProvider();
        _codeToLabel = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        _catalogOrder = new List<string>(catalog.Count);

        foreach (var (rawCode, label) in catalog)
        {
            var normCode = NormalizeOutputCode(rawCode);
            _codeToLabel[normCode] = label;
            _catalogOrder.Add(normCode);
        }
    }

    private void ReloadCatalogAndReset(IReadOnlyList<string> initialCodes, bool notify)
    {
        ReloadCatalog();
        RebuildLists(initialCodes, notify);
    }

    private void RebuildLists(IReadOnlyList<string> priorityCodes, bool notify)
    {
        _suppressNotifications = true;
        try
        {
            PriorityItems.Clear();
            AvailableItems.Clear();

            var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var rank = 1;

            foreach (var rawCode in priorityCodes)
            {
                var code = NormalizeCode(rawCode);
                if (string.IsNullOrEmpty(code) || !used.Add(code))
                {
                    continue;
                }

                var canonicalCode = _catalogOrder.FirstOrDefault(c => string.Equals(c, code, StringComparison.OrdinalIgnoreCase))
                                    ?? NormalizeOutputCode(code);
                var label = _codeToLabel.TryGetValue(canonicalCode, out var foundLabel)
                    ? foundLabel
                    : $"{canonicalCode.ToUpperInvariant()} ({canonicalCode})";

                PriorityItems.Add(new PriorityItemViewModel(canonicalCode, label, rank++, this));
            }

            foreach (var code in _catalogOrder)
            {
                if (!used.Contains(code) && _codeToLabel.TryGetValue(code, out var label))
                {
                    AvailableItems.Add(new PriorityItemViewModel(code, label, 0, this));
                }
            }

            RefreshFilteredAvailableItems();
            RefreshPositions();
        }
        finally
        {
            _suppressNotifications = false;
        }

        if (notify)
        {
            PriorityChanged?.Invoke(this, GetPriorityCodes());
        }
    }

    private void SortAvailableByCatalogOrder()
    {
        var orderMap = _catalogOrder
            .Select((code, idx) => (code, idx))
            .ToDictionary(x => x.code, x => x.idx, StringComparer.OrdinalIgnoreCase);

        var sorted = AvailableItems
            .OrderBy(item => orderMap.TryGetValue(item.Code, out var idx) ? idx : int.MaxValue)
            .ThenBy(item => item.Code, StringComparer.OrdinalIgnoreCase)
            .ToList();

        AvailableItems.Clear();
        foreach (var item in sorted)
        {
            AvailableItems.Add(item);
        }
    }

    private void RefreshFilteredAvailableItems()
    {
        var query = SearchFilterText.Trim();
        FilteredAvailableItems.Clear();

        foreach (var item in AvailableItems)
        {
            if (string.IsNullOrEmpty(query)
                || item.Label.Contains(query, StringComparison.OrdinalIgnoreCase)
                || item.Code.Contains(query, StringComparison.OrdinalIgnoreCase))
            {
                FilteredAvailableItems.Add(item);
            }
        }
    }

    private void RefreshPositions()
    {
        foreach (var item in PriorityItems)
        {
            item.RefreshPosition();
        }

        HasPriorityItems = PriorityItems.Count > 0;
    }

    private void UpdateRanksAndNotify()
    {
        for (var i = 0; i < PriorityItems.Count; i++)
        {
            PriorityItems[i].RankIndex = i + 1;
        }

        RefreshPositions();

        if (!_suppressNotifications)
        {
            PriorityChanged?.Invoke(this, GetPriorityCodes());
        }
    }
}

/// <summary>
/// Specialized ViewModel for selecting and ordering priority languages
/// (lowercase codes: <c>fr</c>, <c>en</c>, <c>m</c>, <c>ja</c>...).
/// By default at initial opening, the chosen list is empty and all languages are available.
/// </summary>
public sealed class LanguagePriorityViewModel : PriorityListViewModel
{
    public LanguagePriorityViewModel(ILocalizationService localization, IReadOnlyList<string>? initialCodes = null)
        : base(
            () => localization.GetDefaultLanguages(),
            initialCodes ?? [])
    {
    }

    public LanguagePriorityViewModel(
        IReadOnlyList<(string Code, string Label)> customCatalog,
        IReadOnlyList<string>? initialCodes = null)
        : base(
            () => customCatalog,
            initialCodes ?? [])
    {
    }

    protected override string NormalizeCode(string rawCode) =>
        rawCode.Trim().ToLowerInvariant();

    protected override string NormalizeOutputCode(string code) =>
        code.Trim().ToLowerInvariant();
}

/// <summary>
/// Specialized ViewModel for selecting and ordering priority countries/regions
/// (uppercase codes: <c>FR</c>, <c>EU</c>, <c>US</c>, <c>WO</c>, <c>JP</c>..., <c>UK</c> normalized to <c>GB</c>).
/// By default at initial opening, the chosen list is empty and all countries are available.
/// </summary>
public sealed class CountryPriorityViewModel : PriorityListViewModel
{
    public CountryPriorityViewModel(ILocalizationService localization, IReadOnlyList<string>? initialCodes = null)
        : base(
            () => localization.GetDefaultCountries(),
            initialCodes ?? [])
    {
    }

    public CountryPriorityViewModel(
        IReadOnlyList<(string Code, string Label)> customCatalog,
        IReadOnlyList<string>? initialCodes = null)
        : base(
            () => customCatalog,
            initialCodes ?? [])
    {
    }

    protected override string NormalizeCode(string rawCode)
    {
        var upper = rawCode.Trim().ToUpperInvariant();
        return upper switch
        {
            "UK" => "GB",
            "ASIA" => "AS",
            _ => upper
        };
    }

    protected override string NormalizeOutputCode(string code) =>
        NormalizeCode(code);
}
