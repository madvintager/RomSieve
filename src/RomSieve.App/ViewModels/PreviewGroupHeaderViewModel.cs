using System.Windows.Input;
using CommunityToolkit.Mvvm.Input;

namespace RomSieve.App.ViewModels;

/// <summary>
/// Header of a game in the view grouped by game of the preview table: the title of the game, its number of files,
/// the kept file and its status, and whether its winner was forced by hand. Its files follow it while it is unfolded
/// (<see cref="PreviewRowViewModel.IsExpanded"/>). The table (<see cref="PreviewTableViewModel"/>) fills it each
/// time it lays out its rows.
/// </summary>
public sealed class PreviewGroupHeaderViewModel : PreviewRowViewModel
{
    public PreviewGroupHeaderViewModel(string groupKey, Action<PreviewGroupHeaderViewModel> toggle)
    {
        GroupKey = groupKey;
        ToggleCommand = new RelayCommand(() => toggle(this));
        IsTitleBold = true;
    }

    /// <summary>
    /// Canonical game key of the files of the group (the key of the 1G1R overrides), or
    /// <see cref="PreviewTableViewModel.UnidentifiedGroupKey"/> for the files without a game.
    /// </summary>
    public string GroupKey { get; }

    /// <summary>Whether the group gathers the files without an identified game (non-conforming names).</summary>
    public bool IsUnidentified => GroupKey == PreviewTableViewModel.UnidentifiedGroupKey;

    public override bool IsGroupHeader => true;

    public override ICommand ToggleCommand { get; }

    /// <summary>Title of the game, searched by "Show only this game"; empty for the files without a game.</summary>
    public string GameTitle { get; internal set; } = string.Empty;

    /// <summary>Files of the game, shown or not, which checking the header chooses for a partial sort.</summary>
    public IReadOnlyList<PreviewItemViewModel> Files { get; internal set; } = [];

    /// <summary>Number of files of the game, shown or not.</summary>
    public int TotalCount { get; internal set; }

    /// <summary>Number of files of the game that the status chips and the search let through.</summary>
    public int VisibleCount { get; internal set; }
}
