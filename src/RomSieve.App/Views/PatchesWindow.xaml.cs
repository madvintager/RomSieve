using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using RomSieve.App.ViewModels;

namespace RomSieve.App.Views;

/// <summary>
/// The patches of the last run (<see cref="PatchesViewModel"/>): the renamings happen in the window, which returns true
/// for « Simulate again », false for Close, Escape or the close button.
/// </summary>
public partial class PatchesWindow : Window
{
    public PatchesWindow()
    {
        AvaloniaXamlLoader.Load(this);
    }

    private void OnSimulateAgainClick(object? sender, RoutedEventArgs e) => Close(true);

    private void OnCloseClick(object? sender, RoutedEventArgs e) => Close(false);

    // A double click on a file found takes it as the origin, as the button does
    private void OnSearchResultDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (DataContext is PatchesViewModel { ChooseSearchResultCommand: var choose } && choose.CanExecute(null))
        {
            choose.Execute(null);
        }
    }
}
