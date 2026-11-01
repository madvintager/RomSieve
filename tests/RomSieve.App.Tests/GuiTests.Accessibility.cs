using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.VisualTree;
using RomSieve.App.ViewModels;
using RomSieve.App.Views;

namespace RomSieve.App.Tests;

/// <summary>
/// Accessibility: a button that shows an icon only (a cross, an arrow) has a name for a screen reader, translated as its
/// tooltip.
/// </summary>
public sealed partial class GuiTests
{
    private static readonly string[] IconContents = ["×", "↺", "▲", "▼", "✕"];

    private static bool IsIconButton(Button button) =>
        button.Classes.Contains("field-clear") ||
        button.Classes.Contains("chip-remove") ||
        (button.Content is string text && IconContents.Contains(text));

    [Fact]
    public async Task IconButtons_ShouldHaveANameForScreenReaders()
    {
        await RunOnUiThreadAsync(() =>
        {
            var vm = CreateViewModel();
            var window = new MainWindow(vm);
            window.Show();
            try
            {
                var iconButtons = new List<Button>();
                foreach (var step in Enum.GetValues<WorkflowStep>())
                {
                    vm.Navigation.CurrentStep = step;
                    Settle(window);
                    iconButtons.AddRange(window.GetVisualDescendants().OfType<Button>().Where(IsIconButton));
                }

                iconButtons.Should().HaveCountGreaterThan(5, "the fields, the chips and the priority lists have icon buttons");
                foreach (var button in iconButtons.Distinct())
                {
                    AutomationProperties.GetName(button).Should().NotBeNullOrWhiteSpace($"{button.Name ?? button.Content} is named");
                    AutomationProperties.GetName(button).Should().Be(ToolTip.GetTip(button)?.ToString(), "the name is the tooltip");
                }

                AutomationProperties.GetName(window.FindControl<Button>("BtnClearSource")!)
                    .Should().Be(vm.Localization.Tr("tab1.tooltip_clear_source"));
            }
            finally
            {
                window.Close();
            }
        });
    }
}
