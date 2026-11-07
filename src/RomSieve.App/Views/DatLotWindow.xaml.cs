using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using RomSieve.App.ViewModels;

namespace RomSieve.App.Views;

/// <summary>
/// The DATs of a lot to check (<see cref="DatLotViewModel"/>): the boxes change the lot at once, and the window returns
/// true for OK, false for Cancel, Escape or the close button, after which the caller puts the boxes back.
/// </summary>
public partial class DatLotWindow : Window
{
    public DatLotWindow()
    {
        AvaloniaXamlLoader.Load(this);
        // The search first: a pack holds thousands of DATs
        Opened += (_, _) => this.FindControl<TextBox>("EditDatLotSearch")?.Focus();
    }

    private void OnOkClick(object? sender, RoutedEventArgs e) => Close(true);

    private void OnCancelClick(object? sender, RoutedEventArgs e) => Close(false);
}
