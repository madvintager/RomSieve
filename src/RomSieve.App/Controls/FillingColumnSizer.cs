using System;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.VisualTree;

namespace RomSieve.App.Controls;

/// <summary>
/// Keeps one column of a <see cref="DataGrid"/> at least as wide as the room the other columns leave, while the user
/// can still widen it beyond: the grid then scrolls horizontally.
/// </summary>
/// <remarks>
/// A star column (<c>Width="*"</c>) fills the room, but the grid never lets it grow past the room: dragging its edge only
/// takes width from the columns on its right, so a long content stays cut until the window grows. The column is
/// therefore given in pixels, the width of the room or the width the user dragged it to, whichever is larger. The width
/// chosen by the user is kept while the window or the other columns change; narrowed back within the room, the column
/// fills the room again. The room is the width of the rows presenter, which the vertical scroll bar overlays, less the
/// other visible columns.
/// </remarks>
public sealed class FillingColumnSizer
{
    private readonly DataGrid _grid;
    private readonly DataGridColumn _column;
    private DataGridRowsPresenter? _rowsPresenter;
    private double _appliedWidth = double.NaN;

    public FillingColumnSizer(DataGrid grid, DataGridColumn column)
    {
        _grid = grid;
        _column = column;
        grid.TemplateApplied += (_, _) => _rowsPresenter = null;
        grid.LayoutUpdated += (_, _) => Update();
    }

    /// <summary>The width the user dragged the column to, beyond the room; <c>null</c> while it fills the room.</summary>
    public double? UserWidth { get; private set; }

    /// <summary>Sets the width of the column from the room and the width chosen by the user.</summary>
    internal void Update()
    {
        if (!_column.IsVisible)
        {
            return;
        }

        _rowsPresenter ??= _grid.GetVisualDescendants().OfType<DataGridRowsPresenter>().FirstOrDefault();
        // A grid on a hidden page, or not laid out yet, has no room to share
        if (_rowsPresenter is null || _rowsPresenter.Bounds.Width <= 0)
        {
            return;
        }

        var actual = _column.ActualWidth;
        var room = _rowsPresenter.Bounds.Width - _grid.Columns.Where(c => c != _column && c.IsVisible).Sum(c => c.ActualWidth);
        // A width that is not the last one set comes from the user, who dragged the edge of its header
        if (!double.IsNaN(_appliedWidth) && Math.Abs(actual - _appliedWidth) > 0.5)
        {
            UserWidth = actual > room + 0.5 ? actual : null;
        }

        var width = Math.Max(_column.MinWidth, Math.Max(room, UserWidth ?? 0));
        if (!_column.Width.IsAbsolute || Math.Abs(width - actual) > 0.5)
        {
            _column.Width = new DataGridLength(width);
        }

        _appliedWidth = _column.ActualWidth;
    }
}
