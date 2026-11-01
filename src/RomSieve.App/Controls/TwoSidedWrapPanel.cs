using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;

namespace RomSieve.App.Controls;

/// <summary>
/// Panel of two children on one row, the first on the left and the second on the right, which puts the second on a
/// row of its own, still on the right, when both do not fit side by side.
/// </summary>
/// <remarks>
/// It also arranges its children again when one of them changed its desired size while the panel measured it. Avalonia
/// does not invalidate the arrange of a control measured again within the measure of an ancestor: the parent ignores
/// <c>ChildDesiredSizeChanged</c> while it measures, and the arrange pass then skips a panel that receives the same
/// rectangle as before. Its children keep their former place and size, so a text that grew is cut until the next
/// layout change. This happened to the statistics banner on the first simulation, whose statistics changed in the
/// layout pass that hid the progress row above it.
/// </remarks>
public class TwoSidedWrapPanel : Panel
{
    public static readonly StyledProperty<double> SpacingProperty =
        AvaloniaProperty.Register<TwoSidedWrapPanel, double>(nameof(Spacing));

    public static readonly StyledProperty<double> RowSpacingProperty =
        AvaloniaProperty.Register<TwoSidedWrapPanel, double>(nameof(RowSpacing));

    static TwoSidedWrapPanel()
    {
        AffectsMeasure<TwoSidedWrapPanel>(SpacingProperty, RowSpacingProperty);
    }

    /// <summary>Smallest gap between the two children on one row.</summary>
    public double Spacing
    {
        get => GetValue(SpacingProperty);
        set => SetValue(SpacingProperty, value);
    }

    /// <summary>Gap between the two rows when the second child is on a row of its own.</summary>
    public double RowSpacing
    {
        get => GetValue(RowSpacingProperty);
        set => SetValue(RowSpacingProperty, value);
    }

    /// <summary>Whether the second child is on a row of its own, as last arranged.</summary>
    public bool IsWrapped { get; private set; }

    protected override Size MeasureOverride(Size availableSize)
    {
        var previousSizes = Children.Select(c => c.DesiredSize).ToList();
        foreach (var child in Children)
        {
            child.Measure(availableSize);
        }

        if (Children.Where((c, i) => c.DesiredSize != previousSizes[i]).Any())
        {
            InvalidateArrange();
        }

        var (first, second) = DesiredSizes();
        return FitsOnOneRow(availableSize.Width)
            ? new Size(first.Width + Spacing + second.Width, Math.Max(first.Height, second.Height))
            : new Size(Math.Max(first.Width, second.Width), first.Height + RowSpacing + second.Height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var (first, second) = DesiredSizes();
        IsWrapped = !FitsOnOneRow(finalSize.Width);
        if (IsWrapped)
        {
            Arrange(0, 0, first.Height, finalSize.Width, HorizontalAlignment.Left);
            Arrange(1, first.Height + RowSpacing, second.Height, finalSize.Width, HorizontalAlignment.Right);
        }
        else
        {
            Arrange(0, 0, finalSize.Height, finalSize.Width, HorizontalAlignment.Left);
            Arrange(1, 0, finalSize.Height, finalSize.Width, HorizontalAlignment.Right);
        }

        return finalSize;
    }

    /// <summary>Whether both children fit side by side (a hidden one takes no room).</summary>
    private bool FitsOnOneRow(double width)
    {
        var (first, second) = DesiredSizes();
        return first.Width == 0 || second.Width == 0 || first.Width + Spacing + second.Width <= width;
    }

    private (Size First, Size Second) DesiredSizes() =>
        (Children.Count > 0 ? Children[0].DesiredSize : default, Children.Count > 1 ? Children[1].DesiredSize : default);

    /// <summary>Arranges a child at its desired width on the left or on the right, centered in its row.</summary>
    private void Arrange(int index, double top, double rowHeight, double width, HorizontalAlignment side)
    {
        if (index >= Children.Count)
        {
            return;
        }

        var child = Children[index];
        var size = child.DesiredSize;
        var childWidth = Math.Min(size.Width, width);
        var left = side == HorizontalAlignment.Right ? width - childWidth : 0;
        child.Arrange(new Rect(left, top + Math.Max(0, (rowHeight - size.Height) / 2), childWidth, size.Height));
    }
}
