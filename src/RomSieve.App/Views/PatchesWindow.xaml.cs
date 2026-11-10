using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using RomSieve.App.ViewModels;

namespace RomSieve.App.Views;

/// <summary>
/// The patches of the last run (<see cref="PatchesViewModel"/>): the choices and the new simulations happen in the window.
/// It closes by Close, Escape or the close button, never while a simulation it started runs.
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
