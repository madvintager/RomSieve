using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using RomSieve.App.ViewModels;
using RomSieve.Core.Models;

namespace RomSieve.App.Views;

/// <summary>
/// The patches of the last run (<see cref="PatchesViewModel"/>): the choices and the new simulations happen in the window.
/// It closes by Close, Escape or the close button, never while a simulation it started runs.
/// </summary>
public partial class PatchesWindow : Window
{
    private const string OriginCrcWidthKey = "OriginCrcWidth";
    private const string OriginStatusWidthKey = "OriginStatusWidth";

    public PatchesWindow()
    {
        Resources[OriginCrcWidthKey] = double.NaN;
        Resources[OriginStatusWidthKey] = double.NaN;
        AvaloniaXamlLoader.Load(this);
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (DataContext is PatchesViewModel vm)
        {
            UpdateOriginColumnWidths(vm);
        }
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if ((change.Property == FontFamilyProperty || change.Property == FontSizeProperty)
            && DataContext is PatchesViewModel vm)
        {
            UpdateOriginColumnWidths(vm);
        }
    }

    /// <summary>
    /// Fixed column widths from the active fonts and status labels: keeps CRC32 and status left-aligned across every row
    /// of both lists, whatever the order in which Avalonia measures or recycles their containers.
    /// </summary>
    private void UpdateOriginColumnWidths(PatchesViewModel vm)
    {
        var crcFont = this.TryFindResource("CrcFontFamily", out var resource) && resource is FontFamily found ? found : FontFamily;
        var crcWidth = "0123456789ABCDEF"
            .Max(digit => MeasureTextWidth(new string(digit, 8), crcFont, FontSize));
        var statusWidth = Enum.GetValues<FileStatus>()
            .Select(PreviewItemViewModel.GetStatusLabelKey)
            .Max(key => MeasureTextWidth(vm.I18n[key], FontFamily, FontSize));
        Resources[OriginCrcWidthKey] = Math.Ceiling(crcWidth) + 1;
        Resources[OriginStatusWidthKey] = Math.Ceiling(statusWidth) + 1;
    }

    private static double MeasureTextWidth(string text, FontFamily fontFamily, double fontSize)
    {
        var sample = new TextBlock
        {
            FontFamily = fontFamily,
            FontSize = fontSize,
            Text = text
        };
        sample.Measure(Size.Infinity);
        return sample.DesiredSize.Width;
    }

    /// <summary>Ctrl+F goes to the search of a ROM, as in the table of the main window.</summary>
    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.F && e.KeyModifiers == KeyModifiers.Control
            && this.FindControl<TextBox>("EditPatchOriginSearch") is { IsEffectivelyEnabled: true, IsEffectivelyVisible: true } search)
        {
            search.Focus();
            search.SelectAll();
            e.Handled = true;
            return;
        }

        base.OnKeyDown(e);
    }

    /// <summary>A simulation the window started is waited for: its results go to the window.</summary>
    protected override void OnClosing(WindowClosingEventArgs e)
    {
        if (DataContext is PatchesViewModel { IsSimulating: true })
        {
            e.Cancel = true;
        }

        base.OnClosing(e);
    }

    private void OnCloseClick(object? sender, RoutedEventArgs e) => Close();
}
