using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using RomSieve.Core.Models;

namespace RomSieve.App.ViewModels;

/// <summary>
/// Row of the simulation preview table: a file (<see cref="PreviewItemViewModel"/>) or, in the view grouped by game,
/// the header of a game (<see cref="PreviewGroupHeaderViewModel"/>). It holds what the cells of the table show.
/// </summary>
public abstract partial class PreviewRowViewModel : ViewModelBase
{
    /// <summary>Whether the row is the header of a game in the view grouped by game.</summary>
    public abstract bool IsGroupHeader { get; }

    /// <summary>Column 0: original file name (or archive member); the title of the game for a header.</summary>
    [ObservableProperty]
    private string _sourceName = string.Empty;

    /// <summary>Column 1: CRC32 checksum.</summary>
    [ObservableProperty]
    private string _crc32Text = string.Empty;

    /// <summary>Column 2: title; the kept file of the game for a header.</summary>
    [ObservableProperty]
    private string _titleDisplay = string.Empty;

    /// <summary>Column 3: Disc / Media (<c>X of Y</c>, or empty if single media).</summary>
    [ObservableProperty]
    private string _diskDisplay = string.Empty;

    /// <summary>Column 4: Publisher.</summary>
    [ObservableProperty]
    private string _publisher = string.Empty;

    /// <summary>Column 5: Date.</summary>
    [ObservableProperty]
    private string _date = string.Empty;

    /// <summary>Column 6: Effective languages in file order (e.g. <c>en, fr, de</c>).</summary>
    [ObservableProperty]
    private string _languagesDisplay = string.Empty;

    /// <summary>Column 7: Countries / Regions in file order (e.g. <c>US, EU</c>).</summary>
    [ObservableProperty]
    private string _countriesDisplay = string.Empty;

    /// <summary>Column 8: Calculated target path (relative to destination folder).</summary>
    [ObservableProperty]
    private string _targetPathDisplay = string.Empty;

    /// <summary>
    /// Column 10: number of files transferred with the file of the row (tracks, CHDs, other files of its game), empty
    /// without any; empty for a header.
    /// </summary>
    [ObservableProperty]
    private string _relatedCountText = string.Empty;

    /// <summary>Column 11: the DAT of a lot the file is sorted with; empty for a sort with a single DAT, and for a header.</summary>
    [ObservableProperty]
    private string _datDisplay = string.Empty;

    /// <summary>
    /// Badge of the files transferred with the file of the row, next to its name (<c>+ 3 tracks</c>, <c>+ 1 CHD</c>);
    /// empty without any, and for a header.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasRelatedFiles))]
    private string _relatedFilesBadge = string.Empty;

    /// <summary>Whether the row shows the badge of its related files (<see cref="RelatedFilesBadge"/>).</summary>
    public bool HasRelatedFiles => RelatedFilesBadge.Length > 0;

    /// <summary>Column 9: short status shown as a badge (Valid, Excluded, Unmatched, Duplicate, Error).</summary>
    [ObservableProperty]
    private string _statusLabel = string.Empty;

    /// <summary>Status shown by the badge, which the view turns into the color of the status column.</summary>
    [ObservableProperty]
    private FileStatus _status;

    /// <summary>Tooltip of the row.</summary>
    [ObservableProperty]
    private string _tooltipText = string.Empty;

    [ObservableProperty]
    private bool _isTitleBold;

    /// <summary>
    /// Change of the row since the previous simulation of the same source, which the view turns into a marker; for a
    /// header, the change of one of its files.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsChangedSinceLastRun))]
    private PreviewRowChange _change;

    public bool IsChangedSinceLastRun => Change != PreviewRowChange.None;

    /// <summary>
    /// Whether the 1G1R winner was chosen by hand (<c>reason.1g1r_winner_manual</c>): the file, or the header of its
    /// game, shows a "forced" badge.
    /// </summary>
    [ObservableProperty]
    private bool _isForcedWinner;

    /// <summary>
    /// Whether the row is a MAME set kept only because a kept set needs it to run (its BIOS, a device), or, for the
    /// header of a game, holds one: shows a "required" badge.
    /// </summary>
    [ObservableProperty]
    private bool _isRequiredSet;

    /// <summary>Whether the row is a file shown below the header of its game, which the view indents.</summary>
    [ObservableProperty]
    private bool _isGroupMember;

    /// <summary>Header of a game: whether its files are shown below it (the chevron of the header). Unused for a file.</summary>
    [ObservableProperty]
    private bool _isExpanded = true;

    /// <summary>Header of a game: its number of files, shown next to its title (<c>(3)</c>, or <c>(1/3)</c> when filtered).</summary>
    [ObservableProperty]
    private string _countText = string.Empty;

    /// <summary>
    /// Whether the game of the row is chosen for a partial sort (its check box). Checking a file chooses its whole game;
    /// checking a header chooses all its files. Kept by <see cref="PartialSortViewModel"/>, never saved.
    /// </summary>
    [ObservableProperty]
    private bool _isSortChecked;

    /// <summary>
    /// Whether the check box of the row is enabled: the sort would write a file of its game, which a checked game does not
    /// need (a MAME set a checked game needs is sorted with it: its box stays checked and locked).
    /// </summary>
    [ObservableProperty]
    private bool _canBeSortChecked;

    /// <summary>Tooltip of the check box: how to choose the game, or which checked game needs it.</summary>
    [ObservableProperty]
    private string? _sortCheckTooltip;

    /// <summary>Header of a game: folds or unfolds it (chevron of the header); <see langword="null"/> for a file.</summary>
    public virtual ICommand? ToggleCommand => null;
}
