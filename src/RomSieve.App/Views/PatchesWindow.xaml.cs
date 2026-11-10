using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using RomSieve.App.ViewModels;

namespace RomSieve.App.Views;

/// <summary>
/// The patches of the last run (<see cref="PatchesViewModel"/>): the renamings happen in the window, which returns true
/// for « Close and simulate again », false for Close, Escape or the close button.
/// </summary>
public partial class PatchesWindow : Window
{
    public PatchesWindow()
    {
        AvaloniaXamlLoader.Load(this);
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

    private void OnSimulateAgainClick(object? sender, RoutedEventArgs e) => Close(true);

    private void OnCloseClick(object? sender, RoutedEventArgs e) => Close(false);
}
