using Avalonia.Controls;
using RomSieve.App.ViewModels;
using RomSieve.App.Views;
using RomSieve.Core.Models;

namespace RomSieve.App.Tests;

/// <summary>
/// The frontend in the window: its list on the line of the layout of the output step, shown and enabled whatever the
/// convention, which offers the recommended settings of the frontend chosen; the folder per disc game and the playlists
/// read as a game in the frame of the multi-disc games.
/// </summary>
public sealed partial class GuiTests
{
    [Fact]
    public async Task FrontendList_ShouldOfferTheSettingsOfTheFrontendChosen_WhateverTheConvention()
    {
        await RunOnUiThreadAsync(async () =>
        {
            var dialogs = new HeadlessDialogService();
            var vm = CreateViewModel(dialogs);
            var window = new MainWindow(vm);
            window.Show();
            try
            {
                vm.Directories.SelectedNamingConvention = NamingConvention.Mame;
                vm.Navigation.CurrentStep = WorkflowStep.Output;
                Settle(window);

                var list = window.FindControl<ComboBox>("ComboFrontend")!;
                list.IsVisible.Should().BeTrue("nothing is hidden according to the convention");
                list.IsEffectivelyEnabled.Should().BeTrue();
                list.ItemCount.Should().Be(6);
                window.FindControl<TextBlock>("LblFrontend")!.Text.Should().Be("Frontend");
                ToolTip.GetTip(list).Should().Be(vm.I18n["tab1.tooltip_frontend"]);

                list.SelectedIndex = 5;
                Settle(window);
                await vm.Directories.RecommendationTask;
                Settle(window);

                vm.Directories.Frontend.Should().Be(FrontendTarget.MiSTer);
                dialogs.ShownMessages.Should().ContainSingle(m => m.Level == "CONFIRM" && m.Title == "Réglages recommandés pour MiSTer");
                window.FindControl<CheckBox>("ChkFolderPerDiscGame")!.IsChecked.Should().BeTrue();

                var placement = window.FindControl<ComboBox>("ComboM3uPlacement")!;
                placement.ItemCount.Should().Be(4);
                vm.Directories.SelectedM3uPlacement = M3uPlacementMode.GameFolder;
                Settle(window);
                placement.SelectedIndex.Should().Be(3);
            }
            finally
            {
                window.Close();
            }
        });
    }
}
