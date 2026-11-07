using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.VisualTree;
using RomSieve.App.ViewModels;
using RomSieve.App.Views;
using RomSieve.Core.Models;

namespace RomSieve.App.Tests;

/// <summary>
/// Columns of the preview table (hidden by default, columns menu, headers not trimmed), status badges and detail panel
/// of the selected row.
/// </summary>
public sealed partial class GuiTests
{
    [Fact]
    public async Task PreviewTable_ShouldHideCrcDiskAndPublisherByDefault_AndFollowTheColumnsMenu()
    {
        await RunOnUiThreadAsync(() =>
        {
            var (vm, window) = ShowFinishedRun(CreateLargeItemSet().Take(50).ToList(), streamRows: false);
            try
            {
                var grid = window.FindControl<DataGrid>("TablePreview")!;
                grid.Columns.Select(c => c.IsVisible).Should().Equal(true, false, true, false, false, true, true, true, true, true, false, false);

                var menuButton = window.FindControl<DropDownButton>("BtnColumns")!;
                ToolTip.GetTip(menuButton).Should().Be(vm.Localization.Tr("panel4.tooltip_columns"));
                menuButton.Flyout!.ShowAt(menuButton);
                Settle(window);
                var crc = window.FindControl<MenuItem>("MenuColumnCrc")!;
                var target = window.FindControl<MenuItem>("MenuColumnTarget")!;
                crc.Header.Should().Be("CRC32");
                crc.ToggleType.Should().Be(MenuItemToggleType.CheckBox);
                crc.StaysOpenOnClick.Should().BeTrue("several columns are chosen in a row");
                crc.IsChecked.Should().BeFalse();
                target.Header.Should().Be("Chemin cible calculé");
                target.IsChecked.Should().BeTrue();
                window.FindControl<MenuItem>("MenuColumnSource").Should().BeNull("the source name is always shown");
                window.FindControl<MenuItem>("MenuColumnStatus").Should().BeNull("the status is always shown");

                crc.IsChecked = true;
                target.IsChecked = false;
                Settle(window);
                vm.PreviewTable.HiddenColumnIds.Should().Equal("disk", "publisher", "target", "related", "dat");
                grid.Columns[1].IsVisible.Should().BeTrue();
                grid.Columns[8].IsVisible.Should().BeFalse();

                window.FindControl<MenuItem>("MenuColumnsReset")!.Command!.Execute(null);
                Settle(window);
                grid.Columns.Select(c => c.IsVisible).Should().Equal(true, false, true, false, false, true, true, true, true, true, false, false);
                crc.IsChecked.Should().BeFalse();
                menuButton.Flyout.Hide();

                // The headers follow the language, in the table and in the menu
                vm.UiLocale = "en";
                grid.Columns[8].Header.Should().Be("Calculated Target Path");
                vm.PreviewTable.Columns[8].Header.Should().Be("Calculated Target Path");
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public async Task PreviewTable_TargetPath_ShouldTakeTheRoomLeftByTheHiddenColumns()
    {
        await RunOnUiThreadAsync(() =>
        {
            var (vm, window) = ShowFinishedRun(CreateLargeItemSet().Take(50).ToList(), streamRows: false);
            try
            {
                var grid = window.FindControl<DataGrid>("TablePreview")!;
                var targetWidth = grid.Columns[8].ActualWidth;
                targetWidth.Should().BeGreaterThan(200);

                vm.PreviewTable.Columns[7].IsVisible = false;
                Settle(window);
                grid.Columns[8].ActualWidth.Should().BeGreaterThan(targetWidth, "the target path gets the room of a hidden column");
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public async Task PreviewTable_StatusColumn_ShouldShowAColoredBadge()
    {
        await RunOnUiThreadAsync(() =>
        {
            var (vm, window) = ShowFinishedRun(CreateLargeItemSet().Take(50).ToList(), streamRows: false);
            try
            {
                var grid = window.FindControl<DataGrid>("TablePreview")!;
                var badges = grid.GetVisualDescendants().OfType<Border>().Where(b => b.Name == "StatusBadge" && b.IsVisible).ToList();
                badges.Should().NotBeEmpty();
                foreach (var badge in badges)
                {
                    var row = (PreviewItemViewModel)badge.DataContext!;
                    var text = (TextBlock)badge.Child!;
                    text.Text.Should().Be(row.StatusLabel);
                    text.Text.Should().NotContain("(", "the reason is in the tooltip and in the detail panel");
                    ((ISolidColorBrush)badge.Background!).Color.Should().Be(PaletteColor(ThemeVariant.Light, $"Status{row.Status}BadgeBackgroundBrush"));
                    ((ISolidColorBrush)text.Foreground!).Color.Should().Be(PaletteColor(ThemeVariant.Light, $"Status{row.Status}BadgeForegroundBrush"));
                }

                badges.Select(b => ((PreviewItemViewModel)b.DataContext!).Status).Distinct().Should().HaveCountGreaterThan(1);
                grid.Columns[9].SortMemberPath.Should().Be(nameof(PreviewItemViewModel.StatusDisplay), "the status column sorts as before");
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public async Task DetailPanel_ShouldShowTheSelectedRow()
    {
        await RunOnUiThreadAsync(() =>
        {
            var items = CreateLargeItemSet().Take(50).ToList();
            items[0].StatusReason = LocalizedMessage.Create("reason.alternate_dump_excluded");
            items[0].Status = FileStatus.Excluded;
            items[0].Crc32 = "1a2b3c4d";
            items[1].Crc32 = string.Empty;
            items[1].IsWholeArchive = true;
            items[1].MameSet = new MameSetInfo("brubber", MissingRoms: 2, MissingDisks: 0, UnknownMembers: 0);
            var (vm, window) = ShowFinishedRun(items, streamRows: false);
            try
            {
                window.Height = 960;
                Settle(window);
                var panel = window.FindControl<Expander>("PreviewDetailPanel")!;
                panel.IsExpanded.Should().BeFalse("folded by default, it leaves its rows to the table");
                vm.PreviewTable.IsDetailPanelExpanded = true;
                Settle(window);
                panel.IsExpanded.Should().BeTrue();
                window.FindControl<TextBlock>("LblDetailEmpty")!.IsVisible.Should().BeTrue("no row is selected");
                window.FindControl<Grid>("GridDetail")!.IsVisible.Should().BeFalse();

                var row = vm.AllPreviewItems.Single(r => ReferenceEquals(r.Item, items[0]));
                vm.SelectedPreviewItem = row;
                Settle(window);

                window.FindControl<TextBlock>("LblDetailEmpty")!.IsVisible.Should().BeFalse();
                window.FindControl<Grid>("GridDetail")!.IsVisible.Should().BeTrue();
                window.FindControl<SelectableTextBlock>("LblDetailSourceName")!.Text.Should().Be(row.SourceName);
                window.FindControl<SelectableTextBlock>("LblDetailSourcePath")!.Text.Should().Be(items[0].SourcePath);
                window.FindControl<SelectableTextBlock>("LblDetailTarget")!.Text.Should().Be(items[0].FinalTargetPath);
                window.FindControl<SelectableTextBlock>("LblDetailReason")!.Text.Should().Be("Alternate Dump [a] exclu");
                window.FindControl<TextBlock>("LblDetailChecksumLabel")!.Text.Should().Be("CRC32");
                window.FindControl<SelectableTextBlock>("LblDetailCrc")!.Text.Should().Be("1A2B3C4D");
                window.FindControl<SelectableTextBlock>("LblDetailMameSet")!.IsVisible.Should().BeFalse("the row is not a MAME set");
                window.FindControl<SelectableTextBlock>("LblDetailChd")!.IsVisible.Should().BeFalse("the row is not a CHD");
                var badge = window.FindControl<Border>("BadgeDetailStatus")!;
                ((TextBlock)badge.Child!).Text.Should().Be("Exclu");
                ((ISolidColorBrush)badge.Background!).Color.Should().Be(PaletteColor(ThemeVariant.Light, "StatusExcludedBadgeBackgroundBrush"));

                // A MAME set has no checksum of its own: no "CRC32 -", but what the set holds
                vm.SelectedPreviewItem = vm.AllPreviewItems.Single(r => ReferenceEquals(r.Item, items[1]));
                Settle(window);
                window.FindControl<TextBlock>("LblDetailChecksumLabel")!.IsVisible.Should().BeFalse();
                window.FindControl<SelectableTextBlock>("LblDetailCrc")!.IsVisible.Should().BeFalse();
                var mameSet = window.FindControl<SelectableTextBlock>("LblDetailMameSet")!;
                mameSet.IsVisible.Should().BeTrue();
                mameSet.Text.Should().Be("brubber : incomplet (2 ROM manquante(s))");

                // The panel folds, and the table gets its room back
                var table = window.FindControl<DataGrid>("TablePreview")!;
                var tableHeight = table.Bounds.Height;
                panel.IsExpanded = false;
                Settle(window);
                vm.PreviewTable.IsDetailPanelExpanded.Should().BeFalse();
                table.Bounds.Height.Should().BeGreaterThan(tableHeight);
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public async Task RelatedFiles_ShouldShowABadgeAColumnAndTheirListInTheDetailPanel()
    {
        await RunOnUiThreadAsync(() =>
        {
            var items = CreateLargeItemSet().Take(20).ToList();
            var cue = items[0];
            cue.SourcePath = Path.ChangeExtension(cue.SourcePath, ".cue");
            foreach (var track in new[] { 1, 2 })
            {
                cue.RelatedItems.Add(new ProcessItem
                {
                    SourcePath = Path.Combine(_tempDir, "src", $"Track {track}.bin"),
                    FileSize = 2048,
                    Parsed = cue.Parsed,
                    Status = cue.Status,
                    FinalTargetPath = Path.Combine(_tempDir, "dst", "Publisher 0", $"Track {track}.bin")
                });
            }

            var (vm, window) = ShowFinishedRun(items, streamRows: false);
            try
            {
                window.Height = 960;
                vm.PreviewTable.SortPreviewByColumn(0);
                vm.PreviewTable.SortPreviewByColumn(0);
                Settle(window);
                var grid = window.FindControl<DataGrid>("TablePreview")!;

                // Badge next to the name of the rows with related files only
                var badges = grid.GetVisualDescendants().OfType<Border>().Where(b => b.Name == "RelatedBadge").ToList();
                badges.Should().NotBeEmpty();
                var shown = badges.Where(b => b.IsVisible).ToList();
                shown.Should().ContainSingle();
                ((TextBlock)shown[0].Child!).Text.Should().Be("+ 2 pistes");
                shown[0].DataContext.Should().BeOfType<PreviewItemViewModel>().Which.Item.Should().BeSameAs(cue);
                ToolTip.GetTip(shown[0]).Should().Be(vm.Localization.Tr("panel4.tooltip_related_badge"));
                ((ISolidColorBrush)shown[0].Background!).Color.Should().Be(PaletteColor(ThemeVariant.Light, "RelatedBadgeBackgroundBrush"));

                // The column, hidden by default, is declared last but shown after the disk column
                var column = grid.Columns[PreviewTableViewModel.RelatedColumnIndex];
                column.IsVisible.Should().BeFalse();
                column.DisplayIndex.Should().Be(MainWindow.RelatedColumnDisplayIndex);
                column.DisplayIndex.Should().Be(grid.Columns[3].DisplayIndex + 1);
                var menuButton = window.FindControl<DropDownButton>("BtnColumns")!;
                menuButton.Flyout!.ShowAt(menuButton);
                Settle(window);
                var menuItem = window.FindControl<MenuItem>("MenuColumnRelated")!;
                menuItem.Header.Should().Be("Associés");
                menuItem.IsChecked.Should().BeFalse();
                menuItem.IsChecked = true;
                Settle(window);
                column.IsVisible.Should().BeTrue();
                column.Header.Should().Be("Associés");
                menuButton.Flyout.Hide();

                // The detail panel lists the files with their targets
                vm.PreviewTable.IsDetailPanelExpanded = true;
                vm.SelectedPreviewItem = vm.AllPreviewItems.Single(r => ReferenceEquals(r.Item, cue));
                Settle(window);
                window.FindControl<ScrollViewer>("ScrollDetailRelated")!.IsVisible.Should().BeTrue();
                var list = window.FindControl<ItemsControl>("ListDetailRelated")!;
                list.ItemCount.Should().Be(2);
                var texts = list.GetVisualDescendants().OfType<SelectableTextBlock>().Where(t => t.IsVisible)
                    .Select(t => t.Inlines is { Count: > 0 } inlines ? string.Concat(inlines.OfType<Avalonia.Controls.Documents.Run>().Select(r => r.Text)) : t.Text)
                    .ToList();
                texts.Should().Contain("Track 1.bin (2.00 Ko)");
                texts.Should().Contain(Path.Combine("→ Publisher 0", "Track 2.bin"));

                vm.SelectedPreviewItem = vm.AllPreviewItems.First(r => !ReferenceEquals(r.Item, cue));
                Settle(window);
                window.FindControl<ScrollViewer>("ScrollDetailRelated")!.IsVisible.Should().BeFalse("the row has no related file");
            }
            finally
            {
                window.Close();
            }
        });
    }

    /// <summary>
    /// The lines of the detail panel are evenly spaced, whatever lines are hidden between them (no MAME set, no CHD
    /// here, before the line of a partial sort) and with the status badge: each line is as high as its text.
    /// </summary>
    [Fact]
    public async Task DetailPanel_Lines_ShouldBeEvenlySpaced()
    {
        await RunOnUiThreadAsync(() =>
        {
            var (vm, window) = ShowFinishedRun(CreateLargeItemSet().Take(20).ToList(), streamRows: false);
            try
            {
                // A partial sort shows the Selection line, after the MAME, CHD and related lines that stay hidden
                var rows = vm.PreviewTable.AllPreviewItems;
                vm.PartialSort.SetChecked(rows.Skip(1), false);
                vm.PreviewTable.IsDetailPanelExpanded = true;
                vm.SelectedPreviewItem = rows[0];
                Settle(window);

                // The gap from the bottom of a line (its highest content) to the top of the next one, whether a text wraps
                var grid = window.FindControl<Grid>("GridDetail")!;
                var lines = grid.Children.Where(c => c.IsVisible)
                    .GroupBy(Grid.GetRow)
                    .OrderBy(g => g.Key)
                    .Select(g => (Top: g.Min(c => BoundsIn(window, c).Top), Bottom: g.Max(c => BoundsIn(window, c).Bottom)))
                    .ToList();
                window.FindControl<SelectableTextBlock>("LblDetailSelection")!.IsEffectivelyVisible.Should().BeTrue();
                lines.Should().HaveCount(5, "name, source, target, status and selection");
                var gaps = lines.Zip(lines.Skip(1), (a, b) => b.Top - a.Bottom).ToList();
                gaps.Should().OnlyContain(gap => Math.Abs(gap - gaps[0]) <= 0.5, $"the lines are evenly spaced ({string.Join(", ", gaps)})");

                // The status badge is no higher than a line of text
                var badge = BoundsIn(window, window.FindControl<Border>("BadgeDetailStatus")!);
                badge.Height.Should().BeLessThanOrEqualTo(BoundsIn(window, window.FindControl<SelectableTextBlock>("LblDetailReason")!).Height + 1);
            }
            finally
            {
                window.Close();
            }
        });
    }

    /// <summary>
    /// The headers of the columns shown by default fit in their default width, in every language. Measured
    /// with the real fonts only (ROMSIEVE_HEADLESS_SKIA=1): the headless text stub has no real glyph widths.
    /// </summary>
    [Fact]
    public async Task PreviewTable_DefaultHeaders_ShouldNotBeTrimmed()
    {
        if (Environment.GetEnvironmentVariable("ROMSIEVE_HEADLESS_SKIA") != "1")
        {
            return;
        }

        await RunOnUiThreadAsync(() =>
        {
            var (vm, window) = ShowFinishedRun(CreateLargeItemSet().Take(20).ToList(), streamRows: false);
            try
            {
                // The default columns, then every column (the hidden ones too, once shown, in a window wide enough
                // for all of them: narrower, the table scrolls horizontally)
                var cases =
                    from columns in new[] { ((IEnumerable<string>?)null, 7, 1280.0), ([], 12, 1860.0) }
                    from locale in vm.Localization.GetAvailableLocales().Select(l => l.Code)
                    select (Hidden: columns.Item1, Count: columns.Item2, Width: columns.Item3, Locale: locale);
                foreach (var (hidden, count, width, locale) in cases)
                {
                    window.Width = width;
                    vm.PreviewTable.SetHiddenColumnIds(hidden, PreviewTableViewModel.ColumnIds);
                    vm.UiLocale = locale;
                    Settle(window);
                    var grid = window.FindControl<DataGrid>("TablePreview")!;
                    var headers = grid.GetVisualDescendants().OfType<DataGridColumnHeader>().Where(h => h.IsVisible && h.Content is string).ToList();
                    headers.Should().HaveCount(count);
                    foreach (var header in headers)
                    {
                        var text = header.GetVisualDescendants().OfType<TextBlock>().First(t => t.Text == (string)header.Content!);
                        var full = text.Bounds.Width;
                        text.Measure(Size.Infinity);
                        text.DesiredSize.Width.Should().BeLessThanOrEqualTo(full + 0.5, $"the header '{header.Content}' ({locale}) must not be trimmed");
                    }
                }
            }
            finally
            {
                window.Close();
            }
        });
    }
}
