using System;
using System.Collections.Specialized;
using System.Linq;
using System.Threading;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using RomSieve.App.Abstractions;
using RomSieve.App.Services;
using RomSieve.App.ViewModels;
using RomSieve.Core.Abstractions;

namespace RomSieve.App.Views;

/// <summary>
/// Minimal code-behind for the Avalonia UI main window (<see cref="MainWindow"/>).
/// All presentation, state, and orchestration logic is handled by <see cref="MainViewModel"/>.
/// </summary>
public partial class MainWindow : Window
{
    /// <summary>Position of the column of related files in the preview table: after the disk column.</summary>
    internal const int RelatedColumnDisplayIndex = 4;

    private readonly MainViewModel _initialViewModel;
    private ListBox? _logViewerList;
    private MainViewModel? _subscribedViewModel;
    private MainViewModel? _themedViewModel;
    private int _logScrollPending;
    private DispatcherTimer? _previewTooltipResumeTimer;
    private PriorityItemViewModel? _pressedChipItem;
    private Point _pressedChipPoint;

    private const string PriorityChipClass = "priority-chip";
    private PriorityItemViewModel? _draggedChipItem;

    /// <summary>The drag carries the code of the chip; the chip itself stays in <see cref="_draggedChipItem"/>.</summary>
    private static readonly DataFormat<string> PriorityChipDataFormat = DataFormat.CreateStringApplicationFormat("romsieve-priority-item");
    private const double PriorityChipDragThreshold = 4;

    /// <summary>How long the preview table keeps its tooltips off after the last mouse wheel step.</summary>
    internal static readonly TimeSpan PreviewTooltipResumeDelay = TimeSpan.FromMilliseconds(500);

    public MainWindow()
        : this(viewModel: null, configFilePath: null)
    {
    }

    /// <summary>
    /// Window of the configuration <paramref name="configFilePath"/>; <paramref name="localization"/> gives the language
    /// shown until the configuration names one (English by default).
    /// </summary>
    public MainWindow(string? configFilePath, IDialogService? dialogService = null, ILocalizationService? localization = null)
        : this(viewModel: null, configFilePath: configFilePath, dialogService: dialogService, localization: localization)
    {
    }

    public MainWindow(
        MainViewModel? viewModel, string? configFilePath = null, IDialogService? dialogService = null, ILocalizationService? localization = null)
    {
        InitializeComponent();

        _initialViewModel = viewModel ?? AppServiceCollectionExtensions.CreateMainViewModel(
            dialogService ?? new AvaloniaDialogService(() => this),
            configFilePath,
            localization: localization);

        WireDoubleClickAndCloseEvents();

        DataContext = _initialViewModel;
    }

    public MainViewModel ViewModel => (DataContext as MainViewModel) ?? _initialViewModel;

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        var vm = DataContext as MainViewModel;
        SubscribeToLogEntries(vm);
        SubscribeToTheme(vm);
        if (this.FindControl<DataGrid>("TablePreview")?.ContextMenu is { } ctx)
        {
            ctx.DataContext = vm;
        }
    }

    /// <summary>Applies the color theme chosen in the ViewModel to the application, now and whenever it changes.</summary>
    private void SubscribeToTheme(MainViewModel? vm)
    {
        if (_themedViewModel is not null)
        {
            _themedViewModel.PropertyChanged -= OnViewModelPropertyChanged;
        }

        _themedViewModel = vm;
        if (vm is not null)
        {
            vm.PropertyChanged += OnViewModelPropertyChanged;
            ApplyTheme(vm.UiTheme);
        }
    }

    private void OnViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.UiTheme) && sender is MainViewModel vm)
        {
            ApplyTheme(vm.UiTheme);
        }
        else if (e.PropertyName is nameof(PreviewTableViewModel.CurrentSortColumn) or nameof(PreviewTableViewModel.IsSortAscending))
        {
            UpdatePreviewSortIndicators();
        }
    }

    /// <summary>
    /// Switches the theme variant of the application: the palette resources (<c>DynamicResource</c>) and the colors
    /// computed by <see cref="Converters.ThemeResourceConverter"/> from the variant of each control follow at once.
    /// </summary>
    internal static void ApplyTheme(Core.Models.UiTheme theme)
    {
        if (Application.Current is { } app)
        {
            app.RequestedThemeVariant = theme switch
            {
                Core.Models.UiTheme.Dark => ThemeVariant.Dark,
                Core.Models.UiTheme.System => ThemeVariant.Default,
                _ => ThemeVariant.Light
            };
        }
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }

    private void WireDoubleClickAndCloseEvents()
    {
        _logViewerList = this.FindControl<ListBox>("LogViewerList");

        // Chips of the language and country lists: drag-and-drop and keyboard reordering, whatever chip raised the event
        AddHandler(PointerPressedEvent, OnPriorityChipPointerPressed);
        AddHandler(PointerMovedEvent, OnPriorityChipPointerMoved);
        AddHandler(PointerReleasedEvent, (_, _) => _pressedChipItem = null);
        AddHandler(DragDrop.DragOverEvent, OnPriorityChipDragOver);
        AddHandler(DragDrop.DropEvent, OnPriorityChipDrop);
        // Tunnel: the scroll viewer and the tab control would otherwise take the arrow keys first
        AddHandler(KeyDownEvent, OnPriorityChipKeyDown, RoutingStrategies.Tunnel);

        if (this.FindControl<DataGrid>("TablePreview") is { } tablePreview)
        {
            // The column of related files is declared last, so that the indexes of the other columns did not change:
            // it is shown after the disk column
            tablePreview.Columns[PreviewTableViewModel.RelatedColumnIndex].DisplayIndex = RelatedColumnDisplayIndex;
            tablePreview.ContextRequested += OnTablePreviewContextRequested;
            tablePreview.Sorting += OnTablePreviewSorting;
            tablePreview.TemplateApplied += (_, _) => Dispatcher.UIThread.Post(UpdatePreviewSortIndicators, DispatcherPriority.Loaded);
            // Tunnel: the DataGrid would otherwise take the arrow keys to move between cells
            tablePreview.AddHandler(KeyDownEvent, OnTablePreviewKeyDown, RoutingStrategies.Tunnel);
            // The rows selected with Ctrl or Shift are checked at once for a partial sort (menu, space bar)
            tablePreview.SelectionChanged += (_, _) =>
                ViewModel.PartialSort.SetGridSelection(tablePreview.SelectedItems.OfType<PreviewRowViewModel>());
            // Tunnel and handledEventsToo: the DataGrid marks the wheel event as handled when it scrolls
            tablePreview.AddHandler(
                PointerWheelChangedEvent,
                OnTablePreviewPointerWheelChanged,
                RoutingStrategies.Tunnel,
                handledEventsToo: true);
            if (tablePreview.ContextMenu is { } ctx)
            {
                ctx.DataContext = ViewModel;
            }
        }

        if (this.FindControl<TextBox>("EditPattern") is { } editPattern)
        {
            // The caret placed by the user is where a variable of the menu is inserted
            editPattern.PropertyChanged += (_, e) =>
            {
                if (e.Property == TextBox.CaretIndexProperty && editPattern.IsFocused)
                {
                    ViewModel.Directories.PatternCaretIndex = editPattern.CaretIndex;
                }
            };

            if (this.FindControl<DropDownButton>("BtnInsertVariable")?.Flyout is { } variableMenu)
            {
                variableMenu.Closed += (_, _) => Dispatcher.UIThread.Post(() => FocusPatternAtCaret(editPattern));
            }
        }

        Closing += OnWindowClosing;

        // The source or the DAT may have been mounted or created while the window was in the background
        Activated += (_, _) => ViewModel.Navigation.RefreshIndicators();
    }

    /// <summary>Gives the focus back to the pattern field, the caret after the inserted variable.</summary>
    internal void FocusPatternAtCaret(TextBox editPattern)
    {
        var caret = ViewModel.Directories.PatternCaretIndex;
        if (caret < 0 || !editPattern.IsEffectivelyEnabled)
        {
            return;
        }

        editPattern.Focus();
        editPattern.CaretIndex = Math.Min(caret, editPattern.Text?.Length ?? 0);
        editPattern.ClearSelection();
    }

    private void OnTablePreviewContextRequested(object? sender, ContextRequestedEventArgs e)
    {
        if (sender is DataGrid dg && dg.ContextMenu is { } ctx)
        {
            ctx.DataContext = ViewModel;
        }

        if (e.Source is Visual visual)
        {
            var row = visual as DataGridRow ?? visual.FindAncestorOfType<DataGridRow>();
            if (row?.DataContext is PreviewRowViewModel previewRow)
            {
                ViewModel.PreviewTable.SelectedRow = previewRow;
                if (!ViewModel.CanOpenPreviewContextMenu)
                {
                    e.Handled = true;
                }
                return;
            }
        }

        // If the right click does not target any data row (e.g. column header or empty space)
        e.Handled = true;
    }

    /// <summary>
    /// Sorts the preview table by the ViewModel instead of the DataGrid: the grid would sort the rows alone, and mix
    /// the headers of the view grouped by game with the files. The ViewModel sorts the files, then groups them.
    /// </summary>
    private void OnTablePreviewSorting(object? sender, DataGridColumnEventArgs e)
    {
        e.Handled = true;
        if (sender is DataGrid grid)
        {
            ViewModel.PreviewTable.SortPreviewByColumn(grid.Columns.IndexOf(e.Column));
        }
    }

    /// <summary>
    /// Shows the sort of the ViewModel on the column headers of the preview table (the arrow of the sorted column),
    /// which the DataGrid only does for its own sort.
    /// </summary>
    internal void UpdatePreviewSortIndicators()
    {
        if (this.FindControl<DataGrid>("TablePreview") is not { } grid)
        {
            return;
        }

        var table = ViewModel.PreviewTable;
        foreach (var header in grid.GetVisualDescendants().OfType<DataGridColumnHeader>())
        {
            // A header shows the Header of its column; the filler header after the last column has none
            var column = header.Content is null ? null : grid.Columns.FirstOrDefault(c => Equals(c.Header, header.Content));
            var isSorted = column is not null && grid.Columns.IndexOf(column) == table.CurrentSortColumn;
            // Plain classes: the header resets its sort pseudo-classes from the grid's own sort on each pointer move
            header.Classes.Set("sort-ascending", isSorted && table.IsSortAscending);
            header.Classes.Set("sort-descending", isSorted && !table.IsSortAscending);
        }
    }

    /// <summary>
    /// Keyboard of a selected game header in the view grouped by game: left arrow folds it, right arrow unfolds it,
    /// Space switches between both. On selected files, Space checks or unchecks their games for a partial sort.
    /// </summary>
    private void OnTablePreviewKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.KeyModifiers != KeyModifiers.None)
        {
            return;
        }

        // Space on files checks or unchecks their games for a partial sort; on a header, it folds or unfolds it
        if (ViewModel.PreviewTable.SelectedGroupHeader is not { } header)
        {
            if (e.Key == Key.Space)
            {
                ViewModel.PartialSort.ToggleGridSelection();
                e.Handled = true;
            }

            return;
        }

        bool? expand = e.Key switch
        {
            Key.Left => false,
            Key.Right => true,
            Key.Space => !header.IsExpanded,
            _ => null
        };

        if (expand is { } isExpanded)
        {
            ViewModel.PreviewTable.SetGroupExpanded(header, isExpanded);
            e.Handled = true;
        }
    }

    private void OnTablePreviewPointerWheelChanged(object? sender, PointerWheelEventArgs e)
    {
        if (sender is DataGrid grid)
        {
            SuspendPreviewTooltips(grid);
        }
    }

    /// <summary>
    /// Turns the tooltips of the preview table off while the mouse wheel scrolls it, and back on
    /// <see cref="PreviewTooltipResumeDelay"/> after the last wheel step. Otherwise each row passing under the pointer
    /// opens its tooltip at once (a tooltip already shown makes the next one skip its delay): one popup window opened
    /// and closed per row makes the cursor flicker, slows scrolling down, and wheel steps landing on a popup are lost.
    /// </summary>
    internal void SuspendPreviewTooltips(DataGrid grid)
    {
        if (ToolTip.GetServiceEnabled(grid))
        {
            ToolTip.SetServiceEnabled(grid, false);
            foreach (var control in grid.GetVisualDescendants().OfType<Control>())
            {
                if (ToolTip.GetIsOpen(control))
                {
                    ToolTip.SetIsOpen(control, false);
                }
            }
        }

        if (_previewTooltipResumeTimer is null)
        {
            _previewTooltipResumeTimer = new DispatcherTimer { Interval = PreviewTooltipResumeDelay };
            _previewTooltipResumeTimer.Tick += (_, _) =>
            {
                _previewTooltipResumeTimer.Stop();
                ToolTip.SetServiceEnabled(grid, true);
            };
        }

        // Restarted on each wheel step: the tooltips come back only once scrolling has stopped
        _previewTooltipResumeTimer.Stop();
        _previewTooltipResumeTimer.Start();
    }

    private void SubscribeToLogEntries(MainViewModel? newVm)
    {
        if (_subscribedViewModel is not null)
        {
            _subscribedViewModel.Log.Entries.CollectionChanged -= OnLogEntriesCollectionChanged;
        }

        _subscribedViewModel = newVm;

        if (_subscribedViewModel is not null)
        {
            _subscribedViewModel.Log.Entries.CollectionChanged += OnLogEntriesCollectionChanged;
        }
    }

    private void OnLogEntriesCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action != NotifyCollectionChangedAction.Add || _logViewerList is null)
        {
            return;
        }

        if (Interlocked.Exchange(ref _logScrollPending, 1) == 0)
        {
            Dispatcher.UIThread.Post(() =>
            {
                Volatile.Write(ref _logScrollPending, 0);
                if (_logViewerList is not null && ViewModel.Log.Entries.Count > 0)
                {
                    _logViewerList.ScrollIntoView(ViewModel.Log.Entries[^1]);
                }
            }, DispatcherPriority.Background);
        }
    }

    /// <summary>The chip of a language or country list that contains the element, if any.</summary>
    private static Border? FindPriorityChip(object? source) =>
        (source as Visual)?.GetSelfAndVisualAncestors().OfType<Border>().FirstOrDefault(b => b.Classes.Contains(PriorityChipClass));

    private static PriorityItemViewModel? PriorityChipItem(object? source) =>
        FindPriorityChip(source)?.DataContext as PriorityItemViewModel;

    private void OnPriorityChipPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        _pressedChipItem = null;
        var isInButton = (e.Source as Visual)?.GetSelfAndVisualAncestors().OfType<Button>().Any() == true;
        if (isInButton || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed || FindPriorityChip(e.Source) is not { } chip)
        {
            return;
        }

        chip.Focus();
        _pressedChipItem = chip.DataContext as PriorityItemViewModel;
        _pressedChipPoint = e.GetPosition(this);
    }

    /// <summary>A drag starts once the pointer has moved a few pixels with the button down.</summary>
    private async void OnPriorityChipPointerMoved(object? sender, PointerEventArgs e)
    {
        if (_pressedChipItem is not { } item || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            return;
        }

        var offset = e.GetPosition(this) - _pressedChipPoint;
        if (Math.Abs(offset.X) < PriorityChipDragThreshold && Math.Abs(offset.Y) < PriorityChipDragThreshold)
        {
            return;
        }

        _pressedChipItem = null;
        var data = new DataTransfer();
        data.Add(DataTransferItem.Create(PriorityChipDataFormat, item.Code));
        _draggedChipItem = item;
        try
        {
            await DragDrop.DoDragDropAsync(e, data, DragDropEffects.Move);
        }
        finally
        {
            _draggedChipItem = null;
        }
    }

    private (PriorityItemViewModel Dragged, PriorityItemViewModel Target)? PriorityChipDrop(DragEventArgs e) =>
        _draggedChipItem is { Owner: not null } dragged
        && e.DataTransfer.TryGetValue(PriorityChipDataFormat) == dragged.Code
        && PriorityChipItem(e.Source) is { } target
        && ReferenceEquals(dragged.Owner, target.Owner)
            ? (dragged, target)
            : null;

    private void OnPriorityChipDragOver(object? sender, DragEventArgs e)
    {
        if (e.DataTransfer.Contains(PriorityChipDataFormat))
        {
            e.DragEffects = PriorityChipDrop(e) is null ? DragDropEffects.None : DragDropEffects.Move;
            e.Handled = true;
        }
    }

    /// <summary>A chip dropped on another chip of the same list takes its place.</summary>
    private void OnPriorityChipDrop(object? sender, DragEventArgs e)
    {
        if (PriorityChipDrop(e) is not var (dragged, target))
        {
            return;
        }

        var list = dragged.Owner!;
        list.MovePriorityItem(list.PriorityItems.IndexOf(dragged), list.PriorityItems.IndexOf(target));
        e.Handled = true;
    }

    /// <summary>
    /// A count of decided games in the order of the 1G1R criteria shows those games in the results (its command):
    /// the panel of the criteria closes, since the page it opened from is no longer shown. The click is raised before
    /// the command runs, and a button closed with its panel no longer runs it: the panel closes once the click is over.
    /// </summary>
    private void OnDecidedGamesClick(object? sender, RoutedEventArgs e) =>
        Dispatcher.UIThread.Post(() => this.FindControl<Button>("BtnTiebreakOrder")?.Flyout?.Hide());

    /// <summary>Keyboard reordering of the focused chip: Delete removes it, Ctrl+arrows move it, Ctrl+Home puts it first.</summary>
    private void OnPriorityChipKeyDown(object? sender, KeyEventArgs e)
    {
        if (PriorityChipItem(e.Source) is not { } item)
        {
            return;
        }

        var control = e.KeyModifiers.HasFlag(KeyModifiers.Control);
        if (e.Key is Key.Delete or Key.Back)
        {
            item.RemoveCommand.Execute(null);
        }
        else if (control && e.Key is Key.Left or Key.Up)
        {
            item.MoveUpCommand.Execute(null);
        }
        else if (control && e.Key is Key.Right or Key.Down)
        {
            item.MoveDownCommand.Execute(null);
        }
        else if (control && e.Key == Key.Home)
        {
            item.MoveToTopCommand.Execute(null);
        }
        else
        {
            return;
        }

        e.Handled = true;
    }

    /// <summary>Whether the window waits for the end of a sort or an undo to close, as the user confirmed it.</summary>
    private bool _isClosingWhenIdle;

    /// <summary>Whether the window closes at last: the sort or undo it waited for has ended.</summary>
    private bool _isIdleForClosing;

    private void OnWindowClosing(object? sender, WindowClosingEventArgs e)
    {
        // A sort or an undo ended with the process would leave the file being written: the window closes once it has ended
        if (!_isIdleForClosing && ViewModel.Execution.IsWritingToDisk)
        {
            e.Cancel = true;
            if (!_isClosingWhenIdle)
            {
                CloseWhenIdle(ask: e.CloseReason != WindowCloseReason.OSShutdown);
            }

            return;
        }

        SubscribeToLogEntries(null);

        if (ViewModel.Execution.IsRunning)
        {
            ViewModel.Execution.Stop();
        }

        if (Width > 0)
        {
            ViewModel.WindowWidth = (int)Width;
        }

        if (Height > 0)
        {
            ViewModel.WindowHeight = (int)Height;
        }

        ViewModel.SaveConfigurationAsync().GetAwaiter().GetResult();
    }

    /// <summary>
    /// Closes the window once the running sort or undo has ended, if the user confirms it (unless the system is shutting
    /// down): the sort is stopped first, and removes the file it was writing.
    /// </summary>
    private async void CloseWhenIdle(bool ask)
    {
        _isClosingWhenIdle = true;
        try
        {
            if (!await ViewModel.Execution.ConfirmCloseWhileWritingAsync(ask))
            {
                return;
            }

            await ViewModel.Execution.WhenIdleAsync();
            _isIdleForClosing = true;
            Close();
        }
        finally
        {
            _isClosingWhenIdle = false;
        }
    }
}
