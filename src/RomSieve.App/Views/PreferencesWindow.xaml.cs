using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using RomSieve.App.ViewModels;

namespace RomSieve.App.Views;

/// <summary>
/// Preferences window (<see cref="PreferencesViewModel"/>): its changes apply at once, so it only needs to be closed.
/// </summary>
public partial class PreferencesWindow : Window
{
    public PreferencesWindow()
    {
        AvaloniaXamlLoader.Load(this);
    }

    private void OnCloseClick(object? sender, RoutedEventArgs e) => Close();
}
